// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nalix.Network.Internal.Pooling;
using Xunit;

namespace Nalix.Network.Tests.Internal.Pooling;

[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
public sealed class PooledSocketReceiveContextTests
{
    [Fact]
    public async Task ReceiveAsync_WhenCompletedAsynchronouslyTwice_ReusesValueTaskSource()
    {
        using Socket listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);

        int port = ((IPEndPoint)listener.LocalEndPoint!).Port;

        Task<Socket> acceptTask = Task.Run(() => listener.Accept());

        using Socket client = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(IPAddress.Loopback, port);

        using Socket server = await acceptTask;
        using PooledSocketReceiveContext receiveContext = new();
        receiveContext.EnsureArgsBound();

        byte[] buffer = new byte[16];
        byte[] firstPayload = [1, 2, 3, 4];
        byte[] secondPayload = [5, 6];

        ValueTask<int> firstReceive = receiveContext.ReceiveAsync(server, buffer, 0, buffer.Length);
        _ = firstReceive.IsCompleted.Should().BeFalse();

        _ = await client.SendAsync(firstPayload, SocketFlags.None);

        int firstRead = await firstReceive;
        _ = firstRead.Should().Be(firstPayload.Length);
        _ = buffer[0..firstRead].Should().Equal(firstPayload);

        ValueTask<int> secondReceive = receiveContext.ReceiveAsync(server, buffer, 0, buffer.Length);
        _ = secondReceive.IsCompleted.Should().BeFalse();

        _ = await client.SendAsync(secondPayload, SocketFlags.None);

        int secondRead = await secondReceive;
        _ = secondRead.Should().Be(secondPayload.Length);
        _ = buffer[0..secondRead].Should().Equal(secondPayload);
    }

    private sealed class HoldingSynchronizationContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _posts = new();

        public ManualResetEventSlim Posted { get; } = new(false);

        public override void Post(SendOrPostCallback d, object? state)
        {
            _posts.Enqueue((d, state));
            Posted.Set();
        }

        public void RunAll()
        {
            while (_posts.TryDequeue(out var post))
            {
                post.Callback(post.State);
            }
        }
    }

    [Fact]
    public async Task ResetForPool_WhenCompletedReceiveContinuationHasNotRunYet_DoesNotWaitForTheConsumer()
    {
        using Socket listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);

        int port = ((IPEndPoint)listener.LocalEndPoint!).Port;
        Task<Socket> acceptTask = Task.Run(() => listener.Accept());

        using Socket client = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(IPAddress.Loopback, port);

        using Socket server = await acceptTask;
        using PooledSocketReceiveContext receiveContext = new();
        receiveContext.EnsureArgsBound();

        byte[] buffer = new byte[16];
        HoldingSynchronizationContext holding = new();

        // The consumer's continuation is parked on a context that never runs it (a saturated thread pool).
        SynchronizationContext? previous = SynchronizationContext.Current;
        Task consumer;
        SynchronizationContext.SetSynchronizationContext(holding);
        try
        {
            consumer = ConsumeAsync(receiveContext, server, buffer);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        _ = await client.SendAsync(new byte[] { 1 }, SocketFlags.None);
        holding.Posted.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue("the receive completed and queued the continuation");

        long start = Stopwatch.GetTimestamp();
        receiveContext.ResetForPool();
        TimeSpan elapsed = Stopwatch.GetElapsedTime(start);

        holding.RunAll();
        await consumer;

        elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2), "the kernel operation is already complete, so teardown must not wait for the consumer's continuation");
    }

    private static async Task ConsumeAsync(PooledSocketReceiveContext context, Socket socket, byte[] buffer)
    {
        try
        {
            _ = await context.ReceiveAsync(socket, buffer, 0, buffer.Length);
        }
        catch (InvalidOperationException)
        {
            // The context was reset while the continuation was still queued.
        }
    }
}
