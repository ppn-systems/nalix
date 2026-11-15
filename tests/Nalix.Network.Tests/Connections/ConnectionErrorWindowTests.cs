// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Net;
using System.Net.Sockets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Environment.Time;
using Nalix.Network.Connections;
using Nalix.Network.Options;
using NSubstitute;
using Xunit;

namespace Nalix.Network.Tests.Connections;

/// <summary>
/// The per-connection error budget (SEC-54) counts errors within <see cref="ConnectionGuardOptions.ErrorWindow"/>.
/// Time is supplied explicitly so a long-lived connection can be simulated over hours without sleeping.
/// </summary>
public sealed class ConnectionErrorWindowTests : IDisposable
{
    private static readonly ConnectionGuardOptions s_defaults = new();

    private readonly Socket _socket;
    private readonly Connection _connection;

    public ConnectionErrorWindowTests()
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _connection = new Connection(_socket, Substitute.For<IOpCodeExtractor>(), new IPEndPoint(IPAddress.Loopback, 0));
    }

    public void Dispose()
    {
        _connection.Dispose();
        _socket.Dispose();
    }

    [Fact]
    public void IncrementErrorCount_WhenErrorsArriveSlowlyOverSimulatedHours_DoesNotDisconnect()
    {
        long windowTicks = Ticks(s_defaults.ErrorWindow);
        long spacingTicks = windowTicks + Clock.TicksPerSecond;
        long now = Clock.MonoTicksNow();

        // Three times the threshold, each error in its own window: a healthy long-lived client.
        for (int i = 0; i < s_defaults.MaxErrorThreshold * 3; i++)
        {
            _connection.IncrementErrorCount(now + (i * spacingTicks));
        }

        Assert.False(_connection.IsDisposed);
        Assert.Equal(1, _connection.ErrorCount);
    }

    [Fact]
    public void IncrementErrorCount_WhenErrorsBelowThresholdWithinWindow_DoesNotDisconnect()
    {
        long now = Clock.MonoTicksNow();

        for (int i = 0; i < s_defaults.MaxErrorThreshold - 1; i++)
        {
            _connection.IncrementErrorCount(now);
        }

        Assert.False(_connection.IsDisposed);
        Assert.Equal(s_defaults.MaxErrorThreshold - 1, _connection.ErrorCount);
    }

    [Fact]
    public void IncrementErrorCount_WhenBurstReachesThresholdWithinWindow_Disconnects()
    {
        long now = Clock.MonoTicksNow();

        for (int i = 0; i < s_defaults.MaxErrorThreshold; i++)
        {
            _connection.IncrementErrorCount(now);
        }

        Assert.True(_connection.IsDisposed);
    }

    [Fact]
    public void IncrementErrorCount_WhenBurstFollowsQuietWindow_Disconnects()
    {
        long windowTicks = Ticks(s_defaults.ErrorWindow);
        long now = Clock.MonoTicksNow();

        // A long quiet period (for example, a rekey grace burst that was already absorbed) must not reduce the
        // budget for a later burst within a fresh window.
        _connection.IncrementErrorCount(now);
        _connection.IncrementErrorCount(now + windowTicks + 1);
        Assert.False(_connection.IsDisposed);

        for (int i = 0; i < s_defaults.MaxErrorThreshold - 1; i++)
        {
            _connection.IncrementErrorCount(now + windowTicks + 1);
        }

        Assert.True(_connection.IsDisposed);
    }

    private static long Ticks(TimeSpan span) => (long)(span.TotalSeconds * Clock.TicksPerSecond);
}
