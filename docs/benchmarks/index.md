# Performance Benchmarks

Nalix is engineered for high-throughput, low-latency real-time applications. This documentation provides a comprehensive report of Nalix benchmark suites executed on 2026-05-19.

## Performance Philosophy

The framework achieves exceptional performance through several core design principles:

- **Lock-Free Abstractions**: Minimizing contention using interlocked and thread-local patterns.
- **Zero-Allocation Pipelines**: Reusing memory via advanced pooling for data transfers.
- **Pre-computed Metadata**: Avoiding reflection and string lookups in the hot path.
- **Hardware-Aware Optimizations**: Leveraging SIMD, aggressive inlining, and memory-safe `Span<T>` layouts.

## Benchmark Categories

Explore detailed metrics by subsystem:

- [**Core Infrastructure**](infrastructure.md): Connection Hub, Session Store, Packet Registry, and Throttling (Concurrency Gate, Token Bucket).
- [**Memory & Storage**](memory.md): Buffer Pooling and Object Pooling.
- [**Data Processing**](data-processing.md): LZ4, Framing, and Pipeline Transformations.
- [**Security & Cryptography**](security.md): Handshakes, Envelope Ciphers, and Hashing.
- [**Serialization**](serialization.md): LiteSerializer performance details.
- [**Serialization Method Comparison**](serialization-method-compare.md): Comprehensive compared methods (LiteSerializer, MemoryPack, MessagePack, System.Text.Json).
- [**End-to-End Network Comparison**](network-comparison.md): Real loopback request/response latency, throughput and allocations vs SignalR, gRPC and MagicOnion.

---

## Environment Details

Benchmarks were executed in the following environment:

- **OS**: Windows 11 (10.0.26200.8457/25H2/2025Update/HudsonValley2)
- **CPU**: 13th Gen Intel Core i7-13620H (2.40GHz)
- **Cores**: 10 Physical, 16 Logical
- **Runtime**: .NET 10.0.8 (X64 RyuJIT)
- **Environment**: Performance Power Plan, Server GC Enabled, Concurrent GC Enabled
- **Toolchain**: BenchmarkDotNet v0.15.8
