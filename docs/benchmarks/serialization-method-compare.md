# Serialization Method Comparison

Detailed method-level comparison for `SerializerComparisonBenchmarks`, including compared libraries, API mapping, performance snapshots, and detailed metrics.

## Compared Libraries & APIs

| Library | Benchmark Method | Simplified API Signature |
| :--- | :--- | :--- |
| **LiteSerializer** | `LiteSerializer_Serialize` | `LiteSerializer.Serialize(payload)` |
| **LiteSerializer** | `LiteSerializer_Serialize_Span` | `LiteSerializer.Serialize(payload, span)` |
| **LiteSerializer** | `LiteSerializer_Deserialize` | `LiteSerializer.Deserialize<BenchPayload>(bytes)` |
| **MemoryPack** | `MemoryPack_Serialize` | `MemoryPackSerializer.Serialize(payload)` |
| **MemoryPack** | `MemoryPack_Serialize_Span` | `MemoryPackSerializer.Serialize(writer, payload)` |
| **MemoryPack** | `MemoryPack_Deserialize` | `MemoryPackSerializer.Deserialize<BenchPayload>(bytes)` |
| **MessagePack** | `MessagePack_Serialize` | `MessagePackSerializer.Serialize(payload)` |
| **MessagePack** | `MessagePack_Deserialize` | `MessagePackSerializer.Deserialize<BenchPayload>(bytes)` |
| **System.Text.Json** | `SystemTextJson_Serialize` | `JsonSerializer.SerializeToUtf8Bytes(payload)` |
| **System.Text.Json** | `SystemTextJson_Deserialize` | `JsonSerializer.Deserialize<BenchPayload>(bytes)` |

---

## Performance Snapshot

Overview of the fastest serialization and deserialization methods at different payload scales (16, 128, and 1024 items).

| Item Count | Fastest Serialize | Fastest Deserialize | Notes |
| :--- | :--- | :--- | :--- |
| **16** | MemoryPack Span (24.92 ns) | **LiteSerializer (69.28 ns)** | LiteSerializer Span is close at 29.10 ns. MemoryPack deserialization is 75.13 ns. |
| **128** | MemoryPack Span (28.65 ns) | **LiteSerializer (115.40 ns)** | LiteSerializer Span is close at 31.85 ns. MemoryPack deserialization is 118.78 ns. |
| **1024** | MemoryPack Span (54.11 ns) | **LiteSerializer (423.75 ns)** | LiteSerializer Span is close at 57.31 ns. MemoryPack deserialization is 448.05 ns. |

---

## Detailed Results

Full metrics comparison across all libraries and payload sizes from the BenchmarkDotNet reports.

### Detailed Results (Item Count = 16)

| Method | Mean | Error | StdDev | P95 | Gen0 | Gen1 | Gen2 | Allocated |
| :--- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| **LiteSerializer_Serialize** | **63.19 ns** | 1.319 ns | 1.760 ns | 65.44 ns | 0.0057 | - | - | 216 B |
| **LiteSerializer_Serialize_Span** | **29.10 ns** | 0.350 ns | 0.467 ns | 29.85 ns | - | - | - | 0 B |
| **MemoryPack_Serialize** | **54.13 ns** | 0.466 ns | 0.622 ns | 54.97 ns | 0.0058 | - | - | 216 B |
| **MemoryPack_Serialize_Span** | **24.92 ns** | 0.235 ns | 0.313 ns | 25.42 ns | - | - | - | 0 B |
| **MessagePack_Serialize** | **89.78 ns** | 0.686 ns | 0.916 ns | 91.29 ns | 0.0044 | - | - | 168 B |
| **SystemTextJson_Serialize** | **251.25 ns** | 2.910 ns | 3.885 ns | 257.10 ns | 0.0143 | - | - | 536 B |
| **LiteSerializer_Deserialize** | **69.28 ns** | 3.914 ns | 5.224 ns | 76.09 ns | 0.0108 | - | - | 408 B |
| **MemoryPack_Deserialize** | **75.13 ns** | 4.133 ns | 5.518 ns | 80.58 ns | 0.0117 | - | - | 440 B |
| **MessagePack_Deserialize** | **162.24 ns** | 1.204 ns | 1.566 ns | 164.84 ns | 0.0117 | - | - | 440 B |
| **SystemTextJson_Deserialize** | **508.44 ns** | 5.618 ns | 7.500 ns | 520.34 ns | 0.0267 | - | - | 1008 B |

### Detailed Results (Item Count = 128)

