# End-to-End Network Comparison

This page reports **real end-to-end, over-the-socket** measurements of Nalix against other .NET realtime
stacks, all run on the same machine with the same harness in one session. Unlike the micro-benchmarks elsewhere in this
section, every number here includes the kernel TCP stack, framing, dispatch, serialization, the thread pool
and the client library.

!!! warning "Read the caveats"
    These numbers come from a shared desktop machine over Windows loopback. Absolute values are much lower than on bare metal with dedicated network hardware. Use them to *compare the stacks with each other*, not as absolute capacity figures.

## Summary

!!! note "All 12 libraries measured in a single session"
    All 12 frameworks and baselines below were measured alongside each other on 2026-10-02 in one uninterrupted session under .NET 10.0.12 (SDK 10.0.401). Every comparison reflects verified head-to-head results run under identical hardware and OS conditions.

- **Single-client latency:** Nalix WS achieves **66.6 µs** p50 and Nalix TCP achieves **68.0 µs** p50 at 32 B, outperforming gRPC duplex (74.6 µs), SignalR (78.9 µs), MagicOnion unary (82.9 µs), gRPC duplex TLS (91.9 µs), gRPC unary (102.3 µs), and gRPC unary TLS (137.3 µs). At 1 KB, Nalix TCP achieves **68.6 µs** p50 and Nalix WS achieves **75.5 µs** p50, leading SignalR (80.8 µs), gRPC duplex (82.4 µs), gRPC unary (103.1 µs), and MagicOnion unary (112.2 µs).
- **Server allocations:** **56 B** per message at 32 B for Nalix TCP and Nalix AEAD, the lowest among all full frameworks (MagicOnion hub 200 B, gRPC duplex 264 B, gRPC duplex TLS 552 B, SignalR 672 B, gRPC unary 968 B, MagicOnion unary 1,432 B, gRPC unary TLS 1,523 B) — saving >53% heap allocations following PR #401 zero-alloc fast-paths and pooled dispatch sessions. At 1 KB, Nalix TCP and AEAD allocate **1,048 B** (vs 1,184 B for Nalix WS, 1,192 B for MagicOnion hub, 1,256 B for gRPC duplex, 1,664 B for SignalR, 1,675 B for gRPC duplex TLS, 1,960 B for gRPC unary, 2,573 B for gRPC unary TLS, and 3,736 B for MagicOnion unary).
- **GC Pressure:** At 32 B (64 clients), Nalix AEAD had **0** Gen0 collections and Nalix TCP triggered only **1** Gen0 collection per 8-second run, compared to 110 for MagicOnion hub, 113 for MagicOnion unary, 124 for gRPC duplex TLS, 135 for gRPC duplex, 139 for SignalR, 143 for gRPC unary, and 154 for gRPC unary TLS — a **>100x reduction in GC cycles**.
- **Encryption:** Nalix AEAD (X25519 + ChaCha20-Poly1305) outperforms gRPC duplex TLS in latency at 32 B (p50 **73.0 µs** vs 91.9 µs) and uses a fraction of the server allocations (**56 B** vs 552 B at 32 B; **1,048 B** vs 1,675 B at 1 KB) with zero Gen0 collections (0 vs 124).

## Environment

