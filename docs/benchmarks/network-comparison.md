# End-to-End Network Comparison

This page reports **real end-to-end, over-the-socket** measurements of Nalix against other .NET realtime
stacks, all run on the same machine with the same harness in one session. Unlike the micro-benchmarks elsewhere in this
section, every number here includes the kernel TCP stack, framing, dispatch, serialization, the thread pool
and the client library.

!!! warning "Read the caveats"
    These numbers come from a 4-vCPU cloud Linux container over loopback, where the load generator and the servers compete for the same 4 cores. Absolute values are much lower than on bare metal with dedicated network hardware. Use them to *compare the stacks with each other*, not as absolute capacity figures.

## Summary

!!! note "All 14 variants measured in a single session"
    All 12 frameworks and baselines below, plus two diagnostic Nalix variants, were measured alongside each other on 2026-10-02 in one uninterrupted session under .NET 10.0.401 SDK on a 4-vCPU Linux container. Every comparison reflects head-to-head results run under identical hardware and OS conditions.

- **Single-client latency:** Nalix TCP has the lowest p50 of the full frameworks. At 32 B the p50 is 52.2 µs for Nalix TCP, 55.5 µs for Nalix WebSocket and 57.6 µs for Nalix AEAD, against 68.4 µs for gRPC duplex, 74.2 µs for the MagicOnion hub, 76.3 µs for SignalR and 106.4 µs for gRPC unary. At 1 KB, Nalix TCP achieves 53.6 µs against 69.2 µs for gRPC duplex, 79.4 µs for the MagicOnion hub and 83.5 µs for SignalR. The hand-written raw-socket baseline (30.9 µs / 24.0 µs) and the raw Kestrel WebSocket echo (45.0 µs / 48.5 µs) are faster.
- **Throughput with 64 clients:**
  - At 32 B: gRPC duplex 77.6k ops/s, Nalix TCP 74.8k, SignalR 71.9k, Nalix WebSocket 69.1k, MagicOnion hub 67.5k, Nalix TCP AEAD 63.3k, gRPC unary 58.1k, MagicOnion unary 55.7k.
  - At 1 KB: SignalR 70.7k ops/s, gRPC duplex 68.4k, Nalix WebSocket 61.4k, Nalix TCP 60.8k, MagicOnion hub 58.2k, gRPC unary 56.9k, MagicOnion unary 54.8k, Nalix TCP AEAD 53.3k. Under this 4-core machine all frameworks are CPU-bound and sit within roughly 10–15% of each other at high concurrency.
- **Throughput with 1 and 16 clients:** Nalix TCP is first at 1 client for both sizes (18.1k at 32 B, 16.1k at 1 KB). At 16 clients and 32 B, Nalix WebSocket (64.5k) and Nalix TCP (62.9k) are level with gRPC duplex (62.9k) and ahead of SignalR (61.2k).
- **Diagnostic Nalix variants (not default options):** disabling LZ4 compression (`nalix-tcp-nocomp`) is within noise of the default at 32 B (p50 48.8 µs, 75.2k ops/s at 64 clients) and is somewhat higher at 1 KB (68.1k vs 60.8k ops/s, run spread ±5–6%). The `InlinePacketDispatcher` (`nalix-tcp-inline`) raises 64-client throughput to 86.6k ops/s at 32 B and 74.7k at 1 KB, and cuts server CPU/op to 22.4 µs, ahead of every framework in the table, at the cost of 104 B/op allocations and a slightly higher p50 (55.3 µs).
- **Server CPU per message:** Nalix TCP measures 29.3 µs at 64 clients and 32 B against 21.8 µs for SignalR and 21.9 µs for gRPC duplex, so its server is the more CPU-expensive one; its client is cheaper (19.2 µs client CPU/op vs 24.2 µs for SignalR).
- **Server allocations:** 56 B per message at 32 B for Nalix TCP and Nalix AEAD, the lowest of the full frameworks (MagicOnion hub 200 B, gRPC duplex 264 B, SignalR 672 B). Only the raw Kestrel WebSocket baseline allocates less (0 B). At 1 KB Nalix TCP allocates 1,048 B vs 1,192 B for the MagicOnion hub, 1,256 B for gRPC duplex and 1,664 B for SignalR.
- **Encryption:** Nalix AEAD (X25519 + ChaCha20-Poly1305) wins against gRPC over TLS on single-client latency (32 B p50 57.6 µs vs 100.4 µs for gRPC duplex TLS; at 1 KB 74.6 µs vs 103.8 µs), on 64-client throughput (63.3k vs 57.8k ops/s at 32 B; 53.3k vs 49.5k at 1 KB) and on server allocations (56 B vs 812 B at 32 B; 1,048 B vs 1,801 B at 1 KB).