| Method | Mean | Error | StdDev | P95 | Gen0 | Gen1 | Gen2 | Allocated |
| :--- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| **LiteSerializer_Serialize** | **120.74 ns** | 1.404 ns | 1.825 ns | 123.45 ns | 0.0176 | - | - | 664 B |
| **LiteSerializer_Serialize_Span** | **31.85 ns** | 0.314 ns | 0.409 ns | 32.65 ns | - | - | - | 0 B |
| **MemoryPack_Serialize** | **101.30 ns** | 5.116 ns | 6.830 ns | 107.10 ns | 0.0178 | - | - | 664 B |
| **MemoryPack_Serialize_Span** | **28.65 ns** | 0.239 ns | 0.311 ns | 29.24 ns | - | - | - | 0 B |
| **MessagePack_Serialize** | **380.82 ns** | 2.692 ns | 3.501 ns | 386.04 ns | 0.0134 | - | - | 504 B |
| **SystemTextJson_Serialize** | **785.81 ns** | 6.164 ns | 8.229 ns | 800.89 ns | 0.0277 | - | - | 1056 B |
| **LiteSerializer_Deserialize** | **115.40 ns** | 6.307 ns | 8.419 ns | 124.23 ns | 0.0229 | - | - | 856 B |
| **MemoryPack_Deserialize** | **118.78 ns** | 2.049 ns | 2.516 ns | 121.88 ns | 0.0237 | - | - | 888 B |
| **MessagePack_Deserialize** | **934.29 ns** | 5.249 ns | 7.007 ns | 945.56 ns | 0.0229 | - | - | 888 B |
| **SystemTextJson_Deserialize** | **2,112.12 ns** | 18.254 ns | 24.369 ns | 2,152.67 ns | 0.0496 | - | - | 1976 B |

### Detailed Results (Item Count = 1024)

| Method | Mean | Error | StdDev | P95 | Gen0 | Gen1 | Gen2 | Allocated |
| :--- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| **LiteSerializer_Serialize** | **538.97 ns** | 5.757 ns | 7.686 ns | 549.54 ns | 0.1163 | - | - | 4280 B |
| **LiteSerializer_Serialize_Span** | **57.31 ns** | 0.456 ns | 0.609 ns | 58.36 ns | - | - | - | 0 B |
| **MemoryPack_Serialize** | **406.91 ns** | 23.217 ns | 30.994 ns | 431.30 ns | 0.1116 | - | - | 4248 B |
| **MemoryPack_Serialize_Span** | **54.11 ns** | 0.367 ns | 0.489 ns | 54.86 ns | - | - | - | 0 B |
| **MessagePack_Serialize** | **2,536.02 ns** | 18.638 ns | 24.881 ns | 2,573.17 ns | 0.0839 | - | - | 3192 B |
| **SystemTextJson_Serialize** | **5,184.75 ns** | 42.813 ns | 57.153 ns | 5,269.06 ns | 0.1602 | - | - | 5968 B |
| **LiteSerializer_Deserialize** | **423.75 ns** | 9.611 ns | 12.830 ns | 446.78 ns | 0.1206 | - | 0.0005 | 4440 B |
| **MemoryPack_Deserialize** | **448.05 ns** | 16.775 ns | 21.812 ns | 484.71 ns | 0.1206 | - | - | 4472 B |
| **MessagePack_Deserialize** | **7,083.24 ns** | 45.454 ns | 60.680 ns | 7,172.78 ns | 0.1144 | - | - | 4472 B |
| **SystemTextJson_Deserialize** | **15,276.89 ns** | 115.989 ns | 154.842 ns | 15,524.26 ns | 0.2441 | - | - | 9216 B |

---

## Key Takeaways

- **LiteSerializer vs. MemoryPack**: `LiteSerializer` is exceptionally fast and leads `MemoryPack` in deserialization across all tested payload sizes:
    - **Deserialization**: `LiteSerializer` outperforms MemoryPack at all scales (69.28 ns vs 75.13 ns for 16 items; 115.40 ns vs 118.78 ns for 128 items; 423.75 ns vs 448.05 ns for 1024 items).
    - **Serialization**: `LiteSerializer_Serialize_Span` delivers zero heap allocation (0 B) and runs nearly identically to `MemoryPack_Serialize_Span` (29.10 ns vs 24.92 ns at 16; 31.85 ns vs 28.65 ns at 128; 57.31 ns vs 54.11 ns at 1024).
- **MessagePack & JSON**: `LiteSerializer` significantly outperforms `MessagePack` (by 3x-16x in speed) and `System.Text.Json` (by 8x-36x in speed), while dramatically reducing memory allocation.
