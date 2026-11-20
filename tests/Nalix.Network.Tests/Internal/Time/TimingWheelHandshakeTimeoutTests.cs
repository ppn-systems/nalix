// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nalix.Abstractions;
using Nalix.Abstractions.Identity;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Security;
using Nalix.Environment.Configuration;
using Nalix.Environment.Time;
using Nalix.Framework.Identifiers;
using Nalix.Framework.Memory.Objects;
using Nalix.Network.Internal.Time;
using Nalix.Network.Internal.Transport;
using Nalix.Network.Options;
using Nalix.Network.Tests.TestSupport;
using Xunit;

namespace Nalix.Network.Tests.Internal.Time;

/// <summary>
/// Verifies the pre-authentication deadline enforced by <see cref="TimingWheel"/>: a connection that has not
/// reached <see cref="PermissionLevel.ESTABLISHED"/> is closed a bounded time after accept, even when it keeps
/// sending frames. Uses short tick and deadline values so the suite stays fast.
/// </summary>
[Collection(TestCollections.NetworkConfig)]
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
public sealed class TimingWheelHandshakeTimeoutTests
{
    private const int TickMs = 100;
    private const int HandshakeMs = 1000;

    private sealed class CloseArgs(IConnection connection) : IConnectionEventArgs
    {
        public IConnection Connection { get; } = connection;
        public IBufferLease? Lease => null;
        public INetworkEndpoint? NetworkEndpoint => null;
        public void Dispose() { }
    }

    /// <summary>
    /// Minimal tracked connection. <see cref="Disconnect"/> raises <see cref="ConnectionClosed"/> once and records
    /// the close time, mirroring what the real connections do.
    /// </summary>
    private sealed class PreAuthConnection(bool appliesHandshake) : TimingWheel.ITimeoutTrackedConnection
    {
        private int _closed;
        private int _closeEvents;

        public int CloseEvents => Volatile.Read(ref _closeEvents);
        public long ClosedAtMs { get; private set; }
        public bool Disconnected => Volatile.Read(ref _closed) != 0;

        public int IdleTimeoutMs { get; set; } = 60_000;
        public int TimeoutVersion { get; set; }
        public bool IsRegisteredInWheel { get; set; }
        public TimingWheel.TimeoutTask? TimeoutTask { get; set; }
        public long AcceptedAtMs { get; set; }
        public bool AppliesHandshakeTimeout { get; } = appliesHandshake;

        public bool IsDisposed => false;
        public ulong ConnectionId { get; } = Snowflake.NewId(SnowflakeType.Session).ToUInt64();
        public string? UserId { get; set; }
        public INetworkEndpoint NetworkEndpoint { get; } = SocketEndpoint.FromIpAddress(IPAddress.Loopback);
        public IOpCodeExtractor PacketClassifier => null!;
        public long UpTime => 0;
        public long LastPingTime { get; set; } = Clock.UnixMillisecondsNow();
        public bool ExcludeFromIdleTimeout { get; set; }
        public IObjectMap<AttributeKey, object> Attributes { get; } = ObjectMap<AttributeKey, object>.Rent();
        public ConcurrentDictionary<ushort, object> RateLimitCache { get; } = new();
        public int ErrorCount => 0;
        public PermissionLevel Level { get; set; } = PermissionLevel.NONE;
        public CipherSuiteType Algorithm { get; set; }
        public Bytes32 Secret { get; set; }
        public long BytesSent => 0;
        public long BytesReceived => 0;
        public long PacketsDropped => 0;
        public IConnection.ITransport Transport => throw new NotSupportedException();

        public event EventHandler<IConnectionEventArgs>? ConnectionClosed;
        public event EventHandler<IConnectionEventArgs>? MessageProcessed { add { } remove { } }
        public event EventHandler<IConnectionEventArgs>? MessageProcessing { add { } remove { } }

        public void Disconnect(string? reason = null)
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0)
            {
                return;
            }

