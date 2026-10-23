// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nalix.Abstractions;
using Nalix.Abstractions.Identity;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Security;
using Nalix.Environment.Fragments;
using Nalix.Environment.Memory;
using Nalix.Environment.Time;
using Nalix.Framework.Identifiers;
using Nalix.Framework.Memory.Objects;
using Nalix.Network.Connections;
using Nalix.Network.Internal.Time;
using Nalix.Network.Internal.Transport;
using Nalix.Network.Listeners.Udp;
using Nalix.Network.Options;
using Nalix.Network.RateLimiting;
using Nalix.Network.Tests.TestSupport;
using NSubstitute;
using Xunit;

namespace Nalix.Network.Tests.Internal.Time;

[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
public sealed class TimingWheelTests
{
    private static readonly IOpCodeExtractor s_testOpCodeExtractor = new TestOpCodeExtractor();

    private sealed class TestOpCodeExtractor : IOpCodeExtractor
    {
        public ushort Extract(ReadOnlySpan<byte> payload) =>
            payload.Length >= 2 ? BinaryPrimitives.ReadUInt16LittleEndian(payload[0..]) : (ushort)0;
    }

    [Fact]
    public async Task Register_WhenConnectionExcludedFromIdleTimeout_DecrementsRegisteredCount()
    {
        using TimingWheel wheel = new();
        IPEndPoint ep = new(IPAddress.Parse("198.51.100.11"), 9001);
        using PassthroughConnection conn = new(s_testOpCodeExtractor, ep)
        {
            ExcludeFromIdleTimeout = true,
            IdleTimeoutMs = 50
        };

        wheel.Activate();
        try
        {
            wheel.Register(conn);
            wheel.GetStatistics().RegisteredCount.Should().Be(1);

            // Wait for TimingWheel RUN_LOOP to process the bucket and evict the excluded connection
            long deadline = System.Environment.TickCount64 + 3000;
            while (conn.IsRegisteredInWheel && System.Environment.TickCount64 < deadline)
            {
                await Task.Delay(25);
            }

            conn.IsRegisteredInWheel.Should().BeFalse();
            wheel.GetStatistics().RegisteredCount.Should().Be(0, "TimingWheel must decrement _registeredCount when skipping an ExcludeFromIdleTimeout connection");
        }
        finally
        {
            wheel.Deactivate();
        }
    }

    private sealed class RaceTriggeredTimeoutConnection : TimingWheel.ITimeoutTrackedConnection
    {
        private readonly TimingWheel _wheel;
        private readonly TaskCompletionSource<bool> _unregisteredDuringLoop;
        private int _hookFired;

        public RaceTriggeredTimeoutConnection(TimingWheel wheel, TaskCompletionSource<bool> unregisteredDuringLoop)
        {
            _wheel = wheel;
            _unregisteredDuringLoop = unregisteredDuringLoop;
            ConnectionId = Snowflake.NewId(SnowflakeType.Session).ToUInt64();
            LastPingTime = Clock.UnixMillisecondsNow();
            NetworkEndpoint = SocketEndpoint.FromIpAddress(IPAddress.Loopback);
        }

        public int IdleTimeoutMs
        {
            get
            {
                // When RUN_LOOP reads IdleTimeoutMs (at line 715, after DequeueAll and after the initial version check),
                // simulate a concurrent thread calling Unregister(this) right before RUN_LOOP re-enqueues the task.
                if (System.Environment.CurrentManagedThreadId != 0 &&
                    ((TimingWheel.ITimeoutTrackedConnection)this).TimeoutTask is { Bucket: null } &&
                    Interlocked.CompareExchange(ref _hookFired, 1, 0) == 0)
                {
                    _wheel.Unregister(this);
                    _unregisteredDuringLoop.TrySetResult(true);
                    return 60_000; // Not expired yet, so RUN_LOOP enters the re-enqueue path
                }

                return 50;
            }
            set { }
        }

        public int TimeoutVersion { get; set; }
        public bool IsRegisteredInWheel { get; set; }
        public TimingWheel.TimeoutTask? TimeoutTask { get; set; }

        public bool IsDisposed => false;
        public ulong ConnectionId { get; }
        public string? UserId { get; set; }
        public INetworkEndpoint NetworkEndpoint { get; }
        public IOpCodeExtractor PacketClassifier => s_testOpCodeExtractor;
        public long UpTime => 0;
        public long LastPingTime { get; set; }
        public bool IsUdpCreated => false;
        public bool ExcludeFromIdleTimeout { get; set; }
        public IObjectMap<AttributeKey, object> Attributes => throw new NotSupportedException();
        public ConcurrentDictionary<ushort, object> RateLimitCache { get; } = new();
        public int ErrorCount => 0;
        public PermissionLevel Level { get; set; }
        public CipherSuiteType Algorithm { get; set; }
        public Bytes32 Secret { get; set; }
        public long BytesSent => 0;
        public long BytesReceived => 0;
        public long PacketsDropped => 0;
        public IConnection.ITransport TCP => throw new NotSupportedException();
        public IConnection.ITransport UDP => throw new NotSupportedException();

        public event EventHandler<IConnectionEventArgs>? ConnectionClosed { add { } remove { } }
        public event EventHandler<IConnectionEventArgs>? MessageProcessed { add { } remove { } }
        public event EventHandler<IConnectionEventArgs>? MessageProcessing { add { } remove { } }

        public void Disconnect(string? reason = null) { }
        public void Dispose() { }
        public void IncrementBytesReceived(int bytes) { }
        public void IncrementBytesSent(int bytes) { }
        public void IncrementErrorCount() { }
        public void IncrementPacketsDropped() { }
        public void UpdateIdleTimeout(int newTimeoutMs) { }
    }

    [Fact]
    public async Task Unregister_DuringRunLoopTick_DoesNotReenqueueCancelledTask()
    {
        using TimingWheel wheel = new();
        TaskCompletionSource<bool> unregisteredTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RaceTriggeredTimeoutConnection conn = new(wheel, unregisteredTcs);

        wheel.Activate();
        try
        {
            wheel.Register(conn);
            TimingWheel.TimeoutTask? capturedTask = conn.TimeoutTask;
            capturedTask.Should().NotBeNull();

            await unregisteredTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(150); // Allow RUN_LOOP to finish processing the tick

            // After concurrent Unregister, RUN_LOOP must NOT have re-enqueued the task into a bucket
            capturedTask!.Bucket.Should().BeNull("a task whose connection was unregistered mid-tick must not be re-enqueued into a wheel bucket");
            capturedTask.Conn.Should().BeNull("the stale task must be returned to the pool (ResetForPool clears Conn)");
        }
        finally
        {
            wheel.Deactivate();
        }
    }
}
