using Nalix.Environment.Memory;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nalix.Environment.Configuration;
using Nalix.Framework.Injection;
using Nalix.Framework.Memory.Buffers;
using Nalix.Framework.Memory.Objects;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Network.Connections;
using Xunit;
using TransportAsyncCallback = Nalix.Network.Internal.Transport.AsyncCallback;

namespace Nalix.Network.Tests;

[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
[Collection(AsyncCallbackSerialGroup.Name)]
public sealed class AsyncCallbackDispatchTests
{
    private static readonly IOpCodeExtractor s_testOpCodeExtractor = new TestOpCodeExtractor();

    private sealed class TestOpCodeExtractor : IOpCodeExtractor
    {
        public ushort Extract(System.ReadOnlySpan<byte> payload) =>
            payload.Length >= 2 ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(payload[0..]) : (ushort)0;
    }

    [Fact]
    public async Task InjectIncoming_QueuesProcessCallbackOnce_AndDoesNotUnderflowPendingPackets()
    {
        EnsureLoggerRegistered();

        using Socket listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);

        int port = ((IPEndPoint)listener.LocalEndPoint!).Port;

        Task<Socket> acceptTask = Task.Run(() => listener.Accept());

        using Socket clientSocket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await clientSocket.ConnectAsync(IPAddress.Loopback, port);

        using Socket serverSocket = await acceptTask;
        using Connection connection = new(serverSocket, s_testOpCodeExtractor);

        TaskCompletionSource processObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.MessageProcessing += (_, e) =>
        {
            if (e is null)
            {
                _ = processObserved.TrySetException(new InvalidOperationException("Process callback args were null."));
                return;
            }

            _ = e.Lease.Should().NotBeNull();
            _ = e.Lease!.Length.Should().Be(3);
            _ = processObserved.TrySetResult();
        };

        TransportAsyncCallback.ResetStatistics();

        BufferLease lease = BufferLease.CopyFrom([1, 2, 3]);
        connection.InjectIncoming(lease);

        await processObserved.Task.WaitAsync(TimeSpan.FromSeconds(15));

        // Wait for the dispatcher's finally block to release the pending packet slot.
        for (int i = 0; i < 100 && connection.PendingPackets > 0; i++)
        {
            await Task.Delay(1);
        }

        var stats = TransportAsyncCallback.GetStatistics();
        _ = stats.PendingProcess.Should().Be(0);
        _ = stats.Dropped.Should().Be(0);
        _ = stats.Total.Should().Be(1);
        _ = connection.PendingPackets.Should().Be(0);
    }

    [Fact]
    public async Task Send_PostProcessCallback_DoesNotConsumeReceivePendingSlot()
    {
        EnsureLoggerRegistered();

        using Socket listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);

        int port = ((IPEndPoint)listener.LocalEndPoint!).Port;

        Task<Socket> acceptTask = Task.Run(() => listener.Accept());

        using Socket clientSocket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await clientSocket.ConnectAsync(IPAddress.Loopback, port);

        using Socket serverSocket = await acceptTask;
        using Connection connection = new(serverSocket, s_testOpCodeExtractor);

        TaskCompletionSource postObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.MessageProcessed += (_, _) => postObserved.TrySetResult();

        TransportAsyncCallback.ResetStatistics();

        connection.TCP.Send([7, 8, 9, 10]);

        byte[] receivedFrame = new byte[32];
        int bytesRead = await clientSocket.ReceiveAsync(receivedFrame, SocketFlags.None);
        _ = bytesRead.Should().BeGreaterThan(0);

        await postObserved.Task.WaitAsync(TimeSpan.FromSeconds(15));

        // Wait for the dispatcher's finally block to release the pending packet slot.
        for (int i = 0; i < 100 && connection.PendingPackets > 0; i++)
        {
            await Task.Delay(1);
        }

        var stats = TransportAsyncCallback.GetStatistics();
        _ = stats.PendingPost.Should().Be(0);
        _ = stats.Dropped.Should().Be(0);
        _ = stats.Total.Should().Be(1);
        _ = connection.PendingPackets.Should().Be(0);
    }

    private static void EnsureLoggerRegistered()
    {
        InstanceManager.Instance.Register<ILogger>(NullLogger.Instance);
        Nalix.Framework.Options.ObjectPoolOptions poolOpts = ConfigurationManager.Instance.Get<Nalix.Framework.Options.ObjectPoolOptions>();
        poolOpts.BaseKeepPercentage = 75;
        poolOpts.DeepTrimPercentage = 25;
        poolOpts.HotHitRateThreshold = 85.0;
        poolOpts.DefaultPreallocate = 16;
        poolOpts.DefaultMaxPoolSize = 1024;
        InstanceManager.Instance.GetOrCreateInstance<BufferPoolManager>();
        InstanceManager.Instance.Register<Nalix.Abstractions.IObjectPoolManager>(ObjectPoolManager.Shared);
        InstanceManager.Instance.Register<ObjectPoolManager>(ObjectPoolManager.Shared);
    }
}
















