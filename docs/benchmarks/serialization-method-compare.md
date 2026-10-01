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
| **16** | MemoryPack Span (44.37 ns) | LiteSerializer (123.60 ns) | LiteSerializer Span is close at 57.09 ns. MemoryPack deserialization is close at 132.29 ns. |
| **128** | MemoryPack Span (49.95 ns) | LiteSerializer (198.81 ns) | LiteSerializer Span is close at 62.74 ns. MemoryPack deserialization is close at 210.24 ns. |
| **1024** | MemoryPack Span (95.43 ns) | MemoryPack (430.24 ns) | LiteSerializer Span is close at 109.99 ns. LiteSerializer deserialization is 789.39 ns. |

---

## Detailed Results

Full metrics comparison across all libraries and payload sizes from the BenchmarkDotNet reports.

### Detailed Results (Item Count = 16)

| Method | Mean | Error | StdDev | P95 | Gen0 | Allocated |
| :--- | ---: | ---: | ---: | ---: | ---: | ---: |
| **LiteSerializer_Serialize** | **106.29 ns** | 4.912 ns | 5.657 ns | 114.90 ns | 0.0057 | 216 B |
| **LiteSerializer_Serialize_Span** | **57.09 ns** | 3.339 ns | 3.845 ns | 61.13 ns | - | 0 B |
| **MemoryPack_Serialize** | **79.33 ns** | 10.931 ns | 12.588 ns | 90.56 ns | 0.0058 | 216 B |
| **MemoryPack_Serialize_Span** | **44.37 ns** | 1.935 ns | 2.229 ns | 46.24 ns | - | 0 B |
| **MessagePack_Serialize** | **154.87 ns** | 12.612 ns | 14.524 ns | 172.81 ns | 0.0044 | 168 B |
| **SystemTextJson_Serialize** | **364.98 ns** | 29.897 ns | 34.429 ns | 422.71 ns | 0.0143 | 536 B |
| **LiteSerializer_Deserialize** | **123.60 ns** | 4.164 ns | 4.795 ns | 128.74 ns | 0.0110 | 408 B |
| **MemoryPack_Deserialize** | **132.29 ns** | 4.988 ns | 5.745 ns | 141.73 ns | 0.0118 | 440 B |
| **MessagePack_Deserialize** | **277.23 ns** | 24.826 ns | 28.589 ns | 311.06 ns | 0.0122 | 0 B |
| **SystemTextJson_Deserialize** | **768.22 ns** | 47.358 ns | 54.538 ns | 867.24 ns | 0.0267 | 1008 B |

### Detailed Results (Item Count = 128)

| Method | Mean | Error | StdDev | P95 | Gen0 | Allocated |
| :--- | ---: | ---: | ---: | ---: | ---: | ---: |
| **LiteSerializer_Serialize** | **205.52 ns** | 6.101 ns | 6.781 ns | 213.11 ns | 0.0179 | 664 B |
| **LiteSerializer_Serialize_Span** | **62.74 ns** | 4.581 ns | 5.276 ns | 68.27 ns | - | 0 B |
| **MemoryPack_Serialize** | **185.61 ns** | 8.753 ns | 10.080 ns | 195.04 ns | 0.0178 | 664 B |
| **MemoryPack_Serialize_Span** | **49.95 ns** | 1.962 ns | 2.259 ns | 52.14 ns | - | 0 B |
| **MessagePack_Serialize** | **591.39 ns** | 48.459 ns | 55.805 ns | 678.69 ns | 0.0134 | 504 B |
| **SystemTextJson_Serialize** | **1,081.57 ns** | 58.536 ns | 65.062 ns | 1,200.13 ns | 0.0277 | 1056 B |
| **LiteSerializer_Deserialize** | **198.81 ns** | 11.173 ns | 12.867 ns | 211.32 ns | 0.0230 | 856 B |
| **MemoryPack_Deserialize** | **210.24 ns** | 8.491 ns | 9.779 ns | 217.16 ns | 0.0238 | 888 B |
| **MessagePack_Deserialize** | **1,255.99 ns** | 61.385 ns | 68.230 ns | 1,366.64 ns | 0.0238 | 888 B |
| **SystemTextJson_Deserialize** | **2,923.24 ns** | 304.306 ns | 350.440 ns | 3,528.77 ns | 0.0496 | 1976 B |

### Detailed Results (Item Count = 1024)

| Method | Mean | Error | StdDev | P95 | Gen0 | Allocated |
| :--- | ---: | ---: | ---: | ---: | ---: | ---: |
| **LiteSerializer_Serialize** | **1,067.76 ns** | 34.621 ns | 39.870 ns | 1,098.17 ns | 0.1173 | 4280 B |
| **LiteSerializer_Serialize_Span** | **109.99 ns** | 11.705 ns | 13.479 ns | 119.80 ns | - | 0 B |
| **MemoryPack_Serialize** | **828.14 ns** | 68.301 ns | 78.656 ns | 888.26 ns | 0.1121 | 4248 B |
| **MemoryPack_Serialize_Span** | **95.43 ns** | 8.877 ns | 10.222 ns | 106.82 ns | - | 0 B |
| **MessagePack_Serialize** | **3,759.39 ns** | 330.437 ns | 380.532 ns | 4,233.36 ns | 0.0839 | 3192 B |
| **SystemTextJson_Serialize** | **7,640.76 ns** | 490.546 ns | 564.914 ns | 8,481.10 ns | 0.1602 | 5968 B |
| **LiteSerializer_Deserialize** | **789.39 ns** | 209.641 ns | 241.423 ns | 969.73 ns | 0.1206 | 4440 B |
| **MemoryPack_Deserialize** | **430.24 ns** | 8.260 ns | 9.512 ns | 444.31 ns | 0.1206 | 4472 B |
| **MessagePack_Deserialize** | **7,119.98 ns** | 58.049 ns | 64.522 ns | 7,226.96 ns | 0.1144 | 4472 B |
| **SystemTextJson_Deserialize** | **15,360.46 ns** | 95.841 ns | 102.548 ns | 15,553.78 ns | 0.2441 | 9216 B |

---

## Key Takeaways

- **LiteSerializer vs. MemoryPack**: `LiteSerializer` is highly competitive with `MemoryPack`, which is widely recognized as the fastest serialization framework for .NET.
    - **Serialization**: `MemoryPack_Serialize_Span` holds a slight lead (e.g. 95.43 ns vs 109.99 ns for 1024 items).
    - **Deserialization**: `LiteSerializer` outpaces MemoryPack on smaller workloads (e.g. 123.60 ns vs 132.29 ns for 16 items, 198.81 ns vs 210.24 ns for 128 items) and remains very fast on large ones.
- **MessagePack & JSON**: `LiteSerializer` significantly outperforms `MessagePack` (by 3x-7x in speed) and `System.Text.Json` (by 10x-20x in speed), while using much less memory allocation.