            ClosedAtMs = Clock.UnixMillisecondsNow();
            _ = Interlocked.Increment(ref _closeEvents);
            ConnectionClosed?.Invoke(this, new CloseArgs(this));
        }

        public void Dispose() { }
        public void IncrementBytesReceived(int bytes) { }
        public void IncrementBytesSent(int bytes) { }
        public void IncrementErrorCount() { }
        public void IncrementPacketsDropped() { }
        public void UpdateIdleTimeout(int newTimeoutMs) { }
    }

    private static void ConfigureWheel(int handshakeTimeoutMs)
    {
        TimingWheelOptions options = ConfigurationManager.Instance.Get<TimingWheelOptions>();
        options.TickDuration = TickMs;
        options.HandshakeTimeoutMs = handshakeTimeoutMs;
    }

    private static void RestoreWheel(int tick, int handshake)
    {
        TimingWheelOptions options = ConfigurationManager.Instance.Get<TimingWheelOptions>();
        options.TickDuration = tick;
        options.HandshakeTimeoutMs = handshake;
    }

    /// <summary>Keeps <paramref name="connection"/> sending frames (updating LastPingTime) until it is closed or the window ends.</summary>
    private static Task FeedFramesAsync(PreAuthConnection connection, int durationMs)
    {
        return Task.Run(async () =>
        {
            long end = Clock.UnixMillisecondsNow() + durationMs;
            while (!connection.Disconnected && Clock.UnixMillisecondsNow() < end)
            {
                connection.LastPingTime = Clock.UnixMillisecondsNow();
                await Task.Delay(TickMs / 2);
            }
        });
    }

    private static async Task WaitUntilClosedAsync(PreAuthConnection connection, int maxWaitMs)
    {
        long deadline = Clock.UnixMillisecondsNow() + maxWaitMs;
        while (!connection.Disconnected && Clock.UnixMillisecondsNow() < deadline)
        {
            await Task.Delay(25);
        }
    }

    [Fact]
    public async Task TimingWheel_WhenPreAuthConnectionSendsFrames_DisconnectsAtHandshakeDeadline()
    {
        TimingWheelOptions opts = ConfigurationManager.Instance.Get<TimingWheelOptions>();
        int oldTick = opts.TickDuration, oldHandshake = opts.HandshakeTimeoutMs;
        ConfigureWheel(HandshakeMs);
        try
        {
            using TimingWheel wheel = new();
            PreAuthConnection conn = new(appliesHandshake: true);
            wheel.Activate();
            try
            {
                wheel.Register(conn);
                Task feeder = FeedFramesAsync(conn, 10_000);

                await WaitUntilClosedAsync(conn, 6_000);
                await feeder;

                conn.Disconnected.Should().BeTrue("frames must not extend the pre-authentication deadline");
                (conn.ClosedAtMs - conn.AcceptedAtMs).Should().BeGreaterThanOrEqualTo(HandshakeMs - TickMs,
                    "the connection must not be closed before the deadline");
                (conn.ClosedAtMs - conn.AcceptedAtMs).Should().BeLessThan(HandshakeMs + 3 * TickMs,
                    "the connection must be closed at the deadline, not at the 60 s idle timeout");
                conn.CloseEvents.Should().Be(1);
                conn.IsRegisteredInWheel.Should().BeFalse("the slot must be released by the wheel on close");
                wheel.GetStatistics().RegisteredCount.Should().Be(0);
            }
            finally
            {
                wheel.Deactivate();
            }
        }
        finally
        {
            RestoreWheel(oldTick, oldHandshake);
        }
    }

    [Fact]
    public async Task TimingWheel_WhenConnectionEstablishedBeforeDeadline_StaysOpen()
    {
        TimingWheelOptions opts = ConfigurationManager.Instance.Get<TimingWheelOptions>();
        int oldTick = opts.TickDuration, oldHandshake = opts.HandshakeTimeoutMs;
        ConfigureWheel(HandshakeMs);
        try
        {
            using TimingWheel wheel = new();
            PreAuthConnection conn = new(appliesHandshake: true);
            wheel.Activate();
            try
            {
                wheel.Register(conn);
                Task feeder = FeedFramesAsync(conn, 2_500);

                await Task.Delay(TickMs * 2);
                conn.Level = PermissionLevel.ESTABLISHED; // handshake completed before the deadline

                await feeder;
                await Task.Delay(TickMs * 2);

                conn.Disconnected.Should().BeFalse("an authenticated connection is governed by the idle timeout only");
                conn.IsRegisteredInWheel.Should().BeTrue();
            }
            finally
            {
                wheel.Unregister(conn);
                wheel.Deactivate();
            }
        }
        finally
        {
            RestoreWheel(oldTick, oldHandshake);
        }
    }

    [Fact]
    public async Task TimingWheel_WhenProofOfWorkGrantPresent_ExtendsDeadlineOnce()
    {
        TimingWheelOptions opts = ConfigurationManager.Instance.Get<TimingWheelOptions>();
        int oldTick = opts.TickDuration, oldHandshake = opts.HandshakeTimeoutMs;
        ConfigureWheel(HandshakeMs);
        try
        {
            using TimingWheel wheel = new();
            PreAuthConnection conn = new(appliesHandshake: true);
            const int grantMs = 2_000;
            conn.Attributes[ConnectionAttributes.HandshakeDeadlineExtension] = grantMs;
            conn.Level = PermissionLevel.POW_VERIFIED;

            wheel.Activate();
            try
            {
                wheel.Register(conn);
                Task feeder = FeedFramesAsync(conn, 6_000);

                await Task.Delay(HandshakeMs + TickMs);
                conn.Disconnected.Should().BeFalse("the PoW grant must keep the connection open past the base deadline");

                await WaitUntilClosedAsync(conn, 6_000);
                await feeder;

                conn.Disconnected.Should().BeTrue();
                (conn.ClosedAtMs - conn.AcceptedAtMs).Should().BeGreaterThanOrEqualTo(HandshakeMs + grantMs - TickMs,
                    "the extension is added to the base deadline");
                (conn.ClosedAtMs - conn.AcceptedAtMs).Should().BeLessThan(HandshakeMs + grantMs + 3 * TickMs);
            }
            finally
            {
                wheel.Deactivate();
            }
        }
        finally
        {
            RestoreWheel(oldTick, oldHandshake);
        }
    }

    [Fact]
    public async Task TimingWheel_WhenDeadlineNotApplicable_StaysOpen()
    {
        TimingWheelOptions opts = ConfigurationManager.Instance.Get<TimingWheelOptions>();
        int oldTick = opts.TickDuration, oldHandshake = opts.HandshakeTimeoutMs;
        ConfigureWheel(HandshakeMs);
        try
        {
            using TimingWheel wheel = new();
            PreAuthConnection passthroughLike = new(appliesHandshake: false);
            wheel.Activate();
            try
            {
                wheel.Register(passthroughLike);
                Task feeder = FeedFramesAsync(passthroughLike, 2_500);
                await feeder;

                passthroughLike.Disconnected.Should().BeFalse("connections without a Nalix handshake are not subject to the deadline");
            }
            finally
            {
                wheel.Unregister(passthroughLike);
                wheel.Deactivate();
            }
        }
        finally
        {
            RestoreWheel(oldTick, oldHandshake);
        }
    }

    [Fact]
    public async Task TimingWheel_WhenHandshakeTimeoutDisabled_StaysOpen()
    {
        TimingWheelOptions opts = ConfigurationManager.Instance.Get<TimingWheelOptions>();
        int oldTick = opts.TickDuration, oldHandshake = opts.HandshakeTimeoutMs;
        ConfigureWheel(0);
        try
        {
            using TimingWheel wheel = new();
            PreAuthConnection conn = new(appliesHandshake: true);
            wheel.Activate();
            try
            {
                wheel.Register(conn);
                Task feeder = FeedFramesAsync(conn, 2_500);
                await feeder;

                conn.Disconnected.Should().BeFalse("HandshakeTimeoutMs = 0 disables the pre-authentication deadline");
            }
            finally
            {
                wheel.Unregister(conn);
                wheel.Deactivate();
            }
        }
        finally
        {
            RestoreWheel(oldTick, oldHandshake);
        }
    }
}