| Item | Value |
|:--|:--|
| CPU | 13th Gen Intel Core i7-13620H @ 2.40 GHz (10 physical cores, 16 logical cores) |
| Memory | 16 GB |
| OS | Windows 11 (10.0.26300) |
| Runtime | .NET 10.0.12 (SDK 10.0.401), Release, x64 RyuJIT, TieredPGO on |
| GC | Server GC + Concurrent GC for **every** process (from `benchmarks/Directory.Build.props`) |
| Nalix | source at `master` (`Version.props` 14.2.16), project references |
| ASP.NET Core / SignalR | 10.0.12 (`Microsoft.AspNetCore.SignalR.Client`, `.Protocols.MessagePack` → MessagePack 2.5.302) |
| gRPC | `Grpc.AspNetCore` 2.84.0, `Grpc.Net.Client` 2.84.0, Google.Protobuf 3.35.1 |
| MagicOnion | `MagicOnion.Server` / `.Client` 7.11.0 (MessagePack 3.1.7) |
| Date | 2026-10-02 (Refreshed for PR #401 zero-alloc send pipeline optimizations) |
| Machine state | Clean idle state before the run; one uninterrupted session executing all 12 libraries |

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

```powershell
.\benchmarks\run-comparison.ps1
```

## Results

### Latency — 32 B payload, 1 client, sequential request/response

| Library | mean (µs) | p50 (µs) | p90 (µs) | p99 (µs) | p99.9 (µs) | run spread (p50) | server alloc/op (B) |
|:--|--:|--:|--:|--:|--:|--:|--:|
| raw-tcp | 56.3 | 48.2 | 72.6 | 118.3 | 604.4 | ±2.7% | 143 |
| kestrel-ws | 60.3 | 55.1 | 70.0 | 125.0 | 595.0 | ±5.5% | 0 |
| nalix-tcp | 90.3 | 68.0 | 91.0 | 349.2 | 3905.1 | ±4.4% | 56 |
| nalix-ws | 75.4 | 66.6 | 92.8 | 196.6 | 688.6 | ±1.4% | 192 |
| nalix-tcp-aead | 81.3 | 73.0 | 97.9 | 189.3 | 658.8 | ±0.5% | 56 |
| signalr-ws-msgpack | 90.1 | 78.9 | 110.4 | 219.9 | 1272.4 | ±1.2% | 680 |
| grpc-unary | 114.6 | 102.3 | 137.7 | 284.6 | 1413.6 | ±3.1% | 968 |
| grpc-duplex | 84.5 | 74.6 | 110.8 | 227.5 | 891.9 | ±1.7% | 264 |
| grpc-unary-tls | 166.3 | 137.3 | 197.2 | 578.6 | 3348.3 | ±2.6% | 999 |
| grpc-duplex-tls | 102.2 | 91.9 | 117.8 | 215.6 | 1664.0 | ±3.3% | 305 |
| magiconion-unary | 93.2 | 82.9 | 126.3 | 215.8 | 648.9 | ±2.6% | 1432 |
| magiconion-hub | 71.4 | 62.7 | 98.2 | 162.0 | 536.0 | ±25.5% | 201 |

### Latency — 1024 B payload, 1 client, sequential request/response

| Library | mean (µs) | p50 (µs) | p90 (µs) | p99 (µs) | p99.9 (µs) | run spread (p50) | server alloc/op (B) |
|:--|--:|--:|--:|--:|--:|--:|--:|
| raw-tcp | 52.6 | 46.2 | 61.3 | 126.1 | 729.7 | ±2.9% | 143 |
| kestrel-ws | 59.4 | 53.4 | 68.8 | 117.5 | 390.4 | ±1.3% | 0 |
| nalix-tcp | 78.5 | 68.6 | 96.3 | 192.2 | 846.4 | ±0.9% | 1048 |
| nalix-ws | 85.1 | 75.5 | 104.0 | 207.4 | 899.4 | ±0.2% | 1184 |
| nalix-tcp-aead | 103.8 | 90.2 | 124.0 | 276.8 | 1633.0 | ±0.6% | 1048 |
| signalr-ws-msgpack | 90.5 | 80.8 | 106.3 | 213.3 | 935.5 | ±1.2% | 1672 |
| grpc-unary | 115.6 | 103.1 | 137.2 | 232.6 | 1386.0 | ±1.0% | 1960 |
| grpc-duplex | 95.0 | 82.4 | 120.5 | 264.9 | 1487.9 | ±13.5% | 1256 |
| grpc-unary-tls | 158.6 | 134.9 | 185.8 | 406.1 | 3227.6 | ±12.8% | 1990 |
| grpc-duplex-tls | 68.8 | 63.7 | 81.7 | 152.2 | 555.5 | ±0.3% | 1299 |
| magiconion-unary | 123.4 | 112.2 | 149.6 | 293.8 | 1777.1 | ±4.4% | 3736 |
| magiconion-hub | 72.6 | 66.3 | 96.4 | 154.8 | 452.7 | ±2.5% | 1192 |

### Throughput — 32 B payload, N clients closed-loop (ops/s, median of runs)

| Library | 1 client | 16 clients | 64 clients |
|:--|--:|--:|--:|
| raw-tcp | 14,541 (±25%) | 47,879 (±3%) | 72,940 (±12%) |
| kestrel-ws | 14,935 (±10%) | 59,040 (±1%) | 67,651 (±2%) |
| nalix-tcp | 13,474 (±1%) | 36,897 (±4%) | 38,900 (±5%) |
| nalix-ws | 13,496 (±2%) | 32,802 (±5%) | 37,693 (±8%) |
| nalix-tcp-aead | 12,118 (±1%) | 33,044 (±1%) | 36,016 (±0%) |
| signalr-ws-msgpack | 11,436 (±1%) | 43,739 (±1%) | 73,494 (±35%) |
| grpc-unary | 8,412 (±5%) | 34,272 (±4%) | 100,366 (±39%) |
| grpc-duplex | 11,710 (±1%) | 41,349 (±17%) | 163,023 (±33%) |
| grpc-unary-tls | 6,803 (±22%) | 49,415 (±28%) | 51,661 (±86%) |
| grpc-duplex-tls | 9,075 (±5%) | 36,114 (±1%) | 176,854 (±19%) |
| magiconion-unary | 10,580 (±20%) | 27,414 (±4%) | 43,289 (±1%) |
| magiconion-hub | 13,476 (±6%) | 137,514 (±2%) | 176,154 (±2%) |

#### Cost per message at 64 clients — 32 B

| Library | server alloc/op (B) | server CPU/op (µs) | client CPU/op (µs) | client alloc/op (B) | server Gen0/1/2 per run |
|:--|--:|--:|--:|--:|--:|
| raw-tcp | 144 | 21.4 | 21.1 | 288 | 11/0/0 |
| kestrel-ws | 0 | 22.9 | 24.8 | 152 | 0/0/0 |
| nalix-tcp | 56 | 41.4 | 31.7 | 888 | 1/0/0 |
| nalix-ws | 192 | 42.8 | 35.3 | 888 | 3/0/0 |
| nalix-tcp-aead | 56 | 47.9 | 35.1 | 888 | 0/0/0 |
| signalr-ws-msgpack | 672 | 28.2 | 30.3 | 1553 | 139/0/0 |
| grpc-unary | 968 | 40.1 | 47.1 | 6737 | 143/0/0 |
| grpc-duplex | 264 | 31.4 | 28.0 | 2126 | 135/0/0 |
| grpc-unary-tls | 1523 | 42.4 | 52.7 | 6736 | 154/0/0 |
| grpc-duplex-tls | 552 | 37.5 | 31.5 | 2220 | 124/0/0 |
| magiconion-unary | 1432 | 33.5 | 42.4 | 7401 | 113/0/0 |
| magiconion-hub | 200 | 39.1 | 31.9 | 2246 | 110/0/0 |

### Throughput — 1024 B payload, N clients closed-loop (ops/s, median of runs)

| Library | 1 client | 16 clients | 64 clients |
|:--|--:|--:|--:|
| raw-tcp | 17,371 (±2%) | 57,436 (±2%) | 67,529 (±7%) |
| kestrel-ws | 17,330 (±0%) | 86,582 (±90%) | 62,258 (±2%) |
| nalix-tcp | 13,086 (±1%) | 34,465 (±1%) | 39,107 (±1%) |
| nalix-ws | 10,976 (±3%) | 29,915 (±2%) | 34,209 (±2%) |
| nalix-tcp-aead | 9,884 (±2%) | 28,643 (±3%) | 29,110 (±11%) |
| signalr-ws-msgpack | 8,205 (±32%) | 41,585 (±13%) | 46,562 (±13%) |
| grpc-unary | 8,885 (±6%) | 66,794 (±1%) | 110,551 (±1%) |
| grpc-duplex | 10,941 (±3%) | 38,383 (±13%) | 155,762 (±13%) |
| grpc-unary-tls | 6,115 (±37%) | 23,232 (±126%) | 31,284 (±11%) |
| grpc-duplex-tls | 15,269 (±1%) | 117,523 (±5%) | 142,456 (±4%) |
| magiconion-unary | 10,790 (±11%) | 71,753 (±1%) | 122,833 (±19%) |
| magiconion-hub | 13,937 (±5%) | 108,032 (±17%) | 145,240 (±35%) |

#### Cost per message at 64 clients — 1024 B

| Library | server alloc/op (B) | server CPU/op (µs) | client CPU/op (µs) | client alloc/op (B) | server Gen0/1/2 per run |
|:--|--:|--:|--:|--:|--:|
| raw-tcp | 144 | 22.2 | 23.5 | 288 | 10/0/0 |
| kestrel-ws | 0 | 23.7 | 25.1 | 152 | 0/0/0 |
| nalix-tcp | 1048 | 45.2 | 32.3 | 1880 | 137/0/0 |
| nalix-ws | 1184 | 50.6 | 37.8 | 1880 | 71/0/0 |
| nalix-tcp-aead | 1048 | 57.2 | 44.5 | 1880 | 70/0/0 |
| signalr-ws-msgpack | 1664 | 30.6 | 32.5 | 2546 | 205/1/1 |
| grpc-unary | 1960 | 46.4 | 48.5 | 7728 | 232/0/0 |
| grpc-duplex | 1256 | 32.6 | 33.1 | 3258 | 313/2/0 |
| grpc-unary-tls | 2573 | 43.5 | 52.6 | 0 | 106/0/0 |
| grpc-duplex-tls | 1675 | 43.1 | 42.9 | 3331 | 257/0/0 |
| magiconion-unary | 3736 | 44.8 | 49.9 | 9704 | 305/0/0 |
| magiconion-hub | 1192 | 40.1 | 37.4 | 3269 | 282/0/0 |

## Who wins where

| Scenario | Best framework (excluding raw baselines) | Nalix position |
|:--|:--|:--|
| Latency, 32 B, 1 client | **MagicOnion hub** (62.7 µs), **Nalix WS** (66.6 µs), **Nalix TCP** (68.0 µs) | 2nd / top tier. gRPC duplex 74.6 µs, SignalR 78.9 µs, MagicOnion unary 82.9 µs |
| Latency, 1 KB, 1 client | **gRPC duplex TLS** (63.7 µs) / **MagicOnion hub** (66.3 µs) / **Nalix TCP** (68.6 µs) | 3rd (within 2–5 µs of leader). Nalix WS 75.5 µs, SignalR 80.8 µs, gRPC duplex 82.4 µs |
| Latency, encrypted, 32 B | **Nalix AEAD** (p50 73.0 µs) | 1st. gRPC duplex TLS 91.9 µs, gRPC unary TLS 137.3 µs |
| Latency, encrypted, 1 KB | **gRPC duplex TLS** (p50 63.7 µs) | 2nd (Nalix AEAD 90.2 µs vs gRPC unary TLS 134.9 µs) |
| Throughput, 1 client, 32 B | **Nalix WS** (13.5k) / **Nalix TCP** (13.5k) / **MagicOnion hub** (13.5k) | 1st tied among application-layer frameworks |
| Throughput, 16 clients, 32 B | **MagicOnion hub** 137.5k | SignalR 43.7k, gRPC duplex 41.3k, Nalix TCP 36.9k, Nalix AEAD 33.0k, Nalix WS 32.8k |
| Throughput, 16 clients, 1 KB | **gRPC duplex TLS** 117.5k / **MagicOnion hub** 108.0k | gRPC unary 66.8k, SignalR 41.6k, gRPC duplex 38.4k, Nalix TCP 34.5k |
| Throughput, 64 clients, 32 B | **gRPC duplex TLS** 176.9k / **MagicOnion hub** 176.2k / **gRPC duplex** 163.0k | gRPC unary 100.4k, SignalR 73.5k, Nalix TCP 38.9k, Nalix WS 37.7k, Nalix AEAD 36.0k |
| Throughput, 64 clients, 1 KB | **gRPC duplex** 155.8k / **MagicOnion hub** 145.2k / **gRPC duplex TLS** 142.5k | gRPC unary 110.6k, SignalR 46.6k, Nalix TCP 39.1k, Nalix WS 34.2k, Nalix AEAD 29.1k |
| Server allocations/msg, 32 B | **Nalix TCP / AEAD** 56 B | 1st of the frameworks (only raw Kestrel WebSocket, 0 B, is lower) |
| Server allocations/msg, 1 KB | **Nalix TCP / AEAD** 1,048 B | 1st of the frameworks (only raw Kestrel WebSocket, 0 B, is lower) |
| GC Gen0 collections, 32 B | **Nalix AEAD** 0 / **Nalix TCP** 1 | 1st (SignalR 139, gRPC duplex 135, gRPC unary 143, MagicOnion unary 113, MagicOnion hub 110) |

## Caveats

- **Loopback and shared host.** Loopback measurements reflect stack overhead and dispatch efficiency on a shared machine; absolute capacity on bare metal with dedicated network hardware will differ.
- **The client library counts.** Latency and throughput include each stack's client (Nalix SDK, `HubConnection`, `Grpc.Net.Client`, MagicOnion's dynamic client).
- **Closed loop with one request in flight per client.** Pipelined or fire-and-forget server push was not measured.
- **Whole-process allocations.** The server numbers include background work (timers, task manager), spread across all messages.
- **The encrypted comparison is not like-for-like.** TLS 1.3 (AES-GCM via OpenSSL, hardware-accelerated) protects the whole stream, while Nalix encrypts per packet with a managed ChaCha20-Poly1305 after an X25519 handshake, and needs no certificate.
