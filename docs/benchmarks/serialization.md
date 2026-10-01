# Serialization Benchmarks

Nalix features a custom binary serialization engine, `LiteSerializer`, designed for maximum throughput and minimal allocation in high-performance network transport.

## LiteSerializer Performance

Detailed micro-benchmarks of standard serialization operations, including unmanaged structs, custom formatters, inline span fills, and formatter resolving.

| Method | Mean | Error | StdDev | P95 | Gen0 | Allocated |
| :--- | ---: | ---: | ---: | ---: | ---: | ---: |
| **Serialize_Unmanaged_Small** | **13.307 ns** | 1.0595 ns | 1.2202 ns | 14.524 ns | 0.0015 | 56 B |
| **Serialize_Unmanaged_Large** | **99.932 ns** | 7.0242 ns | 8.0891 ns | 112.509 ns | 0.0144 | 536 B |
| **Deserialize_Unmanaged_Small** | **6.313 ns** | 0.2710 ns | 0.3121 ns | 6.577 ns | - | 0 B |
| **Deserialize_Unmanaged_Large** | **38.592 ns** | 1.3685 ns | 1.5760 ns | 40.117 ns | - | 0 B |
| **Serialize_Formatter** | **91.637 ns** | 7.0983 ns | 8.1744 ns | 102.945 ns | 0.0041 | 152 B |
| **Deserialize_Formatter** | **93.731 ns** | 7.8585 ns | 9.0499 ns | 104.558 ns | 0.0079 | 296 B |
| **Fill_IntoSpan_Small** | **1.836 ns** | 0.1194 ns | 0.1375 ns | 1.950 ns | - | 0 B |
| **Fill_IntoSpan_Large** | **10.825 ns** | 0.4651 ns | 0.5356 ns | 11.316 ns | - | 0 B |
| **Resolve_Formatter** | **1.135 ns** | 0.2158 ns | 0.2485 ns | 1.339 ns | - | 0 B |

### Why Nalix Serialization?

- **Zero-Allocation Deserialization**: Deserialization of unmanaged structures (`Deserialize_Unmanaged_Small` and `Deserialize_Unmanaged_Large`) is fully allocation-free (0 B) and runs in single-digit to low double-digit nanoseconds.
- **Fast Path Span Fills**: Copying primitive data directly into target spans (`Fill_IntoSpan_Small`) takes under **2 nanoseconds** (~1.8 ns) with zero overhead.
- **Direct Memory Blitting**: For unmanaged structs, `LiteSerializer` uses low-level memory block copying via the `Unsafe` class, producing CPU instructions that operate at raw hardware limits.
- **Aggressive Inlining**: Key methods in the serialization path are decorated with aggressive compilation attributes, allowing RyuJIT to inline and optimize out method call overhead.