## Environment

| Item | Value |
|:--|:--|
| CPU | Intel Xeon Processor @ 2.10 GHz (4 vCPU, 1 thread per core) |
| Memory | 16 GB |
| OS | Ubuntu 24.04.4 LTS (Linux 6.18, cloud VM container) |
| Runtime | .NET 10.0.x runtime (SDK 10.0.401), Release, x64 RyuJIT, TieredPGO on |
| GC | Server GC + Concurrent GC for **every** process (from `benchmarks/Directory.Build.props`) |
| Nalix | source at `master` (`Version.props` 14.2.16), project references |
| ASP.NET Core / SignalR | 10.0.12 (`Microsoft.AspNetCore.SignalR.Client`, `.Protocols.MessagePack` → MessagePack 2.5.302) |
| gRPC | `Grpc.AspNetCore` 2.84.0, `Grpc.Net.Client` 2.84.0, Google.Protobuf 3.35.1 |
| MagicOnion | `MagicOnion.Server` / `.Client` 7.11.0 (MessagePack 3.1.7) |
| Date | 2026-10-02 (All 14 variants measured in one session) |
| Machine state | Idle container before the run; one uninterrupted session executing all 14 variants |

## Methodology

Harness: `benchmarks/Nalix.Comparison.Benchmarks` (runner `benchmarks/run-comparison.ps1` / `run-comparison.sh`). MagicOnion is in a
separate executable, `benchmarks/Nalix.Comparison.MagicOnion`, because it needs MessagePack v3 while the SignalR
MessagePack protocol is built against v2. Both executables share the same harness source file.

- **Topology:** the driver starts the library's **server as a separate child process** on `127.0.0.1`,
  then runs the clients inside the driver process. The server process reports its own
  `GC.GetTotalAllocatedBytes(precise: true)`, Gen0/1/2 collection counts and process CPU time between
  `reset` and `report` commands sent over stdin. So the allocation and CPU numbers are server-only, and they cover the
  **whole process** (I/O threads, timers and background tasks included).
- **Workload:** echo. The client sends an opaque byte payload of **32 B** or **1024 B**, and the server returns the same
  bytes in a response message. Each library uses its idiomatic request/response API:

| Label | What runs |
|:--|:--|
| `raw-tcp` | Baseline: hand-written `Socket` echo with a 4-byte length prefix. No framework. |
| `kestrel-ws` | Baseline: ASP.NET Core `UseWebSockets` binary echo, `ClientWebSocket` client. |
| `nalix-tcp` | Nalix `ListenTcp<DefaultProtocol>` with a `[PacketHandler]` that echoes via a pooled response (`PacketFactory<T>.Acquire()` + `context.Sender.SendAsync`). The client uses `TcpSession.RequestAsync<T>`. **Default options**. |
| `nalix-ws` | Same as above over `ListenWebSocket`, with a `WebSocketSession` client. |
| `nalix-tcp-aead` | `UseSecureConnections()` + `UseSystemControl()`. The client runs `HandshakeAsync()` (X25519), then requests go out with `WithEncrypt()` and the handler has `[PacketEncryption(true)]`, so both directions are ChaCha20-Poly1305. |
| `signalr-ws-msgpack` | SignalR hub `byte[] Echo(byte[])`, WebSockets transport only, `SkipNegotiation`, MessagePack protocol, `InvokeAsync<byte[]>`. |
| `grpc-unary` / `grpc-unary-tls` | Unary `rpc Unary(EchoMessage) returns (EchoMessage)` with a `bytes` field, over h2c or HTTP/2+TLS (self-signed ECDSA P-256). |
| `grpc-duplex` / `grpc-duplex-tls` | One long-lived bidirectional stream per client, with one write and one read per operation. |
| `magiconion-unary` / `magiconion-hub` | `IService<T>` `UnaryResult<byte[]>`, or a `StreamingHub` method returning `ValueTask<byte[]>`, over h2c. |
| `nalix-tcp-nocomp` | *Diagnostic.* Same as `nalix-tcp` with LZ4 compression disabled on server and client. |
| `nalix-tcp-inline` | *Diagnostic.* Same as `nalix-tcp` but the server uses `InlinePacketDispatcher` instead of the default channel dispatcher. |

