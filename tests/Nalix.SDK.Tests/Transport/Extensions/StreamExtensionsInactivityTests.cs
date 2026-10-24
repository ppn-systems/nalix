// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Codec.DataFrames;
using Nalix.Codec.ProtocolFrames;
using Nalix.Hosting;
using Nalix.SDK.Options;
using Nalix.SDK.Transport;
using Nalix.SDK.Transport.Extensions;
using Nalix.SDK.Tests.TestSupport;
using Xunit;

namespace Nalix.SDK.Tests.Transport.Extensions;

[Collection(TestCollections.RealServer)]
public sealed class StreamExtensionsInactivityTests : IDisposable
{
    public StreamExtensionsInactivityTests()
    {
        TestAssemblySetup.EnsureHighLimits();
        if (!PacketRegistry.IsBuilt)
        {
            PacketRegistry.Build();
        }
    }

    [Fact]
    public async Task StreamAsync_WhenInactivityTimeoutElapses_ThrowsTimeoutException()
    {
        int port = TestEnvironment.GetFreePort();
        TcpListener listener = new(IPAddress.Loopback, port);
        listener.Start();

        using TcpSession client = new(new TransportOptions { Address = "127.0.0.1", Port = (ushort)port });
        try
        {
            Task<Socket> acceptTask = listener.AcceptSocketAsync();
            await client.ConnectAsync();
            using Socket serverSide = await acceptTask;
            _ = serverSide; // Server never sends anything -> stream must stall.

            TimeSync request = new();
            request.Initialize(ControlType.PING, 1, PacketFlags.NONE);

            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));

            await Assert.ThrowsAsync<TimeoutException>(async () =>
            {
                await foreach (NullableStreamItem _ in client.StreamAsync<NullableStreamItem>(
                    request, ct: cts.Token, inactivityTimeoutMs: 100))
                {
                }
            });
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task StreamAsync_WhenCallerCancelsWhileInactivityTimeoutActive_ThrowsOperationCanceledExceptionPromptly()
    {
        int port = TestEnvironment.GetFreePort();
        TcpListener listener = new(IPAddress.Loopback, port);
        listener.Start();

        using TcpSession client = new(new TransportOptions { Address = "127.0.0.1", Port = (ushort)port });
        try
        {
            Task<Socket> acceptTask = listener.AcceptSocketAsync();
            await client.ConnectAsync();
            using Socket serverSide = await acceptTask;
            _ = serverSide; // Server never sends anything -> stream stalls waiting for chunks.

            TimeSync request = new();
            request.Initialize(ControlType.PING, 1, PacketFlags.NONE);

            using CancellationTokenSource cts = new();
            cts.CancelAfter(50);

            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await foreach (NullableStreamItem _ in client.StreamAsync<NullableStreamItem>(
                    request, ct: cts.Token, inactivityTimeoutMs: 10_000))
                {
                }
            });

            TimeSpan elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(start);
            Assert.True(elapsed < TimeSpan.FromSeconds(2), $"Expected prompt cancellation, but took {elapsed.TotalMilliseconds}ms.");
        }
        finally
        {
            listener.Stop();
        }
    }

    public void Dispose() => Nalix.Framework.Injection.InstanceManager.Instance.Clear(dispose: false);
}
