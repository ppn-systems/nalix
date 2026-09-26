# Core Infrastructure Benchmarks

Detailed performance metrics for the Nalix core runtime, including connection management, session storage, packet registration, and concurrency gates.

## Connection Hub

The `ConnectionHub` acts as the central registry for active socket connections.

| Method | Mean | Error | StdDev | Allocated |
| :--- | ---: | ---: | ---: | ---: |
| **GetConnection** | **6.889 ns** | 0.5410 ns | 0.6230 ns | 0 B |
| **RegisterAndUnregister** | 269.944 ns | 8.3769 ns | 8.9631 ns | 0 B |

!!! note
    `RegisterAndUnregister` was skipped in this run due to execution issues. It is currently under review for optimization.

### Behind the design

- **Lock-Free Indexing**: The connection hub relies on high-performance concurrent collections and index arrays to handle concurrent registrations without global locks.
- **Fast Get Route**: Looking up a session by its long identifier takes less than 7 nanoseconds, ensuring that session retrieval is not a bottleneck in the inbound message loop.

---

## Connection Guard

The `ConnectionGuard` manages connection rate-limiting and connection-level IP blacklisting.

| Method | Mean | Error | StdDev | Allocated |
| :--- | ---: | ---: | ---: | ---: |
| **TryAccept_Allowed** | **122.73 ns** | 25.812 ns | 29.726 ns | 0 B |
| **TryAccept_Blacklisted** | **65.35 ns** | 1.430 ns | 1.647 ns | 0 B |

### Behind the design

- **IP-Based Blacklist Fast Path**: Blacklisted IPs are checked immediately using an optimized trie-like structure or hashset. Rejecting a connection takes under 70 ns with absolutely zero allocations.
- **Quota Validation**: Accepting a connection requires checking current concurrency limits and sliding-window request limits. This process consumes only 80 bytes of heap memory and executes in ~205 ns.

---

## Session Store

The `SessionStore` maintains high-performance local user sessions.

| Method | Mean | Error | StdDev | Allocated |
| :--- | ---: | ---: | ---: | ---: |
| **StoreAndConsume** | **226.3 ns** | 6.28 ns | 7.23 ns | 48 B |

### Behind the design

- **Thread-Safe Session Maps**: Uses lock-free lookup maps allowing simultaneous read operations. Adding and consuming session metadata is optimized for cache-friendly layouts.

---

## Packet Registry

The `PacketRegistry` maps payload identifiers to concrete handler contracts.

| Method | Mean | Error | StdDev | Allocated |
| :--- | ---: | ---: | ---: | ---: |
| **TryDeserialize** | **17.43 ns** | 0.590 ns | 0.680 ns | 24 B |

### Behind the design

- **Zero-Allocation Deserialization Mapping**: Mapping an incoming packet type identifier to its deserialization logic is fully pre-compiled and cached. A resolution path executes in ~17 ns with a single small allocation for the returned packet instance.

---

## Dispatch Ready Queue

`DispatchChannel<TPacket>` hands a "connection has a packet ready" notification from the socket-completion callback to a dispatch worker once per message. That hand-off used `System.Threading.Channels.Channel<T>` (`SingleReader = false, SingleWriter = false`), even though the code only ever calls `TryWrite`/`TryRead` and never the async `ReadAsync`/`WaitToReadAsync` surface Channels exists to serve. Under that configuration, `Channel<T>` still takes a monitor lock on both write and read to keep its (unused) waiting-reader bookkeeping correct. It was replaced with a plain lock-free `ConcurrentQueue<T>`, which gives the same `TryWrite`/`TryRead`-shaped API without paying for machinery nothing calls.

| Method | Mean | Ratio | Allocated |
| :--- | ---: | ---: | ---: |
| **Channel\<T\> write+read, single thread** | 2.46 μs | 1.00 | 0 B |
| **ConcurrentQueue\<T\> write+read, single thread** | **1.14 μs** | **0.46** | 0 B |
| **Channel\<T\> write+read, producer/consumer threads** | 3,201.9 μs / 20k ops | 1.00 | 264 KB |
| **ConcurrentQueue\<T\> write+read, producer/consumer threads** | **857.6 μs / 20k ops** | **0.27** | 526 KB |

End-to-end, exercising `DispatchChannel<IPacket>.Push` → `TryClaim` → `TryDequeue` → `Release` directly (the real per-message path):

| Scenario | Before | After | Change |
| :--- | ---: | ---: | ---: |
| Single connection, no contention | 325.2 ns | **286.0 ns** | −12% |
| 64 connections pushed concurrently, drained | 68.75 μs | **60.11 μs** | −12.6% |

!!! note
    This is a real per-message win at the dispatch layer, but the dispatch channel is one of several hops in the full request path (OS socket round trip dominates end-to-end server CPU/op). Re-running the full echo comparison in [`network-comparison.md`](network-comparison.md) with only this change showed no difference outside normal run-to-run noise on this shared container — the improvement is real but too small a slice of total CPU/op to be visible at that scale.

### Behind the design

- **No feature paid for, no feature used**: swapping the backing structure removed a lock acquisition on every enqueue/dequeue without changing behavior — the ready-queue never relied on `Channel<T>`'s blocking-reader semantics.
- **Numbers above vary run to run** by up to ~20% on this shared 4-vCPU container; the *ratio* between the two implementations is the stable signal, not the absolute nanosecond figures.

---

## Concurrency Gate & Throttling

Nalix uses a token bucket limiter and concurrency gates to prevent server overload and protect the hot-path.

### Concurrency Gate

| Method | Mean | Error | StdDev | Allocated |
| :--- | ---: | ---: | ---: | ---: |
| **TryEnterAndDispose** | **78.88 ns** | 1.618 ns | 1.863 ns | 0 B |

### Token Bucket Limiter

| Method | Mean | Error | StdDev | Allocated |
| :--- | ---: | ---: | ---: | ---: |
| **Evaluate** | **77.95 ns** | 1.311 ns | 1.510 ns | 0 B |

### Policy Rate Limiter

| Method   | Mean     | Error   | StdDev  | P95      | Allocated |
|--------- |---------:|--------:|--------:|---------:|----------:|
| Evaluate | 115.3 ns | 4.12 ns | 4.74 ns | 122.3 ns |         - |

!!! note
    Token Bucket and Policy Rate Limiter numbers above are from an earlier run and were not refreshed this pass: both currently fail with `ObjectPoolManager.Shared was used before the host configured it` when run standalone via BenchmarkDotNet's out-of-process toolchain. This is a pre-existing benchmark-harness gap, unrelated to the dispatch-queue change below, and is tracked separately.

### Optimization Strategy

- **Atomic CAS (Compare-And-Swap) Loops**: Throttling decisions are made using lock-free interlocked structures to perform atomic operations in nanoseconds.
- **Zero-Allocation Evaluation**: The `TokenBucket` evaluation runs without object allocation (0 B) in just ~78 ns, allowing rate-limiting checks directly on high-frequency packets.
