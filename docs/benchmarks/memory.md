# Memory & Storage Benchmarks

Nalix uses a highly optimized memory management subsystem designed to eliminate Garbage Collection (GC) pauses in high-throughput workloads.

## Buffer Pooling & Leases

Comparison of memory acquisition strategies across various sizes (64 B, 1 KB, and 16 KB).

### Buffer Allocation Metrics (Size = 64)

| Method | Mean | Error | StdDev | P95 | Ratio | Allocated | Alloc Ratio |
| :--- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| **RawAllocation** | **16.974 ns** | 1.3931 ns | 1.6043 ns | 19.503 ns | 1.01 | 88 B | 1.00 |
| **ArrayPool_Shared** | **10.824 ns** | 0.3976 ns | 0.4579 ns | 11.244 ns | 0.64 | 0 B | 0.00 |
| **BufferPoolManager_RentReturn** | **23.905 ns** | 0.6627 ns | 0.7632 ns | 24.545 ns | 1.42 | 0 B | 0.00 |
| **BufferLease_RentDispose** | **54.052 ns** | 0.5018 ns | 0.5778 ns | 54.617 ns | 3.21 | 0 B | 0.00 |

### Buffer Allocation Metrics (Size = 1024)

| Method | Mean | Error | StdDev | P95 | Ratio | Allocated | Alloc Ratio |
| :--- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| **RawAllocation** | **159.487 ns** | 29.9061 ns | 34.4399 ns | 194.306 ns | 1.06 | 1048 B | 1.00 |
| **ArrayPool_Shared** | **6.406 ns** | 0.0902 ns | 0.1039 ns | 6.556 ns | 0.04 | 0 B | 0.00 |
| **BufferPoolManager_RentReturn** | **24.173 ns** | 0.4341 ns | 0.4999 ns | 24.471 ns | 0.16 | 0 B | 0.00 |
| **BufferLease_RentDispose** | **62.729 ns** | 1.1419 ns | 1.3150 ns | 63.688 ns | 0.42 | 0 B | 0.00 |

### Buffer Allocation Metrics (Size = 16384)

| Method | Mean | Error | StdDev | P95 | Ratio | Allocated | Alloc Ratio |
| :--- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| **RawAllocation** | **2,608.051 ns** | 452.9858 ns | 521.6591 ns | 3,035.733 ns | 1.062 | 16408 B | 1.00 |
| **ArrayPool_Shared** | **10.496 ns** | 0.8494 ns | 0.9782 ns | 11.159 ns | 0.004 | 0 B | 0.00 |
| **BufferPoolManager_RentReturn** | **23.769 ns** | 0.6516 ns | 0.7504 ns | 24.451 ns | 0.010 | 0 B | 0.00 |
| **BufferLease_RentDispose** | **53.938 ns** | 1.3191 ns | 1.5190 ns | 54.873 ns | 0.022 | 0 B | 0.00 |

### Why Nalix Memory?

- **Tiered Buffer Rental**: The `BufferPoolManager` optimizes throughput using a multi-path strategy:
    - **Fast Path**: Common block sizes (256B to 4KB) bypass expensive lookup logic via direct array index resolutions.
    - **Adaptive Allocation**: Large allocations fall back to standard `ArrayPool<byte>.Shared` to prevent memory spikes, but return memory cleanly to avoid fragmentation.
- **Lease Safety**: `BufferLease` provides a scope-bound, auto-disposed rental wrapper that keeps rent/return usage allocation-free in the latest benchmark while reducing leak risk in high-complexity code.
- **Trimming & Stability**: The system implements automated shrinking policies to safely return unused memory blocks to the operating system during long periods of low load, keeping the application footprint minimal.

---

## Object Pooling

Memory metrics for reusing class instances via object pools.

| Method | Mean | Error | StdDev | P95 | Ratio | Allocated | Alloc Ratio |
| :--- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| **RawAllocation** | **8.016 ns** | 0.4384 ns | 0.5049 ns | 8.474 ns | 1.00 | 32 B | 1.00 |
| **RentAndReturn_ObjectPool** | **157.624 ns** | 5.2398 ns | 6.0342 ns | 161.363 ns | 19.74 | 0 B | 0.00 |

### Behind the design

- **Object Reusability**: Reusing instances of complex structures (like session states, packet envelopes, and processing contexts) prevents Gen 0 GC thrashing.
- **Hybrid Fast-Path Architecture**:
    - **Thread-Local Cache**: First-level lock-free lookup caches object instances in thread-local storage (`ThreadLocalCache<T>`) for ultra-low latency reuse on the same thread.
    - **Type-Indexed Buckets**: When thread-local slots are empty or full, retrieval falls back to an array-backed lookup using compiled type IDs (`PoolType<T>.Id`) to bypass slow hashing or generic dictionary lookups.
- **Reset Logic**: To prevent state pollution across rentals, pooled objects implement a reset interface (`IPoolable`) that automatically wipes data and prepares the instance for reuse upon its return to the pool.
