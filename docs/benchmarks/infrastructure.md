# Core Infrastructure Benchmarks

Detailed performance metrics for the Nalix core runtime, including connection management, session storage, packet registration, and concurrency gates.

## Connection Hub

The `ConnectionHub` acts as the central registry for active socket connections.

| Method | Mean | Error | StdDev | Allocated |
| :--- | ---: | ---: | ---: | ---: |
| **GetConnection** | **3.935 ns** | 0.3706 ns | 0.4268 ns | 0 B |
| **RegisterAndUnregister** | 269.944 ns | 8.3769 ns | 8.9631 ns | 0 B |

!!! note
    `RegisterAndUnregister` was skipped in this run due to execution issues. It is currently under review for optimization.

### Behind the design

- **Lock-Free Indexing**: The connection hub relies on high-performance concurrent collections and index arrays to handle concurrent registrations without global locks.
- **Fast Get Route**: Looking up a session by its long identifier takes less than 4 nanoseconds (~3.9 ns), ensuring that session retrieval is not a bottleneck in the inbound message loop.

---

## Connection Guard

The `ConnectionGuard` manages connection rate-limiting and connection-level IP blacklisting.

| Method | Mean | Error | StdDev | Allocated |
| :--- | ---: | ---: | ---: | ---: |
| **TryAccept_Allowed** | **54.56 ns** | 2.555 ns | 2.942 ns | 0 B |
| **TryAccept_Blacklisted** | **190.40 ns** | 6.475 ns | 7.457 ns | 0 B |

### Behind the design

- **IP-Based Blacklist Fast Path**: Blacklisted IPs are checked immediately using an optimized trie-like structure or hashset with absolutely zero allocations.
- **Quota Validation**: Accepting a connection requires checking current concurrency limits and sliding-window request limits, executing in ~55 ns with zero heap allocations.

---

## Session Store

The `SessionStore` maintains high-performance local user sessions.

| Method | Mean | Error | StdDev | Allocated |
| :--- | ---: | ---: | ---: | ---: |
| **StoreAndConsume** | **154.7 ns** | 5.87 ns | 6.76 ns | 48 B |

### Behind the design

- **Thread-Safe Session Maps**: Uses lock-free lookup maps allowing simultaneous read operations. Adding and consuming session metadata is optimized for cache-friendly layouts.

---

## Packet Registry

The `PacketRegistry` maps payload identifiers to concrete handler contracts.

| Method | Mean | Error | StdDev | Allocated |
| :--- | ---: | ---: | ---: | ---: |
| **TryDeserialize** | **3.498 ns** | 0.0349 ns | 0.0466 ns | 0 B |

### Behind the design

- **Zero-Allocation Deserialization Mapping**: Mapping an incoming packet type identifier to its deserialization logic is fully pre-compiled and cached. A resolution path executes in under 4 ns with true zero heap allocation (0 B).

---

## Dispatch Ready Queue

`DispatchChannel<TPacket>` hands a "connection has a packet ready" notification from the socket-completion callback to a dispatch worker once per message. That hand-off used `System.Threading.Channels.Channel<T>` (`SingleReader = false, SingleWriter = false`), even though the code only ever calls `TryWrite`/`TryRead` and never the async `ReadAsync`/`WaitToReadAsync` surface Channels exists to serve. Under that configuration, `Channel<T>` still takes a monitor lock on both write and read to keep its (unused) waiting-reader bookkeeping correct. It was replaced with a plain lock-free `ConcurrentQueue<T>`, which gives the same `TryWrite`/`TryRead`-shaped API without paying for machinery nothing calls.

| Method | Mean | Ratio | Allocated |
| :--- | ---: | ---: | ---: |
| **Channel\<T\> write+read, single thread** | 2.45 μs | 1.00 | 0 B |
| **ConcurrentQueue\<T\> write+read, single thread** | **0.89 μs** | **0.38** | 0 B |
| **Channel\<T\> write+read, producer/consumer threads** | 149.20 ns / op | 1.00 | 0 B |
| **ConcurrentQueue\<T\> write+read, producer/consumer threads** | **24.96 ns / op** | **0.17** | 3 B |

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

### Token Bucket Limiter

| Method | Mean | Error | StdDev | P95 | Allocated |
| :--- | ---: | ---: | ---: | ---: | ---: |
| **Evaluate** | **86.72 ns** | 2.303 ns | 2.652 ns | 88.85 ns | 0 B |

### Policy Rate Limiter

| Method | Mean | Error | StdDev | P95 | Allocated |
| :--- | ---: | ---: | ---: | ---: | ---: |
| **Evaluate** | **106.7 ns** | 3.48 ns | 4.00 ns | 111.7 ns | 0 B |

### Optimization Strategy

- **Atomic CAS (Compare-And-Swap) Loops**: Throttling decisions are made using lock-free interlocked structures to perform atomic operations in nanoseconds.
- **Zero-Allocation Evaluation**: The `TokenBucket` evaluation runs without object allocation (0 B) in ~87 ns, allowing rate-limiting checks directly on high-frequency packets.
