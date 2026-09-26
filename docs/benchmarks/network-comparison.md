# End-to-End Network Comparison

This page reports **real end-to-end, over-the-socket** measurements of Nalix against other .NET realtime
stacks, all run on the same machine with the same harness in one session. Unlike the micro-benchmarks elsewhere in this
section, every number here includes the kernel TCP stack, framing, dispatch, serialization, the thread pool
and the client library.

!!! warning "Read the caveats"
    These numbers come from a **4-vCPU Linux container over loopback**, with client and server sharing the
    same 4 cores. Absolute values are much lower than on bare metal. Use them to *compare the stacks with
    each other*, not as absolute capacity figures.

## Summary

!!! note "This pass only re-ran Nalix"
    The numbers below for `nalix-tcp`, `nalix-ws` and `nalix-tcp-aead` were re-measured on 2026-09-26 against
    `perf/dispatch-readyqueue` (a small dispatch-channel fix, see [What changed](#what-changed-since-the-previous-run)).
    Every other library's numbers are from the previous session and were **not** re-run alongside them. Any comparison
    that now reads differently than before (e.g. Nalix passing a library it previously trailed) reflects Nalix's own
    numbers moving, not a verified head-to-head result — treat those as tentative until both sides are re-measured
    in the same session.

- **Single-client latency:** Nalix TCP has the lowest latency of the frameworks tested. At 32 B the p50 is 89.3 µs, against
  123 µs for gRPC duplex, 157 µs for SignalR, 161 µs for the MagicOnion hub and 217 µs for gRPC unary. At 1 KB it is 99.0 µs against 132 µs for gRPC duplex.
  Only the hand-written raw-socket baseline is faster (71 µs, from the previous run).
- **Throughput with 64 clients:** At 32 B: Nalix TCP 48.0k ops/s, SignalR 45.9k, gRPC duplex 44.4k, Nalix WebSocket 43.7k, MagicOnion hub 39.7k.
  At 1 KB: Nalix TCP 41.3k, SignalR 38.8k, gRPC duplex 37.7k, Nalix WebSocket 37.3k. Nalix TCP now reads ahead of SignalR at both sizes,
  but SignalR was not re-run this session — see the note above. At 16 clients Nalix TCP now reads ahead of gRPC duplex at both payload sizes
  (40.8k vs 37.4k at 32 B; 32.2k vs 32.2k, essentially tied, at 1 KB), same caveat.
- **Server CPU per message is still Nalix's weak spot:** 44.5 µs at 32 B against 36.3 µs for SignalR (+23 %) and 39.8 µs for gRPC duplex (+12 %).
  Nalix keeps up in throughput because its client is cheaper (30 µs client CPU/op vs 39 µs for SignalR).
- **Server allocations:** 120 B per message at 32 B for Nalix TCP, the lowest of the frameworks (MagicOnion hub 200 B,
  gRPC duplex 264 B, SignalR 672 B). Only the raw Kestrel WebSocket baseline allocates less (0 B).
- **Encryption:** Nalix AEAD (X25519 + ChaCha20-Poly1305) wins against gRPC over TLS for small messages
  (32 B p50 103 µs vs 209 µs for gRPC duplex TLS, unchanged from the previous run; 41.8k vs 32.0k ops/s at 64 clients).
  At 1 KB it now reads ahead too (p50 132.5 µs vs 213.5 µs; 33.1k vs 27.0k ops/s at 64 clients) — a reversal from the
  previous run, where it lost at 1 KB (255 µs). That previous-run number came from a much slower session overall and
  was not cross-checked against this one; take the 1 KB AEAD win as unconfirmed until gRPC + TLS is re-run alongside it,
  not as a result of the dispatch-channel fix below (which is far too small to explain a ~2× latency change).

## Environment

| Item | Value |
|:--|:--|
| CPU | Intel Xeon Processor @ 2.80 GHz (KVM guest, AVX2/AVX-512), 33 MB L3 |
| Cores | 4 vCPU (client and server processes share them) |
| Memory | 15 GB |
| OS | Ubuntu 24.04.4 LTS, Linux 6.18 (container; IPv6 unavailable) |
| Runtime | .NET 10.0.12 (SDK 10.0.401), Release, x64 RyuJIT, TieredPGO on |
| GC | Server GC + Concurrent GC for **every** process (from `benchmarks/Directory.Build.props`) |
| Nalix | source at `master` 0b58c2824 (Version.props 14.2.16), project references; **`nalix-tcp`/`nalix-ws`/`nalix-tcp-aead` re-run at `perf/dispatch-readyqueue` 79211bccf** |
| ASP.NET Core / SignalR | 10.0.12 (`Microsoft.AspNetCore.SignalR.Client`, `.Protocols.MessagePack` → MessagePack 2.5.302) |
| gRPC | `Grpc.AspNetCore` 2.84.0, `Grpc.Net.Client` 2.84.0, Google.Protobuf 3.35.1 |
| MagicOnion | `MagicOnion.Server` / `.Client` 7.11.0 (MessagePack 3.1.7) |
| Date | 2026-09-25 (full run); Nalix rows re-measured 2026-09-26 |
| Machine state | Idle before the run (no build servers, load average < 0.5 target); one uninterrupted session of about 65 minutes for the full run, a separate short session for the Nalix-only re-run |

## Methodology

Harness: `benchmarks/Nalix.Comparison.Benchmarks` (runner `benchmarks/run-comparison.sh`). MagicOnion is in a
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
LIBS="nalix-tcp,nalix-ws,nalix-tcp-aead,raw-tcp,kestrel-ws,signalr-ws-msgpack,grpc-unary,grpc-duplex,grpc-unary-tls,grpc-duplex-tls" \
  benchmarks/run-comparison.sh   # everything (about 65 min on the machine above)
LIBS="nalix-tcp,grpc-duplex" MO_LIBS="" benchmarks/run-comparison.sh /tmp/cmp --runs 5
```

## Results

### Latency — 32 B payload, 1 client, sequential request/response

| Library | mean (µs) | p50 (µs) | p90 (µs) | p99 (µs) | p99.9 (µs) | run spread (p50) | server alloc/op (B) |
|:--|--:|--:|--:|--:|--:|--:|--:|
| nalix-tcp | 101.3 | 89.3 | 146.9 | 260.6 | 1085.3 | ±1.9% | 120 |
| nalix-ws | 115.2 | 102.7 | 171.6 | 300.9 | 970.5 | ±1.8% | 256 |
| nalix-tcp-aead | 118.3 | 103.2 | 172.7 | 307.0 | 1251.8 | ±2.7% | 120 |
| raw-tcp | 84.5 | 70.9 | 148.6 | 262.5 | 1021.9 | ±15.9% | 144 |
| kestrel-ws | 109.6 | 98.9 | 160.3 | 314.9 | 1427.7 | ±5.3% | 0 |
| signalr-ws-msgpack | 172.8 | 157.3 | 254.9 | 491.7 | 1570.5 | ±0.3% | 680 |
| grpc-unary | 242.1 | 217.3 | 347.8 | 747.6 | 2279.3 | ±2.3% | 969 |
| grpc-duplex | 137.0 | 123.1 | 196.5 | 379.4 | 1429.0 | ±2.2% | 264 |
| grpc-unary-tls | 363.6 | 337.8 | 532.1 | 1017.3 | 2866.2 | ±3.1% | 1076 |
| grpc-duplex-tls | 235.6 | 209.0 | 347.2 | 701.8 | 2452.2 | ±1.8% | 331 |
| magiconion-unary | 262.3 | 238.5 | 380.4 | 812.1 | 2533.9 | ±0.4% | 1433 |
| magiconion-hub | 184.9 | 161.0 | 272.5 | 556.8 | 2256.6 | ±1.4% | 200 |

### Latency — 1024 B payload, 1 client, sequential request/response

| Library | mean (µs) | p50 (µs) | p90 (µs) | p99 (µs) | p99.9 (µs) | run spread (p50) | server alloc/op (B) |
|:--|--:|--:|--:|--:|--:|--:|--:|
| nalix-tcp | 114.2 | 99.0 | 166.0 | 303.5 | 1293.5 | ±3.5% | 1112 |
| nalix-ws | 142.6 | 127.6 | 208.5 | 376.4 | 1287.2 | ±3.9% | 1248 |
| nalix-tcp-aead | 147.5 | 132.5 | 207.2 | 356.9 | 1283.0 | ±1.4% | 1112 |
| raw-tcp | 80.8 | 61.6 | 144.7 | 251.3 | 950.1 | ±16.1% | 144 |
| kestrel-ws | 120.7 | 112.1 | 176.4 | 327.8 | 1159.6 | ±3.3% | 0 |
| signalr-ws-msgpack | 202.1 | 183.0 | 294.6 | 578.5 | 2164.9 | ±1.1% | 1672 |
| grpc-unary | 257.8 | 230.7 | 379.8 | 773.5 | 2602.8 | ±0.1% | 1961 |
| grpc-duplex | 149.1 | 131.8 | 213.9 | 432.2 | 1817.5 | ±3.9% | 1256 |
| grpc-unary-tls | 413.7 | 374.8 | 602.9 | 1170.5 | 3303.6 | ±4.5% | 2082 |
| grpc-duplex-tls | 232.9 | 213.5 | 324.7 | 624.6 | 2386.0 | ±1.6% | 1327 |
| magiconion-unary | 290.5 | 262.6 | 431.5 | 854.3 | 2891.6 | ±1.5% | 3737 |
| magiconion-hub | 181.1 | 161.3 | 260.6 | 519.7 | 2085.4 | ±0.9% | 1192 |

### Throughput — 32 B payload, N clients closed-loop (ops/s, median of runs)

| Library | 1 client | 16 clients | 64 clients |
|:--|--:|--:|--:|
| nalix-tcp | 9,763 (±2%) | 40,788 (±6%) | 47,954 (±3%) |
| nalix-ws | 8,226 (±1%) | 35,880 (±8%) | 43,678 (±8%) |
| nalix-tcp-aead | 8,483 (±2%) | 33,258 (±1%) | 41,780 (±1%) |
| raw-tcp | 12,596 (±4%) | 68,908 (±1%) | 84,469 (±3%) |
| kestrel-ws | 9,641 (±3%) | 53,479 (±4%) | 68,883 (±10%) |
| signalr-ws-msgpack | 5,219 (±7%) | 34,571 (±3%) | 45,904 (±2%) |
| grpc-unary | 4,202 (±7%) | 25,638 (±6%) | 33,187 (±5%) |
| grpc-duplex | 7,451 (±4%) | 37,413 (±3%) | 44,388 (±6%) |
| grpc-unary-tls | 2,555 (±8%) | 21,686 (±3%) | 25,741 (±2%) |
| grpc-duplex-tls | 4,382 (±4%) | 26,052 (±3%) | 31,970 (±5%) |
| magiconion-unary | 3,707 (±5%) | 25,521 (±0%) | 32,050 (±1%) |
| magiconion-hub | 5,884 (±2%) | 30,279 (±2%) | 39,715 (±3%) |

#### Cost per message at 64 clients — 32 B

| Library | server alloc/op (B) | server CPU/op (µs) | client CPU/op (µs) | client alloc/op (B) | server Gen0/1/2 per run |
|:--|--:|--:|--:|--:|--:|
| nalix-tcp | 120 | 44.5 | 30.2 | 1246 | 2/0/0 |
| nalix-ws | 256 | 43.5 | 35.0 | 1247 | 16/0/0 |
| nalix-tcp-aead | 120 | 50.4 | 34.3 | 1247 | 2/1/0 |
| raw-tcp | 144 | 21.8 | 21.4 | 287 | 17/0/0 |
| kestrel-ws | 0 | 25.4 | 27.3 | 152 | 0/0/0 |
| signalr-ws-msgpack | 672 | 36.3 | 39.2 | 1554 | 88/0/0 |
| grpc-unary | 969 | 48.1 | 57.2 | 6737 | 47/0/0 |
| grpc-duplex | 264 | 39.8 | 39.2 | 2322 | 34/0/0 |
| grpc-unary-tls | 1493 | 67.7 | 72.1 | 6737 | 50/0/0 |
| grpc-duplex-tls | 804 | 55.7 | 51.5 | 2324 | 42/0/0 |
| magiconion-unary | 1433 | 49.7 | 57.1 | 7401 | 71/0/0 |
| magiconion-hub | 200 | 44.8 | 42.0 | 2239 | 15/0/0 |

### Throughput — 1024 B payload, N clients closed-loop (ops/s, median of runs)

| Library | 1 client | 16 clients | 64 clients |
|:--|--:|--:|--:|
| nalix-tcp | 9,170 (±4%) | 32,218 (±2%) | 41,335 (±4%) |
| nalix-ws | 7,418 (±4%) | 30,229 (±1%) | 37,271 (±5%) |
| nalix-tcp-aead | 6,427 (±1%) | 28,647 (±2%) | 33,117 (±1%) |
| raw-tcp | 11,618 (±6%) | 64,361 (±3%) | 83,681 (±7%) |
| kestrel-ws | 8,408 (±3%) | 49,593 (±3%) | 63,120 (±4%) |
| signalr-ws-msgpack | 5,244 (±4%) | 31,409 (±3%) | 38,780 (±5%) |
| grpc-unary | 4,071 (±1%) | 25,581 (±2%) | 32,226 (±2%) |
| grpc-duplex | 6,926 (±3%) | 32,234 (±2%) | 37,740 (±3%) |
| grpc-unary-tls | 2,194 (±7%) | 19,653 (±4%) | 21,808 (±1%) |
| grpc-duplex-tls | 4,354 (±2%) | 23,551 (±3%) | 27,049 (±8%) |
| magiconion-unary | 3,550 (±3%) | 23,549 (±4%) | 28,380 (±2%) |
| magiconion-hub | 5,531 (±1%) | 26,948 (±3%) | 33,729 (±5%) |

#### Cost per message at 64 clients — 1024 B

| Library | server alloc/op (B) | server CPU/op (µs) | client CPU/op (µs) | client alloc/op (B) | server Gen0/1/2 per run |
|:--|--:|--:|--:|--:|--:|
| nalix-tcp | 1112 | 50.8 | 33.8 | 2232 | 148/0/0 |
| nalix-ws | 1248 | 50.0 | 40.9 | 2232 | 149/0/0 |
| nalix-tcp-aead | 1112 | 61.2 | 44.4 | 2234 | 117/0/0 |
| raw-tcp | 144 | 22.1 | 22.2 | 287 | 30/0/0 |
| kestrel-ws | 0 | 28.4 | 29.1 | 152 | 0/0/0 |
| signalr-ws-msgpack | 1664 | 40.4 | 42.4 | 2546 | 187/0/0 |
| grpc-unary | 1960 | 49.4 | 56.4 | 7729 | 128/0/0 |
| grpc-duplex | 1256 | 44.1 | 43.5 | 3342 | 146/0/0 |
| grpc-unary-tls | 2491 | 74.7 | 79.2 | 7730 | 83/0/0 |
| grpc-duplex-tls | 1784 | 62.5 | 58.1 | 3343 | 152/0/0 |
| magiconion-unary | 3737 | 54.3 | 62.2 | 9705 | 159/0/0 |
| magiconion-hub | 1192 | 51.7 | 47.5 | 3262 | 104/0/0 |

## Who wins where

Rows marked † compare a re-measured Nalix number against another library's number from the previous session (not re-run together this pass) — read as tentative, per the note in [Summary](#summary).

| Scenario | Best framework (excluding raw baselines) | Nalix position |
|:--|:--|:--|
| Latency, 32 B, 1 client | **Nalix TCP** (p50 89.3 µs) † | 1st. gRPC duplex +38 %, SignalR +76 %, MagicOnion hub +80 % |
| Latency, 1 KB, 1 client | **Nalix TCP** (p50 99.0 µs) † | 1st. gRPC duplex 132 µs |
| Latency, encrypted | **Nalix AEAD** at both sizes (103 µs vs gRPC duplex TLS 209 µs at 32 B; 132.5 µs vs 213.5 µs at 1 KB) † | Wins both now, unconfirmed at 1 KB — see Summary |
| Throughput, 1 client | **Nalix TCP** (9.8k at 32 B, 9.2k at 1 KB) | 1st |
| Throughput, 16 clients, 32 B | **Nalix TCP** 40.8k † | 1st (gRPC duplex 37.4k), was 2nd in the previous run |
| Throughput, 16 clients, 1 KB | **Nalix TCP** 32.2k † | Tied with gRPC duplex (32.2k) |
| Throughput, 64 clients, 32 B | **Nalix TCP** 48.0k † | 1st (SignalR 45.9k, gRPC duplex 44.4k), was 3rd in the previous run |
| Throughput, 64 clients, 1 KB | **Nalix TCP** 41.3k † | 1st (SignalR 38.8k, gRPC duplex 37.7k), was 3rd in the previous run |
| Throughput, encrypted, 64 clients, 1 KB | **Nalix AEAD** 33.1k † | 1st (gRPC duplex TLS 27.0k), was behind in the previous run |
| Server allocations/msg | **Nalix TCP** 120 B at 32 B | 1st of the frameworks (only the raw Kestrel baseline, 0 B, is lower) |
| Server CPU/msg, 64 clients, 32 B | **SignalR** 36.3 µs | Nalix 44.5 µs, still behind SignalR (+23 %) and gRPC duplex (+12 %) |

## What changed since the previous run

The previous run measured `master` at 9decd6ab3, before #369–#372. Those PRs fixed the hotspots that run found:
an allocation-free dispatch wake and fewer thread-pool hops (#369), sending frames in place without clearing every pooled buffer (#370),
skipping LZ4 on incompressible frames (#371), and stopping the dispatch workers from busy-spinning (#372).

| Metric (nalix-tcp) | Previous run | This run |
|:--|--:|--:|
| Server alloc/op, 1 client, 32 B | 2 000 B | 120 B |
| Server alloc/op, 1 client, 1 KB | 2 995 B | 1 112 B |
| Server CPU/op, 64 clients, 32 B | 56.8 µs | 45.2 µs |
| Server CPU/op, 64 clients, 1 KB | 73.6 µs | 53.1 µs |
| Throughput, 64 clients, 32 B | 39.6k | 44.3k |
| Throughput, 64 clients, 1 KB | 31.1k | 36.9k |
| Throughput, 64 clients, 1 KB, WebSocket | 27.1k | 35.8k |

Read the throughput rows with care. The competitors were also **slower** in this session than in the previous one
(gRPC duplex 52.8k → 44.4k, SignalR 46.8k → 45.9k, MagicOnion hub 43.8k → 39.7k at 64 clients / 32 B), so the shared
VM was slower overall today. Compare rankings within one run, not absolute numbers across runs. Relative to the other frameworks,
Nalix moved from 4th to 3rd at 64 clients / 32 B, and from 6th to 3rd at 64 clients / 1 KB. The allocation and server CPU/op
numbers are the most reliable signal of the fixes.

The diagnostic rows from the previous run (`nalix-tcp-nocomp`, `nalix-tcp-inline`) were not re-run: their fixes are now on by default.

### Nalix-only re-run at `perf/dispatch-readyqueue` (2026-09-26)

`DispatchChannel<TPacket>`'s ready-queue was switched from `Channel<T>` to `ConcurrentQueue<T>` (see
[`docs/benchmarks/infrastructure.md`](infrastructure.md#dispatch-ready-queue)) — a lock removed from a hop that is
roughly 15–22 % of total server CPU/op, itself made about 12 % cheaper. Only `nalix-tcp`, `nalix-ws` and
`nalix-tcp-aead` were re-run; every number above still marked with the library's name unchanged is from the
2026-09-25 session.

| Metric (nalix-tcp) | 2026-09-25 | 2026-09-26 (this fix) |
|:--|--:|--:|
| Server CPU/op, 64 clients, 32 B | 45.2 µs | 44.5 µs |
| Server CPU/op, 64 clients, 1 KB | 53.1 µs | 50.8 µs |
| Throughput, 64 clients, 32 B | 44.3k | 48.0k |
| Throughput, 64 clients, 1 KB | 36.9k | 41.3k |
| Client alloc/op, 64 clients, 32 B | 2 095 B | 1 246 B |
| Client alloc/op, 64 clients, 1 KB | 3 082 B | 2 232 B |

The client-allocation drop (2 095 B → 1 246 B) is far larger than the ready-queue fix can explain by itself and
almost certainly reflects other work already on `master` since the 2026-09-25 run (the client request-path
allocation work merged earlier in this cycle), not something new in this pass. The CPU/op and throughput deltas
are directionally consistent with the ready-queue fix but, per the [Summary](#summary) note, were not isolated
from ordinary session-to-session noise on this shared container — see
[`infrastructure.md`](infrastructure.md#dispatch-ready-queue) for the controlled, isolated measurement of the fix
itself (−12 % at the dispatch-channel hop, not distinguishable from noise end-to-end).

## Remaining gaps

- **Server CPU per message** is still about 23 % above SignalR and 12 % above gRPC duplex.
- **Encrypted 1 KB messages:** the previous run measured Nalix AEAD as slower than gRPC over TLS at 1 KB; this
  run's numbers show the opposite. Neither gRPC + TLS nor the previous Nalix AEAD numbers were re-verified
  together in one session, so this reversal is unconfirmed — see the Summary note. The underlying reason a gap
  could exist either way is unchanged: the managed, scalar ChaCha20-Poly1305 and a per-packet OS CSPRNG call for
  the nonce (`src/Nalix.Codec/Security/EnvelopeCipher.cs`) versus hardware AES-GCM via OpenSSL.
- **Client allocations:** `TcpSession.RequestAsync` now allocates about 1.2 KB per call (2.2 KB at 1 KB) in this
  run, against 1.5 KB for SignalR (SignalR not re-measured this pass).
- **The payload array is allocated per request** (`byte[]` packet field). This is most of the remaining per-message allocation at 1 KB.

## Caveats

- **Container and loopback.** Loopback in this VM has high latency (a raw-socket round trip p50 is about 60–70 µs, where bare metal is typically 10–20 µs), and
  4 vCPUs are shared by client and server. Throughput is bounded by total CPU. The server CPU/op and client CPU/op columns
  show where the CPU goes, and they are the most portable numbers here.
- **Shared cores and run-to-run noise.** The run spread is shown per cell. The raw baselines varied most (raw-tcp p50 ±16 %).
  Differences of a few percent between frameworks (for example Nalix TCP vs gRPC duplex at 64 clients) are within the noise.
- **The client library counts.** Latency and throughput include each stack's client (Nalix SDK, `HubConnection`,
  `Grpc.Net.Client`, MagicOnion's dynamic client).
- **Closed loop with one request in flight per client.** Pipelined or fire-and-forget server push was not measured. Stacks that batch
  writes, such as gRPC and SignalR, might gain more from pipelining.
- **Whole-process allocations.** The server numbers include background work (timers, task manager), spread across all messages.
- **Excluded libraries.** LiteNetLib (UDP, reliable-ordered) and DotNetty were not benchmarked. Neither offers a comparable request/response
  RPC model without writing a custom protocol. Nalix UDP was not benchmarked either.
- **The encrypted comparison is not like-for-like.** TLS 1.3 (AES-GCM via OpenSSL, hardware-accelerated) protects the whole stream, while
  Nalix encrypts per packet with a managed ChaCha20-Poly1305 after an X25519 handshake, and needs no certificate.
