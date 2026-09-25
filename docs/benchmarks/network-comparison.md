# End-to-End Network Comparison

This page reports **real end-to-end, over-the-socket** measurements of Nalix against other .NET realtime
stacks, all run on the same machine with the same harness. Unlike the micro-benchmarks elsewhere in this
section, every number here includes the kernel TCP stack, framing, dispatch, serialization, the thread pool
and the client library.

!!! warning "Read the caveats"
    These numbers come from a **4-vCPU Linux container over loopback**, with client and server sharing the
    same 4 cores. Absolute values are much lower than on bare metal. Use them to *compare the stacks with
    each other*, not as absolute capacity figures.

## Summary

- **Single-client latency (32 B):** Nalix TCP has the lowest latency of the frameworks tested
  (p50 96 µs vs 117 µs gRPC duplex, 134 µs MagicOnion hub, 150 µs SignalR, 189 µs gRPC unary). Only the two
  hand-written baselines (raw socket 52 µs, raw Kestrel WebSocket 91 µs) are faster.
- **Throughput with 16–64 clients:** Nalix **loses** to SignalR, gRPC bidi streaming and MagicOnion StreamingHub.
  At 64 clients / 32 B: Nalix TCP 39.6k ops/s vs gRPC duplex 52.8k, SignalR 46.8k, MagicOnion hub 43.8k.
  With a 1 KB payload the gap is bigger (Nalix TCP 31.1k vs SignalR 46.9k). The cause is server CPU per
  message: 57–74 µs for Nalix vs 33–42 µs for the others.
- **1 KB payloads:** Nalix LZ4 compression is **on by default** for frames of 512 B or more. With random (incompressible)
  payloads it costs about 20 % of throughput. Turning it off (`nalix-tcp-nocomp`) brings 1-client throughput from 8.1k to 10.9k ops/s.
- **Encryption:** Nalix TCP with X25519 + ChaCha20-Poly1305 (`nalix-tcp-aead`) is close to gRPC over TLS for 32 B messages. It is clearly
  slower for 1 KB messages (18.5k vs 26.4k–30.5k ops/s at 64 clients), because the ChaCha20 and Poly1305 code is managed and scalar.
