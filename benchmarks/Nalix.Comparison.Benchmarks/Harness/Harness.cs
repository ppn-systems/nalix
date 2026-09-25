// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

// Shared end-to-end benchmark harness. Linked into both the main comparison
// project and the MagicOnion project (MagicOnion needs MessagePack v3, which
// conflicts with SignalR's MessagePack v2 protocol, so it lives in its own exe).

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Nalix.Comparison.Benchmarks.Harness;

/// <summary>A connected benchmark client performing one echo round trip per call.</summary>
public interface IBenchClient : IAsyncDisposable
{
    Task ConnectAsync();

    /// <summary>Sends <paramref name="payload"/> and awaits the echoed response; returns response length.</summary>
    ValueTask<int> EchoAsync(byte[] payload);
}

/// <summary>A library under test: knows how to host a server and create clients.</summary>
public interface IBenchLibrary
{
    string Name { get; }

    string Description { get; }

    Task<IAsyncDisposable> StartServerAsync(int port);

    IBenchClient CreateClient(int port);
}

public sealed record BenchOptions(
    int[] Sizes,
    int[] Clients,
    int Runs,
    int LatencyWarmup,
    int LatencyIterations,
    double WarmupSeconds,
    double DurationSeconds,
    string OutFile);

public static class HarnessMain
{
    public static async Task<int> RunAsync(string[] args, IReadOnlyDictionary<string, IBenchLibrary> libs)
    {
        if (args.Length >= 3 && args[0] == "server")
        {
            return await ServerMode(libs[args[1]], int.Parse(args[2], CultureInfo.InvariantCulture));
        }

        if (args.Length >= 2 && args[0] == "bench")
        {
            BenchOptions o = ParseOptions(args);
            foreach (string name in args[1].Split(','))
            {
                await BenchLibrary(libs[name], o);
            }

            return 0;
        }

        Console.Error.WriteLine("usage: server <lib> <port> | bench <lib[,lib]> [--sizes 32,1024] [--clients 1,16,64] [--runs 3] [--lat-iters 100000] [--lat-warmup 20000] [--warmup 2] [--duration 8] [--out results.jsonl]");
        Console.Error.WriteLine("libs: " + string.Join(", ", libs.Keys));
        return 2;
    }

    private static BenchOptions ParseOptions(string[] args)
    {
        string Get(string key, string def)
        {
            int i = Array.LastIndexOf(args, key);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : def;
        }

        static int[] Ints(string s) => s.Split(',').Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        return new BenchOptions(
            Ints(Get("--sizes", "32,1024")),
            Ints(Get("--clients", "1,16,64")),
            int.Parse(Get("--runs", "3"), CultureInfo.InvariantCulture),
            int.Parse(Get("--lat-warmup", "20000"), CultureInfo.InvariantCulture),
            int.Parse(Get("--lat-iters", "100000"), CultureInfo.InvariantCulture),
            double.Parse(Get("--warmup", "2"), CultureInfo.InvariantCulture),
            double.Parse(Get("--duration", "8"), CultureInfo.InvariantCulture),
            Get("--out", "results.jsonl"));
    }

    // ---------------------------------------------------------------- server

    private static async Task<int> ServerMode(IBenchLibrary lib, int port)
    {
        await using IAsyncDisposable server = await lib.StartServerAsync(port);
        Console.Out.WriteLine("READY");
        Console.Out.Flush();

        long allocBase = 0;
        int g0 = 0, g1 = 0, g2 = 0;
        TimeSpan cpuBase = TimeSpan.Zero;
        Process self = Process.GetCurrentProcess();
        string? line;
        while ((line = await Console.In.ReadLineAsync()) is not null)
        {
            switch (line.Trim())
            {
                case "reset":
                    allocBase = GC.GetTotalAllocatedBytes(precise: true);
                    g0 = GC.CollectionCount(0);
                    g1 = GC.CollectionCount(1);
                    g2 = GC.CollectionCount(2);
                    self.Refresh();
                    cpuBase = self.TotalProcessorTime;
                    Console.Out.WriteLine("OK");
                    break;
                case "report":
                    long alloc = GC.GetTotalAllocatedBytes(precise: true) - allocBase;
                    self.Refresh();
                    double cpuMs = (self.TotalProcessorTime - cpuBase).TotalMilliseconds;
                    Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"STATS {alloc} {GC.CollectionCount(0) - g0} {GC.CollectionCount(1) - g1} {GC.CollectionCount(2) - g2} {cpuMs}"));
                    break;
                case "quit":
                    return 0;
            }