- **Every client owns its own connection** (for gRPC and MagicOnion, its own `GrpcChannel` / `SocketsHttpHandler`), so
  "N clients" means N TCP connections for every library.
- **Latency:** one client, sequential calls. 20 000 warm-up calls, then **100 000 timed calls**. Every call is timed
  with `Stopwatch` and the samples are sorted to read exact percentiles.
- **Throughput:** N ∈ {1, 16, 64} clients in a **closed loop** (each client keeps one request in flight). After 2 s of warm-up,
  requests are counted over an 8 s window.
- **Runs:** 3 runs of each cell. The tables show the **median** of the runs, and `±x%` is half of the (max − min)/median spread.
- ASP.NET Core servers use `WebApplication.CreateBuilder`, with logging cleared and the minimum level set to Warning, and Kestrel bound only to loopback. No
  competitor was tuned beyond its documented default setup. The same goes for Nalix: no tuning at all (loopback is exempt from the default per-IP connection quota since #365).
- Raw data (JSON lines, one row per run) is in
  [`data/network-comparison-results.jsonl`](data/network-comparison-results.jsonl). The tables below are produced from it by
  `benchmarks/Nalix.Comparison.Benchmarks/aggregate.py`.

Reproduce with:

```bash
benchmarks/run-comparison.sh        # Linux/macOS
```

```powershell
.\benchmarks\run-comparison.ps1    # Windows
```

## Results

### Latency — 32 B payload, 1 client, sequential request/response

| Library | mean (µs) | p50 (µs) | p90 (µs) | p99 (µs) | p99.9 (µs) | run spread (p50) | server alloc/op (B) |
|:--|--:|--:|--:|--:|--:|--:|--:|
| raw-tcp | 39.4 | 30.9 | 66.0 | 119.2 | 540.3 | ±11.0% | 144 |
| kestrel-ws | 50.4 | 45.0 | 79.9 | 132.8 | 564.4 | ±2.8% | 0 |
| nalix-tcp | 59.1 | 52.2 | 84.0 | 154.9 | 691.9 | ±2.8% | 56 |
| nalix-ws | 63.3 | 55.5 | 89.0 | 159.6 | 654.5 | ±2.7% | 192 |
| nalix-tcp-aead | 64.3 | 57.6 | 91.0 | 154.3 | 666.4 | ±3.3% | 56 |
| signalr-ws-msgpack | 82.3 | 76.3 | 116.1 | 190.8 | 720.1 | ±5.8% | 680 |
| grpc-unary | 121.1 | 106.4 | 170.5 | 353.5 | 1215.7 | ±4.1% | 969 |
| grpc-duplex | 74.1 | 68.4 | 101.3 | 167.3 | 743.1 | ±3.9% | 264 |
| grpc-unary-tls | 168.4 | 150.5 | 236.5 | 490.8 | 1841.2 | ±4.7% | 1037 |
| grpc-duplex-tls | 110.5 | 100.4 | 155.2 | 282.6 | 1108.5 | ±3.6% | 328 |
| nalix-tcp-nocomp | 55.0 | 48.8 | 77.6 | 125.3 | 556.2 | ±3.7% | 56 |
| nalix-tcp-inline | 62.1 | 55.3 | 86.9 | 155.7 | 792.0 | ±8.2% | 104 |
| magiconion-unary | 123.5 | 110.2 | 175.8 | 379.6 | 1257.9 | ±2.6% | 1433 |
| magiconion-hub | 79.3 | 74.2 | 109.1 | 173.8 | 784.5 | ±8.3% | 200 |

### Latency — 1024 B payload, 1 client, sequential request/response

| Library | mean (µs) | p50 (µs) | p90 (µs) | p99 (µs) | p99.9 (µs) | run spread (p50) | server alloc/op (B) |
|:--|--:|--:|--:|--:|--:|--:|--:|
| raw-tcp | 33.5 | 24.0 | 57.6 | 107.4 | 424.1 | ±12.0% | 144 |
| kestrel-ws | 53.2 | 48.5 | 84.3 | 144.5 | 459.3 | ±2.7% | 0 |
| nalix-tcp | 61.2 | 53.6 | 86.8 | 150.9 | 760.0 | ±6.6% | 1048 |
| nalix-ws | 71.6 | 63.4 | 99.5 | 199.0 | 893.5 | ±3.0% | 1184 |
| nalix-tcp-aead | 84.7 | 74.6 | 117.2 | 230.9 | 971.6 | ±5.4% | 1048 |
| signalr-ws-msgpack | 93.9 | 83.5 | 132.2 | 255.3 | 1197.6 | ±4.7% | 1672 |
| grpc-unary | 120.8 | 106.7 | 165.1 | 318.9 | 1475.8 | ±2.9% | 1961 |
| grpc-duplex | 76.8 | 69.2 | 105.0 | 180.6 | 925.1 | ±3.4% | 1256 |
| grpc-unary-tls | 182.8 | 161.4 | 255.7 | 520.0 | 1783.2 | ±1.9% | 2028 |
| grpc-duplex-tls | 114.6 | 103.8 | 157.7 | 292.9 | 1142.2 | ±1.9% | 1300 |
| nalix-tcp-nocomp | 59.0 | 52.2 | 82.5 | 146.6 | 713.5 | ±3.4% | 1048 |
| nalix-tcp-inline | 61.9 | 55.6 | 85.3 | 156.4 | 731.7 | ±3.3% | 1096 |
| magiconion-unary | 135.0 | 120.7 | 189.7 | 383.9 | 1599.5 | ±4.9% | 3737 |
| magiconion-hub | 85.3 | 79.4 | 114.9 | 192.2 | 1068.4 | ±0.8% | 1192 |

### Throughput — 32 B payload, N clients closed-loop (ops/s, median of runs)

| Library | 1 client | 16 clients | 64 clients |
|:--|--:|--:|--:|
| raw-tcp | 24,865 (±8%) | 107,698 (±5%) | 126,234 (±4%) |
| kestrel-ws | 19,602 (±3%) | 91,628 (±2%) | 106,659 (±0%) |
| nalix-tcp | 18,121 (±3%) | 62,919 (±6%) | 74,791 (±2%) |
| nalix-ws | 16,109 (±2%) | 64,458 (±5%) | 69,135 (±5%) |
| nalix-tcp-aead | 14,177 (±4%) | 54,470 (±3%) | 63,309 (±4%) |
| signalr-ws-msgpack | 11,345 (±3%) | 61,223 (±1%) | 71,871 (±4%) |
| grpc-unary | 8,691 (±4%) | 48,197 (±6%) | 58,059 (±5%) |
| grpc-duplex | 13,658 (±7%) | 62,926 (±3%) | 77,648 (±3%) |
| grpc-unary-tls | 6,217 (±13%) | 35,028 (±2%) | 43,455 (±4%) |
| grpc-duplex-tls | 9,066 (±5%) | 43,856 (±3%) | 57,791 (±5%) |
| nalix-tcp-nocomp | 18,028 (±5%) | 60,913 (±4%) | 75,224 (±3%) |
| nalix-tcp-inline | 18,126 (±5%) | 75,803 (±6%) | 86,639 (±2%) |
| magiconion-unary | 7,658 (±3%) | 46,330 (±3%) | 55,721 (±2%) |
| magiconion-hub | 12,155 (±3%) | 46,144 (±13%) | 67,501 (±2%) |

#### Cost per message at 64 clients — 32 B

| Library | server alloc/op (B) | server CPU/op (µs) | client CPU/op (µs) | client alloc/op (B) | server Gen0/1/2 per run |
|:--|--:|--:|--:|--:|--:|
| raw-tcp | 143 | 13.9 | 14.0 | 287 | 57/0/0 |
| kestrel-ws | 0 | 17.0 | 17.4 | 152 | 0/0/0 |
| nalix-tcp | 56 | 29.3 | 19.2 | 887 | 1/0/0 |
| nalix-ws | 192 | 27.1 | 22.6 | 888 | 40/0/0 |
| nalix-tcp-aead | 56 | 34.2 | 22.9 | 887 | 0/0/0 |
| signalr-ws-msgpack | 672 | 21.8 | 24.2 | 1553 | 92/0/0 |
| grpc-unary | 968 | 27.9 | 32.8 | 6737 | 80/0/0 |
| grpc-duplex | 264 | 21.9 | 22.7 | 2320 | 61/0/0 |
| grpc-unary-tls | 1515 | 37.6 | 41.5 | 6737 | 74/0/0 |
| grpc-duplex-tls | 812 | 30.4 | 29.7 | 2322 | 112/0/0 |
| nalix-tcp-nocomp | 56 | 29.2 | 19.3 | 887 | 1/0/0 |
| nalix-tcp-inline | 105 | 22.4 | 17.7 | 888 | 28/0/0 |
| magiconion-unary | 1432 | 28.5 | 33.3 | 7401 | 117/0/0 |
| magiconion-hub | 200 | 26.3 | 25.1 | 2238 | 41/0/0 |

### Throughput — 1024 B payload, N clients closed-loop (ops/s, median of runs)

| Library | 1 client | 16 clients | 64 clients |
|:--|--:|--:|--:|
| raw-tcp | 27,356 (±3%) | 120,792 (±3%) | 128,443 (±8%) |
| kestrel-ws | 19,322 (±5%) | 88,303 (±2%) | 107,871 (±3%) |
| nalix-tcp | 16,051 (±3%) | 55,120 (±5%) | 60,776 (±6%) |
| nalix-ws | 13,946 (±3%) | 52,886 (±5%) | 61,438 (±3%) |
| nalix-tcp-aead | 12,546 (±2%) | 45,315 (±2%) | 53,345 (±3%) |
| signalr-ws-msgpack | 11,720 (±5%) | 59,726 (±3%) | 70,651 (±4%) |
| grpc-unary | 8,162 (±1%) | 45,147 (±5%) | 56,864 (±5%) |
| grpc-duplex | 13,243 (±7%) | 62,125 (±4%) | 68,418 (±3%) |
| grpc-unary-tls | 5,684 (±5%) | 31,342 (±3%) | 43,000 (±4%) |
| grpc-duplex-tls | 9,055 (±3%) | 42,611 (±4%) | 49,542 (±5%) |
| nalix-tcp-nocomp | 16,596 (±7%) | 58,821 (±1%) | 68,116 (±5%) |
| nalix-tcp-inline | 17,050 (±3%) | 64,992 (±2%) | 74,664 (±2%) |
| magiconion-unary | 7,661 (±5%) | 43,613 (±1%) | 54,752 (±2%) |
| magiconion-hub | 11,189 (±1%) | 51,115 (±4%) | 58,182 (±1%) |

#### Cost per message at 64 clients — 1024 B

| Library | server alloc/op (B) | server CPU/op (µs) | client CPU/op (µs) | client alloc/op (B) | server Gen0/1/2 per run |
|:--|--:|--:|--:|--:|--:|
| raw-tcp | 144 | 14.4 | 13.8 | 287 | 54/0/0 |
| kestrel-ws | 0 | 17.1 | 17.4 | 152 | 0/0/0 |
| nalix-tcp | 1048 | 34.1 | 23.2 | 1878 | 215/0/0 |
| nalix-ws | 1184 | 30.4 | 24.1 | 1879 | 232/0/0 |
| nalix-tcp-aead | 1048 | 39.7 | 28.1 | 1879 | 184/0/0 |
| signalr-ws-msgpack | 1664 | 22.0 | 24.1 | 2546 | 341/0/0 |
| grpc-unary | 1960 | 28.4 | 32.8 | 7729 | 164/0/0 |
| grpc-duplex | 1256 | 23.7 | 24.0 | 3335 | 280/0/0 |
| grpc-unary-tls | 2504 | 38.9 | 42.4 | 7729 | 150/0/0 |
| grpc-duplex-tls | 1801 | 34.7 | 33.7 | 3344 | 202/0/0 |
| nalix-tcp-nocomp | 1048 | 30.4 | 20.0 | 1879 | 239/0/0 |
| nalix-tcp-inline | 1097 | 25.6 | 20.4 | 1879 | 265/0/0 |
| magiconion-unary | 3736 | 29.9 | 34.0 | 9707 | 256/0/0 |
| magiconion-hub | 1192 | 30.0 | 28.2 | 3261 | 201/0/0 |

## Who wins where

| Scenario | Best framework (excluding raw baselines) | Nalix position |
|:--|:--|:--|
| Latency, 32 B, 1 client | **Nalix TCP** (p50 52.2 µs) | 1st. Nalix WS 55.5 µs, Nalix AEAD 57.6 µs, gRPC duplex 68.4 µs, MagicOnion hub 74.2 µs, SignalR 76.3 µs |
| Latency, 1 KB, 1 client | **Nalix TCP** (p50 53.6 µs) | 1st. Nalix WS 63.4 µs, gRPC duplex 69.2 µs, Nalix AEAD 74.6 µs, MagicOnion hub 79.4 µs, SignalR 83.5 µs |
| Latency, encrypted | **Nalix AEAD** at both sizes (57.6 µs vs gRPC duplex TLS 100.4 µs at 32 B; 74.6 µs vs 103.8 µs at 1 KB) | 1st at both sizes |
| Throughput, 1 client, 32 B | **Nalix TCP** (18.1k) | 1st (Nalix WS 16.1k, Nalix AEAD 14.2k, gRPC duplex 13.7k) |
| Throughput, 16 clients, 32 B | **Nalix WS** 64.5k / **Nalix TCP** 62.9k / gRPC duplex 62.9k | 1st (within run noise of gRPC duplex; SignalR 61.2k) |
| Throughput, 16 clients, 1 KB | **gRPC duplex** 62.1k | 3rd (SignalR 59.7k, Nalix TCP 55.1k) |
| Throughput, 64 clients, 32 B | **gRPC duplex** 77.6k | 2nd (Nalix TCP 74.8k, SignalR 71.9k, Nalix WS 69.1k) |
| Throughput, 64 clients, 1 KB | **SignalR** 70.7k | 3rd–4th (gRPC duplex 68.4k, Nalix WS 61.4k, Nalix TCP 60.8k) |
| Throughput, encrypted, 64 clients | **Nalix AEAD** (63.3k at 32 B, 53.3k at 1 KB) | 1st (gRPC duplex TLS 57.8k / 49.5k) |
| Throughput, 64 clients, diagnostic Nalix variants | **Nalix TCP inline dispatcher** 86.6k (32 B) / 74.7k (1 KB) | Not the default configuration; shown for reference only |
| Server allocations/msg | **Nalix TCP / AEAD** 56 B at 32 B | 1st of the frameworks (only raw Kestrel WebSocket, 0 B, is lower) |
| Server CPU/msg, 64 clients, 32 B | **SignalR** 21.8 µs / gRPC duplex 21.9 µs | Nalix TCP 29.3 µs, behind both and MagicOnion hub (26.3 µs) |

## Caveats

- **Loopback and a small shared host.** The 4 vCPUs run both the load generator and the server, so high-concurrency numbers are CPU-bound and differences of a few percent are within run-to-run noise (see the ± columns); absolute capacity on bare metal with dedicated network hardware will differ.
- **The client library counts.** Latency and throughput include each stack's client (Nalix SDK, `HubConnection`, `Grpc.Net.Client`, MagicOnion's dynamic client).
- **Closed loop with one request in flight per client.** Pipelined or fire-and-forget server push was not measured.
- **Whole-process allocations.** The server numbers include background work (timers, task manager), spread across all messages.
- **The encrypted comparison is not like-for-like.** TLS 1.3 (AES-GCM via OpenSSL, hardware-accelerated) protects the whole stream, while Nalix encrypts per packet with a managed ChaCha20-Poly1305 after an X25519 handshake, and needs no certificate.
