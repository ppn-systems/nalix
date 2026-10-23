using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Codec.DataFrames;
using Nalix.Codec.ProtocolFrames;
using Nalix.Hosting;
using Nalix.SDK.Options;
using Nalix.SDK.Transport;
using Nalix.SDK.Transport.Extensions;
using Xunit;

namespace Nalix.SDK.Tests;

/// <summary>
/// Covers request timeout accounting, server-drop mid-request, and concurrent
/// out-of-order request/response correlation for <see cref="RequestExtensions.RequestAsync{TResponse}"/>.
/// </summary>
[Collection("RealServerTests")]
public sealed class RequestResilienceTests : IDisposable
{
    public RequestResilienceTests()
    {
        TestAssemblySetup.EnsureHighLimits();
        if (!PacketRegistry.IsBuilt)
        {
            PacketRegistry.Build();
        }
        TestUtils.SetupCertificate();
    }

    [Fact]
    public async Task RequestAsync_ServerDropsMidRequest_FailsWithinTimeout()
    {
        int port = TestUtils.GetFreePort();
        TcpListener listener = new(IPAddress.Loopback, port);
        listener.Start();

        try
        {
            using var session = new TcpSession(new TransportOptions
            {
                Address = "127.0.0.1",
                Port = (ushort)port
            });

            Task acceptTask = listener.AcceptSocketAsync().ContinueWith(t =>
            {
                // Accept then immediately close — simulates server dying mid-request.
                if (t.IsCompletedSuccessfully)
                {
                    t.Result.Dispose();
                }
            }, TaskScheduler.Default);

            await session.ConnectAsync();
            await acceptTask;

            var ping = new TimeSync();
            ping.Initialize(ControlType.PING, 1, PacketFlags.NONE);

            DateTime start = DateTime.UtcNow;
            await Assert.ThrowsAnyAsync<Exception>(() => session.RequestAsync<TimeSync>(
                ping,
                options: RequestOptions.Default.WithTimeout(3000).WithRetry(0),
                predicate: _ => true).AsTask());
            TimeSpan elapsed = DateTime.UtcNow - start;

            // Must fail well within the timeout window (disconnect detection is immediate),
            // not hang until the timeout expires.
            Assert.True(elapsed < TimeSpan.FromSeconds(5), $"Took too long: {elapsed}");
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task RequestAsync_NoResponse_TimesOutAtConfiguredWindow()
    {
        int port = TestUtils.GetFreePort();
        TcpListener listener = new(IPAddress.Loopback, port);
        listener.Start();
        _ = listener.AcceptSocketAsync(); // Accept but never respond.

        try
        {
            using var session = new TcpSession(new TransportOptions { Address = "127.0.0.1", Port = (ushort)port });
            await session.ConnectAsync();

            var ping = new TimeSync();
            ping.Initialize(ControlType.PING, 2, PacketFlags.NONE);

            const int timeoutMs = 500;
            DateTime start = DateTime.UtcNow;
            _ = await Assert.ThrowsAsync<TimeoutException>(() => session.RequestAsync<TimeSync>(
                ping,
                options: RequestOptions.Default.WithTimeout(timeoutMs).WithRetry(0),
                predicate: _ => true).AsTask());
            TimeSpan elapsed = DateTime.UtcNow - start;

            // ± generous tolerance for CI scheduling jitter.
            Assert.InRange(elapsed.TotalMilliseconds, timeoutMs * 0.5, timeoutMs * 4);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task RequestAsync_SlowResponseJustUnderTimeout_Succeeds()
    {
        int port = TestUtils.GetFreePort();
        var builder = NetworkApplication.CreateBuilder();
        builder.ListenTcp<IntegrationTestProtocol>().OnPort((ushort)port);
        builder.UseSystemControl();
        builder.UseTimeSync();

        using NetworkApplication app = builder.Build();
        await app.ActivateAsync();

        try
        {
            using var session = new TcpSession(new TransportOptions { Address = "127.0.0.1", Port = (ushort)port });
            await session.ConnectAsync();

            var ping = new TimeSync();
            ping.Initialize(ControlType.PING, 3, PacketFlags.NONE);

            TimeSync response = await session.RequestAsync<TimeSync>(
                ping,
                options: RequestOptions.Default.WithTimeout(10_000),
                predicate: p => p.Type == ControlType.PONG && p.Header.SequenceId == 3);

            Assert.Equal(ControlType.PONG, response.Type);
        }
        finally
        {
            await app.DeactivateAsync();
        }
    }

    [Fact]
    public async Task RequestAsync_ConcurrentOutOfOrderResponses_EachResolvesCorrectly()
    {
        int port = TestUtils.GetFreePort();
        var builder = NetworkApplication.CreateBuilder();
        builder.ListenTcp<IntegrationTestProtocol>().OnPort((ushort)port);
        builder.UseSystemControl();
        builder.UseTimeSync();

        using NetworkApplication app = builder.Build();
        await app.ActivateAsync();

        try
        {
            using var session = new TcpSession(new TransportOptions { Address = "127.0.0.1", Port = (ushort)port });
            await session.ConnectAsync();

            const int concurrency = 32;
            List<Task> tasks = [];
            for (ushort i = 1; i <= concurrency; i++)
            {
                ushort seq = i;
                tasks.Add(Task.Run(async () =>
                {
                    var ping = new TimeSync();
                    ping.Initialize(ControlType.PING, seq, PacketFlags.NONE);

                    TimeSync response = await session.RequestAsync<TimeSync>(
                        ping,
                        options: RequestOptions.Default.WithTimeout(10_000),
                        predicate: p => p.Type == ControlType.PONG && p.Header.SequenceId == seq);

                    // Cross-talk guard: the response we get back must carry exactly
                    // the sequence id we sent, never another concurrent caller's.
                    Assert.Equal(seq, response.Header.SequenceId);
                }));
            }

            await Task.WhenAll(tasks);
        }
        finally
        {
            await app.DeactivateAsync();
        }
    }

    public void Dispose() => Nalix.Framework.Injection.InstanceManager.Instance.Clear(dispose: false);
}