- **Allocations:** at 1 client, Nalix allocates about **2 KB per message** on the server. Almost all of it
  comes from `SemaphoreSlim.WaitAsync(timeout)` in the dispatch worker's idle loop. The inline dispatcher cuts this to 128 B
  per message and raises 64-client throughput by 34 % (39.6k to 52.9k ops/s, level with gRPC duplex). See
  [Nalix hotspots](#nalix-hotspots-found).

## Environment

| Item | Value |
|:--|:--|
| CPU | Intel Xeon Processor @ 2.80 GHz (KVM guest, AVX2/AVX-512), 33 MB L3 |
| Cores | 4 vCPU (client and server processes share them) |
| Memory | 15 GB |
| OS | Ubuntu 24.04.4 LTS, Linux 6.18 (container; IPv6 unavailable) |
| Runtime | .NET 10.0.12 (SDK 10.0.401), Release, x64 RyuJIT, TieredPGO on |
| GC | Server GC + Concurrent GC for **every** process (from `benchmarks/Directory.Build.props`) |
| Nalix | source at `master` 9decd6ab3 (Version.props 14.2.16), project references |
| ASP.NET Core / SignalR | 10.0.12 (`Microsoft.AspNetCore.SignalR.Client`, `.Protocols.MessagePack` → MessagePack 2.5.302) |
| gRPC | `Grpc.AspNetCore` 2.84.0, `Grpc.Net.Client` 2.84.0, Google.Protobuf 3.35.1 |
| MagicOnion | `MagicOnion.Server` / `.Client` 7.11.0 (MessagePack 3.1.7) |
| Date | 2026-09-25 |

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
| `nalix-tcp` | Nalix `ListenTcp<DefaultProtocol>` with a `[PacketHandler]` that echoes via a pooled response (`PacketFactory<T>.Acquire()` + `context.Sender.SendAsync`). The client uses `TcpSession.RequestAsync<T>`. **Default options**, except the per-IP connection quota (see caveats). |
| `nalix-ws` | Same as above over `ListenWebSocket`, with a `WebSocketSession` client. |
| `nalix-tcp-aead` | `UseSecureConnections()` + `UseSystemControl()`. The client runs `HandshakeAsync()` (X25519), then requests go out with `WithEncrypt()` and the handler has `[PacketEncryption(true)]`, so both directions are ChaCha20-Poly1305. |
| `signalr-ws-msgpack` | SignalR hub `byte[] Echo(byte[])`, WebSockets transport only, `SkipNegotiation`, MessagePack protocol, `InvokeAsync<byte[]>`. |
| `grpc-unary` / `grpc-unary-tls` | Unary `rpc Unary(EchoMessage) returns (EchoMessage)` with a `bytes` field, over h2c or HTTP/2+TLS (self-signed ECDSA P-256). |
| `grpc-duplex` / `grpc-duplex-tls` | One long-lived bidirectional stream per client, with one write and one read per operation. |
| `magiconion-unary` / `magiconion-hub` | `IService<T>` `UnaryResult<byte[]>`, or a `StreamingHub` method returning `ValueTask<byte[]>`, over h2c. |
| `nalix-tcp-nocomp` | *Diagnostic.* `nalix-tcp` with LZ4 compression disabled (server `CompressionOptions.Enabled=false`, client `TransportOptions.CompressionEnabled=false`). |
| `nalix-tcp-inline` | *Diagnostic.* `nalix-tcp` with `ConfigureDispatch(o => new InlinePacketDispatcher(o))`. |

- **Every client owns its own connection** (for gRPC and MagicOnion, its own `GrpcChannel` / `SocketsHttpHandler`), so
  "N clients" means N TCP connections for every library.
- **Latency:** one client, sequential calls. 20 000 warm-up calls, then **100 000 timed calls**. Every call is timed
  with `Stopwatch` and the samples are sorted to read exact percentiles.
- **Throughput:** N ∈ {1, 16, 64} clients in a **closed loop** (each client keeps one request in flight). After 2 s of warm-up,
  requests are counted over an 8 s window.
- **Runs:** 3 runs of each cell. The tables show the **median** of the runs, and `±x%` is half of the (max − min)/median spread.
- ASP.NET Core servers use `WebApplication.CreateBuilder`, with logging cleared and the minimum level set to Warning, and Kestrel bound only to loopback. No
  competitor was tuned beyond its documented default setup. The same goes for Nalix: no tuning beyond the quota.
- Raw data (JSON lines, one row per run) is in
  [`data/network-comparison-results.jsonl`](data/network-comparison-results.jsonl). The tables below are produced from it by
  `benchmarks/Nalix.Comparison.Benchmarks/aggregate.py`.

Reproduce with:

```bash
benchmarks/run-comparison.sh                      # everything (~65 min on the machine above)
LIBS="nalix-tcp,grpc-duplex" MO_LIBS="" benchmarks/run-comparison.sh /tmp/cmp --runs 5
```

## Results

The `nalix-tcp-nocomp` and `nalix-tcp-inline` rows are diagnostic variants. They were measured right after the main run, on the same machine and with the same parameters.

### Latency — 32 B payload, 1 client, sequential request/response

| Library | mean (µs) | p50 (µs) | p90 (µs) | p99 (µs) | p99.9 (µs) | run spread (p50) | server alloc/op (B) |
|:--|--:|--:|--:|--:|--:|--:|--:|
| raw-tcp | 70.9 | 51.9 | 132.9 | 211.2 | 764.8 | ±4.3% | 144 |
| kestrel-ws | 99.2 | 90.9 | 147.7 | 254.0 | 1123.9 | ±3.0% | 0 |
| nalix-tcp | 108.1 | 96.4 | 145.9 | 258.4 | 1230.1 | ±2.2% | 2000 |
| nalix-ws | 121.1 | 109.2 | 167.0 | 284.1 | 1273.2 | ±1.7% | 2139 |
| nalix-tcp-aead | 116.4 | 105.0 | 161.1 | 275.6 | 1448.5 | ±6.3% | 2005 |
| signalr-ws-msgpack | 158.8 | 149.6 | 227.4 | 393.0 | 1568.9 | ±1.5% | 680 |
| grpc-unary | 205.8 | 189.3 | 287.2 | 531.3 | 2154.3 | ±2.5% | 969 |
| grpc-duplex | 128.4 | 116.5 | 184.0 | 298.5 | 1306.2 | ±2.8% | 264 |
| grpc-unary-tls | 318.1 | 299.1 | 455.4 | 781.5 | 2514.7 | ±3.6% | 1065 |
| grpc-duplex-tls | 193.6 | 183.3 | 270.4 | 448.6 | 2094.5 | ±2.1% | 334 |
| magiconion-unary | 228.1 | 210.5 | 328.5 | 601.4 | 2005.5 | ±2.9% | 1433 |
| magiconion-hub | 144.9 | 134.0 | 206.5 | 334.5 | 1292.3 | ±4.8% | 200 |
| nalix-tcp-nocomp | 106.0 | 94.9 | 142.6 | 236.4 | 1211.7 | ±0.5% | 2000 |
| nalix-tcp-inline | 99.0 | 88.3 | 141.8 | 236.3 | 1045.8 | ±5.8% | 128 |

### Latency — 1024 B payload, 1 client, sequential request/response

| Library | mean (µs) | p50 (µs) | p90 (µs) | p99 (µs) | p99.9 (µs) | run spread (p50) | server alloc/op (B) |
|:--|--:|--:|--:|--:|--:|--:|--:|
| raw-tcp | 78.5 | 59.3 | 140.0 | 221.8 | 870.3 | ±6.2% | 144 |
| kestrel-ws | 113.8 | 106.8 | 164.6 | 282.2 | 1157.4 | ±2.2% | 0 |
| nalix-tcp | 128.8 | 113.4 | 173.6 | 320.4 | 1548.4 | ±4.5% | 2995 |
| nalix-ws | 145.8 | 134.4 | 194.6 | 327.2 | 1363.4 | ±2.5% | 3133 |
| nalix-tcp-aead | 242.5 | 224.9 | 309.8 | 539.0 | 1937.0 | ±1.9% | 2999 |
| signalr-ws-msgpack | 174.0 | 163.7 | 242.6 | 420.5 | 1836.9 | ±6.1% | 1672 |
| grpc-unary | 208.9 | 192.6 | 298.3 | 487.5 | 2271.2 | ±5.1% | 1961 |
| grpc-duplex | 133.5 | 121.6 | 187.1 | 321.8 | 1503.3 | ±3.4% | 1256 |
| grpc-unary-tls | 370.9 | 342.4 | 524.3 | 928.6 | 2742.5 | ±8.5% | 2068 |
| grpc-duplex-tls | 206.7 | 193.6 | 286.3 | 481.3 | 2150.4 | ±3.2% | 1327 |
| magiconion-unary | 241.8 | 226.4 | 340.1 | 701.5 | 2251.9 | ±4.1% | 3737 |
| magiconion-hub | 147.5 | 137.1 | 210.5 | 342.6 | 1327.8 | ±4.6% | 1192 |
| nalix-tcp-nocomp | 101.0 | 89.8 | 141.1 | 241.3 | 1350.4 | ±5.2% | 2992 |
| nalix-tcp-inline | 121.7 | 109.8 | 172.3 | 294.4 | 1223.3 | ±1.1% | 1120 |

### Throughput — 32 B payload, N clients closed-loop (ops/s, median of runs)

| Library | 1 client | 16 clients | 64 clients |
|:--|--:|--:|--:|
| raw-tcp | 14,696 (±6%) | 71,709 (±1%) | 88,922 (±5%) |
| kestrel-ws | 10,186 (±4%) | 54,613 (±1%) | 67,218 (±4%) |
| nalix-tcp | 8,968 (±1%) | 32,516 (±0%) | 39,566 (±5%) |
| nalix-ws | 8,623 (±1%) | 28,621 (±5%) | 38,876 (±4%) |
| nalix-tcp-aead | 8,321 (±4%) | 30,868 (±2%) | 32,860 (±4%) |
| signalr-ws-msgpack | 6,782 (±12%) | 37,884 (±4%) | 46,834 (±1%) |
| grpc-unary | 5,154 (±9%) | 29,095 (±4%) | 38,406 (±3%) |
| grpc-duplex | 8,197 (±1%) | 41,414 (±2%) | 52,768 (±13%) |
| grpc-unary-tls | 3,053 (±4%) | 22,020 (±2%) | 29,435 (±6%) |
| grpc-duplex-tls | 5,902 (±9%) | 27,820 (±4%) | 37,006 (±11%) |
| magiconion-unary | 4,458 (±7%) | 29,028 (±4%) | 36,226 (±4%) |
| magiconion-hub | 7,656 (±7%) | 37,539 (±7%) | 43,808 (±4%) |
| nalix-tcp-nocomp | 11,273 (±11%) | 32,192 (±4%) | 38,596 (±3%) |
| nalix-tcp-inline | 9,988 (±3%) | 45,891 (±4%) | 52,877 (±2%) |

#### Cost per message at 64 clients — 32 B

| Library | server alloc/op (B) | server CPU/op (µs) | client CPU/op (µs) | client alloc/op (B) | server Gen0/1/2 per run |
|:--|--:|--:|--:|--:|--:|
| raw-tcp | 144 | 20.9 | 20.4 | 287 | 28/0/0 |
| kestrel-ws | 0 | 26.2 | 27.6 | 152 | 0/0/0 |
| nalix-tcp | 239 | 56.8 | 30.8 | 2095 | 17/0/0 |
| nalix-ws | 377 | 56.4 | 35.3 | 2175 | 42/0/0 |
| nalix-tcp-aead | 237 | 68.3 | 39.6 | 2095 | 2/0/0 |
| signalr-ws-msgpack | 672 | 35.0 | 37.8 | 1553 | 97/0/0 |
| grpc-unary | 968 | 41.2 | 48.4 | 6737 | 70/0/0 |
| grpc-duplex | 264 | 32.6 | 32.8 | 2321 | 43/0/0 |
| grpc-unary-tls | 1503 | 58.1 | 63.6 | 6738 | 53/0/0 |
| grpc-duplex-tls | 807 | 48.4 | 46.2 | 2325 | 53/0/0 |
| magiconion-unary | 1432 | 44.0 | 50.6 | 7401 | 75/0/0 |
| magiconion-hub | 200 | 41.9 | 38.8 | 2239 | 24/0/0 |
| nalix-tcp-nocomp | 240 | 58.2 | 31.3 | 2095 | 12/0/0 |
| nalix-tcp-inline | 129 | 35.9 | 29.6 | 2095 | 21/0/0 |

### Throughput — 1024 B payload, N clients closed-loop (ops/s, median of runs)

| Library | 1 client | 16 clients | 64 clients |
|:--|--:|--:|--:|
| raw-tcp | 13,043 (±6%) | 70,250 (±4%) | 85,631 (±3%) |
| kestrel-ws | 9,107 (±3%) | 57,342 (±3%) | 69,631 (±4%) |
| nalix-tcp | 8,097 (±3%) | 28,925 (±7%) | 31,110 (±6%) |
| nalix-ws | 6,983 (±2%) | 23,226 (±1%) | 27,132 (±5%) |
| nalix-tcp-aead | 4,049 (±1%) | 16,804 (±2%) | 18,493 (±3%) |
| signalr-ws-msgpack | 5,828 (±4%) | 32,458 (±3%) | 46,889 (±1%) |
| grpc-unary | 4,953 (±6%) | 26,109 (±3%) | 35,801 (±6%) |
| grpc-duplex | 7,572 (±2%) | 36,806 (±3%) | 44,006 (±11%) |
| grpc-unary-tls | 3,182 (±4%) | 21,813 (±5%) | 26,367 (±4%) |
| grpc-duplex-tls | 4,666 (±5%) | 24,835 (±3%) | 30,510 (±6%) |
| magiconion-unary | 4,013 (±3%) | 24,062 (±1%) | 31,559 (±2%) |
| magiconion-hub | 6,386 (±3%) | 32,147 (±4%) | 36,338 (±4%) |
| nalix-tcp-nocomp | 10,923 (±6%) | 36,217 (±3%) | 37,258 (±4%) |
| nalix-tcp-inline | 8,248 (±5%) | 33,569 (±3%) | 39,930 (±3%) |

#### Cost per message at 64 clients — 1024 B

| Library | server alloc/op (B) | server CPU/op (µs) | client CPU/op (µs) | client alloc/op (B) | server Gen0/1/2 per run |
|:--|--:|--:|--:|--:|--:|
| raw-tcp | 144 | 21.3 | 21.5 | 287 | 36/0/0 |
| kestrel-ws | 0 | 26.2 | 28.1 | 152 | 0/0/0 |
| nalix-tcp | 1211 | 73.6 | 41.3 | 3082 | 116/0/0 |
| nalix-ws | 1370 | 80.0 | 49.7 | 3163 | 118/0/0 |
| nalix-tcp-aead | 1211 | 112.7 | 82.1 | 3084 | 71/0/0 |
| signalr-ws-msgpack | 1664 | 34.8 | 37.5 | 2546 | 208/0/0 |
| grpc-unary | 1961 | 46.2 | 53.0 | 7729 | 135/0/0 |
| grpc-duplex | 1256 | 38.5 | 38.1 | 3341 | 167/0/0 |
| grpc-unary-tls | 2493 | 62.5 | 68.3 | 7729 | 96/0/0 |
| grpc-duplex-tls | 1787 | 55.1 | 52.3 | 3343 | 159/0/0 |
| magiconion-unary | 3737 | 52.5 | 57.7 | 9705 | 180/0/0 |
| magiconion-hub | 1192 | 49.8 | 45.1 | 3262 | 126/0/0 |
| nalix-tcp-nocomp | 1222 | 61.6 | 33.4 | 3080 | 139/0/0 |
| nalix-tcp-inline | 1121 | 50.0 | 38.4 | 3081 | 143/0/0 |

## Who wins where

| Scenario | Best framework (excluding raw baselines) | Nalix position |
|:--|:--|:--|
| Latency, 32 B, 1 client | **Nalix TCP** (p50 96 µs) | 1st. gRPC duplex +21 %, MagicOnion hub +39 %, SignalR +55 % |
| Latency, 1 KB, 1 client | **Nalix TCP** (p50 113 µs), gRPC duplex 122 µs | 1st. Without compression Nalix is 90 µs |
| Latency, encrypted | **Nalix AEAD** at 32 B (105 µs vs gRPC duplex TLS 183 µs); **gRPC duplex TLS** at 1 KB (194 µs vs 225 µs) | Wins small, loses 1 KB |
| Throughput, 16 clients, 32 B | **gRPC duplex** 41.4k | Nalix TCP 32.5k: behind SignalR, MagicOnion hub and gRPC duplex |
| Throughput, 64 clients, 32 B | **gRPC duplex** 52.8k | Nalix TCP 39.6k (4th of 10 frameworks). The inline dispatcher reaches 52.9k |
| Throughput, 64 clients, 1 KB | **SignalR** 46.9k | Nalix TCP 31.1k (6th). No compression 37.3k, inline 39.9k |
| Server allocations/msg at load | **kestrel-ws** 0 B, then MagicOnion hub 200 B | Nalix 239 B at 32 B (good). 1.2 KB at 1 KB (payload `byte[]` + frame) |
| Server allocations/msg at 1 client | MagicOnion hub 200 B, gRPC duplex 264 B | **Worst**: Nalix about 2 000 B (idle-worker wait, see hotspot 1) |

## Nalix hotspots found

Profiles were taken with `dotnet-trace` on the Nalix **server** process, running `nalix-tcp` and `nalix-tcp-aead`
with 64 clients and a 1 KB payload, and 1 client with 32 B for allocations. The profiles used `dotnet-sampled-thread-time` and `gc-verbose`, and the traces
were analysed with TraceEvent. Line numbers refer to commit 9decd6ab3. No Nalix source was changed for this benchmark.

1. **Dispatch worker idle wait allocates and contends.** Location: `src/Nalix.Runtime/Dispatching/PacketDispatchChannel.cs:533`
   (`_wakeSignal.WaitAsync(millisecondsTimeout: 50)`, with the semaphore created at `:78`).
   The comment says "Zero-allocation asynchronous wait", but `SemaphoreSlim.WaitAsync(int)` allocates a
   `TaskNode`, an async state-machine box, a `CancellationPromise<bool>` and a `TimerQueueTimer` for the 50 ms timeout on
   **every** idle→wake transition. In the 1-client allocation trace this path was **87 % of all server allocations**
   (about 1.8 KB per message). `SemaphoreSlim`'s internal `Monitor` lock also shows up as contention
   (`Monitor.Enter_Slowpath` 5 % of non-idle samples, called from the worker loop, `DefaultFrameProcessor.ProcessFrame` and
   `DispatchChannel.PushCore`). Each wake-up is also one more thread-pool hop.
   *Suggested fix:* replace the `SemaphoreSlim` with a per-worker reusable `IValueTaskSource`
   (`ManualResetValueTaskSourceCore<bool>`) auto-reset signal, or a `Channel<T>` of ready sessions, which has allocation-free `ReadAsync`.
   Drop the 50 ms timer and rely on cancellation. Another option is to run the first packet inline on the receive
   thread when the connection's mailbox was empty. The `nalix-tcp-inline` run shows what removing this hop is worth:
   server CPU/op falls from 56.8 to 35.9 µs, 64-client throughput rises from 39.6k to 52.9k (+34 %), and 1-client allocations fall from 2 000 B to 128 B per message.
2. **A thread-pool hop after every send, even with no subscriber.** Location: `src/Nalix.Network/Connections/Connection.cs:762-771`
   (`OnFrameSent`, called from `SocketConnection.Send.cs:652` `INVOKE_POST_CALLBACK`). Every successful send rents
   `ConnectionEventArgs` and queues a Post-lane work item through `AsyncCallback.Invoke`. Only then does the bridge check whether
   `MessageProcessed` has any subscriber (`Connection.cs:860`).
   *Suggested fix:* check `backing?.MessageProcessed is null` before queuing, or keep a "has post handlers" flag.
3. **A thread-pool hop for every received frame.** Location: `src/Nalix.Network/Connections/Connection.cs:320` and `:750`
   (`AsyncCallback.Invoke(MessageProcessingBridge, …, CallbackLane.Process)` → `AsyncCallback.cs:328` `UnsafeQueueUserWorkItem`).
   Together with hotspots 1 and 2, a request/response costs **3 thread-pool hops** on the server, against about 1 for Kestrel, SignalR and gRPC.
   This is the main reason Nalix server CPU/op (57 µs) is 1.4–1.7× that of the ASP.NET-based stacks (33–42 µs).
   *Suggested fix:* when the protocol only pushes into the dispatch channel (as `DefaultFrameProcessor` does), call it synchronously on
   the receive completion instead of queuing. Queuing adds no isolation there.
4. **LZ4 compression is on by default and is tried on every frame of 512 B or more.** Location: `src/Nalix.Codec/Options/CompressionOptions.cs:21,33`, with the SDK
   defaults at `src/Nalix.SDK/Options/TransportOptions.cs:67,73`. On incompressible 1 KB payloads it costs 20–30 % throughput (8.1k to 10.9k ops/s at 1 client,
   31.1k to 37.3k at 64) and adds about 25 µs latency. `LZ4HashTablePool.Rent` also clears the hash table on every call
   (`src/Nalix.Codec/LZ4/Engine/LZ4HashTablePool.cs:39`).
   *Suggested fix:* default compression to off, or raise the threshold. Another option is an adaptive "skip if the last N frames did not
   compress below X %" check per connection.
5. **Every pooled send buffer is fully cleared on return, plus an extra copy.** Location: `src/Nalix.Environment/Memory/BufferLease.cs:155`
   (`ByteArrayPool.Return` → `Return(array, clearArray: true)`) and `src/Nalix.Network/Internal/Transport/SocketConnection.Send.cs:224-230`.
   `SEND_ASYNC_SAFE` rents a fresh array and copies the already-built frame into it only to prepend a 2-byte length header. Clearing
   the whole power-of-two array on every return showed up as `Buffer.ZeroMemoryInternal` (about 2.6 % of non-idle samples plaintext).
   *Suggested fix:* reserve header space in the pipeline's lease and send it in place. Clear only the used length, and only for
   leases that held plaintext secrets.
6. **The encrypted path is slow for 1 KB messages.**
   - `src/Nalix.Codec/Security/Symmetric/ChaCha20.cs:353-360`: scalar ChaCha20 with a byte-by-byte XOR loop and no SIMD. Poly1305
     (`src/Nalix.Codec/Security/Hashing/Poly1305.cs`) uses 32-bit limbs.
   - `src/Nalix.Codec/Security/EnvelopeCipher.cs:224`: `Csprng.Fill(nonce)` makes an OS CSPRNG call for **every packet**
     (`OsCsprng` was 4.5 % of non-idle samples).
   - `src/Nalix.Codec/Transforms/FramePipeline.cs:250-300` (`ProcessOutboundFused`): 12 % of non-idle samples were in
     `Buffer.ZeroMemoryInternal` called directly from this method. The exact inlined callee could not be resolved with
     EventPipe sampling (no `perf` in the container). Likely candidates are the security clears of the ChaCha20/Poly1305 state and the temp region.
     This needs a native profiler to confirm.
   - The fused outbound path also always compresses first, which adds hotspot 4's cost to encrypted 1 KB traffic.
   *Suggested fixes:* use `System.Security.Cryptography.ChaCha20Poly1305` (OpenSSL / CNG, vectorised) when
   `IsSupported`, and keep the managed code as a fallback (same wire format: RFC 8439 AEAD). Batch CSPRNG output into a per-thread
   nonce buffer, or use a counter-based nonce under a per-session key (standard for AEAD transports). Vectorise the XOR with
   `Vector256`/`Vector128`.
7. **The payload array is allocated per request** (`ArrayFormatter<byte>.Deserialize`). This is inherent to a `byte[]` packet field.
   It accounts for most of the 1.1–1.2 KB/op at 1 KB. It is a user-facing design choice. A pooled or `ReadOnlyMemory<byte>`
   field type would remove it.
8. **Client SDK:** `TcpSession.RequestAsync` allocates about 2.1 KB per call (3.1 KB at 1 KB) in the client process, against 150–290 B for the raw
   baselines and 1.5 KB for SignalR. It was not profiled in depth. Client CPU/op is lower than every other framework's client.

Also found while building the benchmark (not a performance issue):

- The default `ConnectionQuotaOptions` (10 connections per IP per 5 s window, `MinConnectionIntervalMs=50`) rejects and **bans**
  127.0.0.1 when 16 or more clients connect at once. The benchmark had to raise these limits. That is expected anti-abuse behaviour, but
  load-testing users will hit it.
- `UseSecureConnections()` constructs the `ConnectionGuard` eagerly
  (`src/Nalix.Hosting/NetworkApplicationBuilder.Extensions.cs:76`), before the deferred `Configure<ConnectionQuotaOptions>()` callbacks
  run in `Build()`. So `Configure<ConnectionQuotaOptions>` is **silently ignored** when secure connections are enabled.
  The benchmark works around this by mutating `ConfigurationManager.Instance.Get<ConnectionQuotaOptions>()` before creating the builder.
- The handshake needs `UseSystemControl()` as well as `UseSecureConnections()`. Without it the client times out waiting for `SessionTofu`
  (`no-handler opcode=1`).

## Caveats

- **Container and loopback.** Loopback in this VM has high latency (a raw-socket round trip p50 is about 52 µs, where bare metal is typically 10–20 µs), and
  4 vCPUs are shared by client and server. Throughput is bounded by total CPU. The server CPU/op and client CPU/op columns
  show where the CPU goes, and they are the most portable numbers here.
- **The client library counts.** Latency and throughput include each stack's client (Nalix SDK, `HubConnection`,
  `Grpc.Net.Client`, MagicOnion's dynamic client).
- **Closed loop with one request in flight per client.** Pipelined or fire-and-forget server push was not measured. Stacks that batch
  writes, such as gRPC and SignalR, might gain more from pipelining.
- **Whole-process allocations.** At low message rates the Nalix server's background allocation (about 65 KB/s when idle: timers,
  TaskManager) is spread across fewer messages. At 1 client (about 9k msg/s) that adds only about 7 B/msg, so it does not explain the 2 KB/msg (see hotspot 1).
- **Excluded libraries.** LiteNetLib (UDP, reliable-ordered) and DotNetty were not benchmarked. They were optional, and neither offers a comparable request/response
  RPC model without writing a custom protocol. Nalix UDP was not benchmarked either. MagicOnion 7.11 built and ran on net10 without issues.
- The comparison between gRPC TLS and Nalix AEAD is not like-for-like. TLS 1.3 (AES-GCM via OpenSSL, hardware-accelerated) protects the whole stream, while
  Nalix encrypts per packet with a managed ChaCha20-Poly1305 after an X25519 handshake.
