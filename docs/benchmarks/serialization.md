# Serialization Benchmarks

Nalix features a custom binary serialization engine, `LiteSerializer`, designed for maximum throughput and minimal allocation in high-performance network transport.

## LiteSerializer Performance

Detailed micro-benchmarks of standard serialization operations, including unmanaged structs, custom formatters, inline span fills, and formatter resolving.

| Method | Mean | Error | StdDev | P95 | Gen0 | Gen1 | Gen2 | Allocated |
| :--- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| **Serialize_Unmanaged_Small** | **8.4605 ns** | 0.3905 ns | 0.5214 ns | 9.1123 ns | 0.0015 | - | - | 56 B |
| **Serialize_Unmanaged_Large** | **49.7401 ns** | 1.3714 ns | 1.7831 ns | 52.4651 ns | 0.0144 | - | - | 536 B |
| **Deserialize_Unmanaged_Small** | **3.3755 ns** | 0.0330 ns | 0.0441 ns | 3.4550 ns | - | - | - | 0 B |
| **Deserialize_Unmanaged_Large** | **22.0984 ns** | 0.2677 ns | 0.3083 ns | 22.6873 ns | - | - | - | 0 B |
| **Serialize_Formatter** | **58.3652 ns** | 2.3481 ns | 2.6099 ns | 60.5600 ns | 0.0041 | - | - | 152 B |
| **Deserialize_Formatter** | **60.4312 ns** | 0.7158 ns | 0.9556 ns | 62.0313 ns | 0.0079 | - | - | 296 B |
| **Fill_IntoSpan_Small** | **1.0629 ns** | 0.0188 ns | 0.0245 ns | 1.1034 ns | - | - | - | 0 B |
| **Fill_IntoSpan_Large** | **5.1200 ns** | 0.0450 ns | 0.0585 ns | 5.2316 ns | - | - | - | 0 B |
| **Resolve_Formatter** | **0.9009 ns** | 0.0128 ns | 0.0162 ns | 0.9219 ns | - | - | - | 0 B |

### Why Nalix Serialization?

- **Zero-Allocation Deserialization**: Deserialization of unmanaged structures (`Deserialize_Unmanaged_Small` and `Deserialize_Unmanaged_Large`) is fully allocation-free (0 B) and runs in single-digit to low double-digit nanoseconds.
- **Fast Path Span Fills**: Copying primitive data directly into target spans (`Fill_IntoSpan_Small`) takes approximately **1 nanosecond** (~1.06 ns) with zero overhead.
- **Direct Memory Blitting**: For unmanaged structs, `LiteSerializer` uses low-level memory block copying via the `Unsafe` class, producing CPU instructions that operate at raw hardware limits.
- **Aggressive Inlining**: Key methods in the serialization path are decorated with aggressive compilation attributes, allowing RyuJIT to inline and optimize out method call overhead.
