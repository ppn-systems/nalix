using System.Diagnostics;
using System.Net;
using Nalix.Hosting;
using Nalix.SDK.Options;
using Nalix.SDK.Transport;
using Nalix.SDK.Transport.Extensions;

namespace Nalix.Network.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DispatchChurnSerialGroup
{
    public const string Name = "Dispatch churn serial";
}

/// <summary>
/// End-to-end regression guard for the dispatch busy-spin fixed in #372.
/// </summary>
/// <remarks>
/// The spin was not reachable from an idle server: it needed ready-queue / per-connection
/// counters to go phantom, which only happens when clients disconnect <i>while</i> their
/// packets are being dispatched. Each phantom entry kept <c>HasClaimableConnection</c> true,
/// so workers looped claim-fail/skip-park and burned whole cores, starving the clients that
/// were still sending. This test churns clients mid-traffic for several rounds and checks
/// that (a) per-round throughput does not collapse and (b) the process goes back to ~0 CPU once
/// every client is gone. On the pre-#372 code (b) fails reliably: phantom ready entries keep
/// every worker spinning at ~3 cores on a 4-core machine long after the last client left.
/// </remarks>
[Collection(DispatchChurnSerialGroup.Name)]
public sealed class DispatchChurnRegressionTests(ITestOutputHelper output)
{
    private const int Rounds = 5;
    private const int ClientsPerRound = 32;
    private const int InFlightPerClient = 4;
    private static readonly TimeSpan RoundWindow = TimeSpan.FromMilliseconds(1500);

    [Fact]
    public async Task Dispatch_ClientsDisconnectMidTraffic_ThroughputAndCpuStayBounded()
    {
        string certPath = Path.GetFullPath("test_certificate_churn.private");
        if (!File.Exists(certPath))
        {
            File.WriteAllText(certPath, new string('a', 64));
        }

        int port = GetFreePort();

        NetworkApplicationBuilder builder = NetworkApplication.CreateBuilder();
        builder.UseSecureConnections(certPath);

        // Other tests in this assembly clear InstanceManager and register their own pool manager;
        // pin the process-wide one so Build/Activate does not trip over a mismatched instance.
        builder.UseObjectPoolManager(Nalix.Framework.Memory.Objects.ObjectPoolManager.Shared);
        builder.ListenTcp<IntegrationTestProtocol>().OnPort((ushort)port);
        builder.MapHandlers<IntegrationTestController2>();

        using NetworkApplication app = builder.Build();
        await app.ActivateAsync();

        using Process self = Process.GetCurrentProcess();
        long[] completedPerRound = new long[Rounds];
        double[] cpuMsPerRequest = new double[Rounds];

        try
        {
            for (int round = 0; round < Rounds; round++)
            {
                self.Refresh();
                TimeSpan cpu0 = self.TotalProcessorTime;
                System.Collections.Concurrent.ConcurrentQueue<string> errors = new();
                completedPerRound[round] = await RunChurnRoundAsync(port, errors).WaitAsync(TimeSpan.FromSeconds(30));
                self.Refresh();
                double cpuMs = (self.TotalProcessorTime - cpu0).TotalMilliseconds;
                cpuMsPerRequest[round] = cpuMs / Math.Max(1, completedPerRound[round]);

                output.WriteLine(
                    $"round {round}: completed={completedPerRound[round]} cpu={cpuMs:F0} ms " +
                    $"cpu/req={cpuMsPerRequest[round]:F3} ms errors={errors.Count} " +
                    $"first={(errors.TryPeek(out string? e) ? e : "-")}");
            }

            // Every client is gone now: the server must park.
            await Task.Delay(TimeSpan.FromMilliseconds(1000));
            self.Refresh();
            TimeSpan idle0 = self.TotalProcessorTime;
            Stopwatch sw = Stopwatch.StartNew();
            await Task.Delay(TimeSpan.FromMilliseconds(1500));
            self.Refresh();
            double idleCores = (self.TotalProcessorTime - idle0).TotalMilliseconds / sw.Elapsed.TotalMilliseconds;
            output.WriteLine($"idle after churn: {idleCores:F2} cores");

            long first = completedPerRound[0];
            Assert.True(first > 0, "first round produced no round trips");

            for (int round = 1; round < Rounds; round++)
            {
                Assert.True(
                    completedPerRound[round] >= first * 0.30,
                    $"round {round} throughput collapsed: {completedPerRound[round]} vs {first} in round 0 " +
                    $"(all rounds: {string.Join(", ", completedPerRound)}); dispatch workers are likely spinning.");
            }

            Assert.True(idleCores < 0.5, $"Idle process CPU was {idleCores:F2} cores after churn (expected ~0).");
        }
        finally
        {
            await app.DeactivateAsync();
        }
    }

    /// <summary>
    /// Connects <see cref="ClientsPerRound"/> clients, each running <see cref="InFlightPerClient"/>
    /// closed-loop request/response loops for
    /// <see cref="RoundWindow"/>. Half of them are dropped abruptly halfway through, with
    /// requests still in flight, while the other half keep going. Returns completed requests.
    /// </summary>
    private static async Task<long> RunChurnRoundAsync(int port, System.Collections.Concurrent.ConcurrentQueue<string> errors)
    {
        TcpSession[] sessions = new TcpSession[ClientsPerRound];
        for (int i = 0; i < ClientsPerRound; i++)
        {
            sessions[i] = new TcpSession(new TransportOptions
            {
                Address = "127.0.0.1",
                Port = (ushort)port,
                CompressionEnabled = false
            });
            await sessions[i].ConnectAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }

        long completed = 0;
        using CancellationTokenSource window = new(RoundWindow);

        // Several outstanding requests per client: the counter races need a producer and the
        // dispatching worker to touch the same connection concurrently.
        Task[] loops = sessions.SelectMany(s => Enumerable.Repeat(s, InFlightPerClient)).Select(s => Task.Run(async () =>
        {
            while (!window.IsCancellationRequested)
            {
                using HostingScan.HostingScanAttributedPacket req = new() { Value = 1 };
                var h = req.Header;
                h.OpCode = 9999;
                req.Header = h;
                try
                {
                    using HostingScan.HostingScanAttributedPacket resp = await s.RequestAsync<HostingScan.HostingScanAttributedPacket>(
                        req, options: RequestOptions.Default.WithTimeout(1000));
                    _ = Interlocked.Increment(ref completed);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Dropped sessions and timeouts end this client's loop.
                    errors.Enqueue(ex.GetType().Name + ": " + ex.Message);
                    return;
                }
            }
        })).ToArray();

        // Drop half the clients mid-traffic.
        await Task.Delay(RoundWindow / 2);
        for (int i = 0; i < ClientsPerRound; i += 2)
        {
            sessions[i].Dispose();
        }

        await Task.WhenAll(loops);

        for (int i = 1; i < ClientsPerRound; i += 2)
        {
            sessions[i].Dispose();
        }

        return Interlocked.Read(ref completed);
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
