using System.Diagnostics;
using System.Net;
using Nalix.Hosting;
using Nalix.SDK.Options;
using Nalix.SDK.Transport;
using Nalix.SDK.Transport.Extensions;

namespace Nalix.Network.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DispatchIdleCpuSerialGroup
{
    public const string Name = "Dispatch idle CPU serial";
}

/// <summary>
/// Regression for the idle busy-spin introduced with the allocation-free dispatch wake (#369):
/// after a burst from many clients that then disconnect, dispatch workers must park and the
/// process must go back to (near) zero CPU.
/// </summary>
[Collection(DispatchIdleCpuSerialGroup.Name)]
public sealed class DispatchIdleCpuTests
{
    private const int Clients = 16;

    [Fact]
    public async Task DispatchWorkers_AfterBurstAndDisconnect_ProcessCpuStaysNearIdle()
    {
        string certPath = Path.GetFullPath("test_certificate_idlecpu.private");
        if (!File.Exists(certPath))
        {
            File.WriteAllText(certPath, new string('a', 64));
        }

        int port = GetFreePort();

        NetworkApplicationBuilder builder = NetworkApplication.CreateBuilder();
        builder.UseSecureConnections(certPath);
        builder.ListenTcp<IntegrationTestProtocol>().OnPort((ushort)port);
        builder.MapHandlers<IntegrationTestController2>();

        using NetworkApplication app = builder.Build();
        await app.ActivateAsync();

        try
        {
            // Burst from many clients, then drop them all (some with packets still queued).
            TcpSession[] sessions = new TcpSession[Clients];
            for (int i = 0; i < Clients; i++)
            {
                sessions[i] = new TcpSession(new TransportOptions
                {
                    Address = "127.0.0.1",
                    Port = (ushort)port,
                    CompressionEnabled = false
                });
                await sessions[i].ConnectAsync();
            }

            // Closed-loop request/response from every client (the benchmark pattern that
            // exposed the spin), then drop them all.
            long completed = 0;
            using CancellationTokenSource burst = new(TimeSpan.FromSeconds(2));
            await Task.WhenAll(sessions.Select(s => Task.Run(async () =>
            {
                while (!burst.IsCancellationRequested)
                {
                    using HostingScan.HostingScanAttributedPacket req = new() { Value = 1 };
                    var h = req.Header;
                    h.OpCode = 9999;
                    req.Header = h;
                    try
                    {
                        using HostingScan.HostingScanAttributedPacket resp = await s.RequestAsync<HostingScan.HostingScanAttributedPacket>(
                            req, options: RequestOptions.Default.WithTimeout(2000));
                        _ = Interlocked.Increment(ref completed);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        // Timeouts/throttling are irrelevant here; only the idle CPU afterwards is.
                        return;
                    }
                }
            })));

            Assert.True(Interlocked.Read(ref completed) > 0, "burst produced no round trips");

            foreach (TcpSession s in sessions)
            {
                s.Dispose();
            }

            // Let in-flight work, disconnect handling and GC settle.
            await Task.Delay(TimeSpan.FromSeconds(2));

            // Sample process CPU over an idle window. A spinning worker burns ~1 core per worker;
            // the threshold is deliberately generous (a quarter of one core) to stay robust on CI.
            using Process self = Process.GetCurrentProcess();
            TimeSpan cpu0 = self.TotalProcessorTime;
            Stopwatch sw = Stopwatch.StartNew();
            await Task.Delay(TimeSpan.FromSeconds(3));
            self.Refresh();
            double cores = (self.TotalProcessorTime - cpu0).TotalMilliseconds / sw.Elapsed.TotalMilliseconds;

            Assert.True(cores < 0.25, $"Idle process CPU was {cores:F2} cores after burst + disconnect (expected ~0).");
        }
        finally
        {
            await app.DeactivateAsync();
        }
    }

    private static int GetFreePort()
    {
        System.Net.Sockets.TcpListener l = new(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