            Console.Out.Flush();
        }

        return 0;
    }

    // ---------------------------------------------------------------- driver

    private sealed class ServerProcess : IAsyncDisposable
    {
        private readonly Process _p;

        public ServerProcess(string lib, int port)
        {
            ProcessStartInfo psi = new(System.Environment.ProcessPath!)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = false,
                UseShellExecute = false,
            };
            if (psi.FileName.EndsWith("dotnet", StringComparison.Ordinal))
            {
                psi.ArgumentList.Add(typeof(HarnessMain).Assembly.Location);
            }

            psi.ArgumentList.Add("server");
            psi.ArgumentList.Add(lib);
            psi.ArgumentList.Add(port.ToString(CultureInfo.InvariantCulture));
            _p = Process.Start(psi)!;
        }

        public async Task WaitReadyAsync()
        {
            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));
            while (true)
            {
                string? l = await _p.StandardOutput.ReadLineAsync(cts.Token);
                if (l is null) throw new InvalidOperationException("server exited before READY");
                if (l.StartsWith("READY", StringComparison.Ordinal)) return;
            }
        }

        private async Task<string> CommandAsync(string cmd, string prefix)
        {
            await _p.StandardInput.WriteLineAsync(cmd);
            await _p.StandardInput.FlushAsync();
            while (true)
            {
                string? l = await _p.StandardOutput.ReadLineAsync();
                if (l is null) throw new InvalidOperationException("server died");
                if (l.StartsWith(prefix, StringComparison.Ordinal)) return l;
            }
        }

        public Task ResetAsync() => CommandAsync("reset", "OK");

        public async Task<ServerStats> ReportAsync()
        {
            string[] s = (await CommandAsync("report", "STATS")).Split(' ');
            return new ServerStats(
                long.Parse(s[1], CultureInfo.InvariantCulture),
                int.Parse(s[2], CultureInfo.InvariantCulture),
                int.Parse(s[3], CultureInfo.InvariantCulture),
                int.Parse(s[4], CultureInfo.InvariantCulture),
                double.Parse(s[5], CultureInfo.InvariantCulture));
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _p.StandardInput.WriteLineAsync("quit");
                await _p.StandardInput.FlushAsync();
                if (!_p.WaitForExit(10_000)) _p.Kill(true);
            }
            catch
            {
                try { _p.Kill(true); } catch { /* ignore */ }
            }

            _p.Dispose();
        }
    }

    private readonly record struct ServerStats(long AllocBytes, int Gen0, int Gen1, int Gen2, double CpuMs);

    private static int s_port = 41000 + (System.Environment.ProcessId % 1000) * 10;

    private static async Task BenchLibrary(IBenchLibrary lib, BenchOptions o)
    {
        int port = Interlocked.Increment(ref s_port);
        Console.WriteLine($"=== {lib.Name}: {lib.Description} (port {port})");
        await using ServerProcess server = new(lib.Name, port);
        await server.WaitReadyAsync();

        foreach (int size in o.Sizes)
        {
            byte[] payload = new byte[size];
            Random.Shared.NextBytes(payload);

            // ---- latency: single client, sequential.
            await using (IBenchClient c = lib.CreateClient(port))
            {
                await c.ConnectAsync();
                int len = await c.EchoAsync(payload);
                if (len != size) throw new InvalidOperationException($"{lib.Name}: echo length {len} != {size}");

                for (int run = 1; run <= o.Runs; run++)
                {
                    for (int i = 0; i < o.LatencyWarmup; i++) await c.EchoAsync(payload);

                    long[] samples = new long[o.LatencyIterations];
                    await server.ResetAsync();
                    long start = Stopwatch.GetTimestamp();
                    for (int i = 0; i < samples.Length; i++)
                    {
                        long t0 = Stopwatch.GetTimestamp();
                        await c.EchoAsync(payload);
                        samples[i] = Stopwatch.GetTimestamp() - t0;
                    }

                    double elapsed = Stopwatch.GetElapsedTime(start).TotalSeconds;
                    ServerStats st = await server.ReportAsync();
                    Array.Sort(samples);
                    double Us(long ticks) => ticks * 1_000_000.0 / Stopwatch.Frequency;
                    double P(double q) => Us(samples[Math.Min(samples.Length - 1, (int)(q * samples.Length))]);
                    double mean = 0;
                    foreach (long s in samples) mean += s;
                    mean = Us((long)(mean / samples.Length));

                    var rec = new
                    {
                        kind = "latency", lib = lib.Name, size, clients = 1, run,
                        iterations = samples.Length,
                        mean_us = mean, p50_us = P(0.50), p90_us = P(0.90), p99_us = P(0.99), p999_us = P(0.999), max_us = Us(samples[^1]),
                        ops_per_sec = samples.Length / elapsed,
                        server_alloc_bytes_per_op = (double)st.AllocBytes / samples.Length,
                        server_cpu_us_per_op = st.CpuMs * 1000.0 / samples.Length,
                        gen0 = st.Gen0, gen1 = st.Gen1, gen2 = st.Gen2,
                    };
                    Emit(o, rec);
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"  lat  size={size,5} run={run} mean={rec.mean_us,8:F1}us p50={rec.p50_us,8:F1} p99={rec.p99_us,8:F1} p99.9={rec.p999_us,8:F1} alloc/op={rec.server_alloc_bytes_per_op,8:F0}B cpu/op={rec.server_cpu_us_per_op,6:F1}us gc={st.Gen0}/{st.Gen1}/{st.Gen2}"));
                }
            }

            // ---- throughput: N clients, closed loop (each client has 1 request in flight).
            foreach (int n in o.Clients)
            {
                IBenchClient[] clients = new IBenchClient[n];
                for (int i = 0; i < n; i++)
                {
                    clients[i] = lib.CreateClient(port);
                    await clients[i].ConnectAsync();
                }

                try
                {
                    for (int run = 1; run <= o.Runs; run++)
                    {
                        long[] counts = new long[n];
                        int phase = 0; // 0 warmup, 1 measure, 2 stop
                        Task[] loops = new Task[n];
                        for (int i = 0; i < n; i++)
                        {
                            int idx = i;
                            loops[i] = Task.Run(async () =>
                            {
                                IBenchClient cl = clients[idx];
                                long local = 0;
                                while (true)
                                {
                                    int ph = Volatile.Read(ref phase);
                                    if (ph == 2) break;
                                    await cl.EchoAsync(payload);
                                    if (ph == 1) local++;
                                }

                                counts[idx] = local;
                            });
                        }

                        await Task.Delay(TimeSpan.FromSeconds(o.WarmupSeconds));
                        await server.ResetAsync();
                        Volatile.Write(ref phase, 1);
                        TimeSpan clientCpu0 = Process.GetCurrentProcess().TotalProcessorTime;
                        long clientAlloc0 = GC.GetTotalAllocatedBytes(false);
                        long start = Stopwatch.GetTimestamp();
                        await Task.Delay(TimeSpan.FromSeconds(o.DurationSeconds));
                        Volatile.Write(ref phase, 2);
                        double elapsed = Stopwatch.GetElapsedTime(start).TotalSeconds;
                        double clientCpuMs = (Process.GetCurrentProcess().TotalProcessorTime - clientCpu0).TotalMilliseconds;
                        long clientAlloc = GC.GetTotalAllocatedBytes(false) - clientAlloc0;
                        ServerStats st = await server.ReportAsync();
                        await Task.WhenAll(loops);
                        long total = counts.Sum();
                        var rec = new
                        {
                            kind = "throughput", lib = lib.Name, size, clients = n, run,
                            ops = total, seconds = elapsed,
                            ops_per_sec = total / elapsed,
                            server_alloc_bytes_per_op = total == 0 ? 0 : (double)st.AllocBytes / total,
                            server_cpu_us_per_op = total == 0 ? 0 : st.CpuMs * 1000.0 / total,
                            client_cpu_us_per_op = total == 0 ? 0 : clientCpuMs * 1000.0 / total,
                            client_alloc_bytes_per_op = total == 0 ? 0 : (double)clientAlloc / total,
                            gen0 = st.Gen0, gen1 = st.Gen1, gen2 = st.Gen2,
                        };
                        Emit(o, rec);
                        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                            $"  tput size={size,5} clients={n,3} run={run} ops/s={rec.ops_per_sec,10:F0} alloc/op={rec.server_alloc_bytes_per_op,8:F0}B cpu/op={rec.server_cpu_us_per_op,6:F1}us gc={st.Gen0}/{st.Gen1}/{st.Gen2} client: cpu/op={rec.client_cpu_us_per_op,6:F1}us alloc/op={rec.client_alloc_bytes_per_op,6:F0}B"));
                    }
                }
                finally
                {
                    foreach (IBenchClient c in clients)
                    {
                        try { await c.DisposeAsync(); } catch { /* ignore */ }
                    }
                }
            }
        }
    }

    private static readonly object s_lock = new();

    private static void Emit(BenchOptions o, object rec)
    {
        string json = JsonSerializer.Serialize(rec);
        lock (s_lock)
        {
            File.AppendAllText(o.OutFile, json + "\n");
        }
    }
}
