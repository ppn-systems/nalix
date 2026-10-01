# Data Processing & Framing Benchmarks

Detailed performance metrics for the Nalix data processing and transformation pipelines, including LZ4 compression and framing transforms.

## Frame Processing Pipeline

The frame pipeline handles end-to-end outbound serialization (compression + encryption) and inbound deserialization (decryption + decompression) processes.

### Pipeline Performance (Payload Size = 64)

| Method | PayloadSize | Mean | Error | StdDev | P95 | Gen0 | Allocated |
| :--- | :--- | ---: | ---: | ---: | ---: | ---: | ---: |
| **ProcessOutbound_CompressOnly** | 64 | **172.0 ns** | 1.21 ns | 1.40 ns | 173.7 ns | - | 0 B |
| **ProcessOutbound_EncryptOnly** | 64 | **571.1 ns** | 3.56 ns | 3.96 ns | 575.9 ns | 0.0048 | 208 B |
| **ProcessOutbound_Full** | 64 | **698.7 ns** | 4.84 ns | 5.58 ns | 707.0 ns | 0.0048 | 208 B |
| **ProcessInbound_DecompressOnly** | 64 | **172.0 ns** | 1.84 ns | 2.12 ns | 174.9 ns | - | 0 B |
| **ProcessInbound_DecryptOnly** | 64 | **1,160.3 ns** | 8.00 ns | 9.21 ns | 1,173.9 ns | 0.0095 | 416 B |
| **ProcessInbound_Full** | 64 | **1,287.7 ns** | 8.90 ns | 9.53 ns | 1,300.5 ns | 0.0134 | 544 B |

### Pipeline Performance (Payload Size = 512)

| Method | PayloadSize | Mean | Error | StdDev | P95 | Gen0 | Allocated |
| :--- | :--- | ---: | ---: | ---: | ---: | ---: | ---: |
| **ProcessOutbound_CompressOnly** | 512 | **441.0 ns** | 3.56 ns | 4.09 ns | 447.8 ns | - | 0 B |
| **ProcessOutbound_EncryptOnly** | 512 | **1,177.2 ns** | 7.53 ns | 8.67 ns | 1,192.2 ns | 0.0286 | 1104 B |
| **ProcessOutbound_Full** | 512 | **1,530.4 ns** | 9.24 ns | 10.64 ns | 1,544.5 ns | 0.0286 | 1104 B |
| **ProcessInbound_DecompressOnly** | 512 | **443.6 ns** | 4.05 ns | 4.50 ns | 450.5 ns | - | 0 B |
| **ProcessInbound_DecryptOnly** | 512 | **2,324.8 ns** | 17.66 ns | 19.63 ns | 2,352.5 ns | 0.0572 | 2208 B |
| **ProcessInbound_Full** | 512 | **2,858.6 ns** | 24.84 ns | 28.60 ns | 2,904.0 ns | 0.0839 | 3232 B |

### Pipeline Performance (Payload Size = 4096)

| Method | PayloadSize | Mean | Error | StdDev | P95 | Gen0 | Gen1 | Allocated |
| :--- | :--- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| **ProcessOutbound_CompressOnly** | 4096 | **1,182.0 ns** | 8.85 ns | 10.19 ns | 1,197.1 ns | - | - | 0 B |
| **ProcessOutbound_EncryptOnly** | 4096 | **6,172.2 ns** | 40.13 ns | 44.60 ns | 6,228.2 ns | 0.2365 | 0.0076 | 8272 B |
| **ProcessOutbound_Full** | 4096 | **7,304.3 ns** | 51.40 ns | 57.13 ns | 7,383.6 ns | 0.2365 | - | 8272 B |
| **ProcessInbound_DecompressOnly** | 4096 | **1,205.0 ns** | 7.17 ns | 7.97 ns | 1,215.2 ns | - | - | 0 B |
| **ProcessInbound_DecryptOnly** | 4096 | **12,472.2 ns** | 98.50 ns | 113.43 ns | 12,648.5 ns | 0.4730 | 0.0153 | 16544 B |
| **ProcessInbound_Full** | 4096 | **14,019.7 ns** | 72.64 ns | 83.65 ns | 14,148.0 ns | 0.6866 | 0.0305 | 24736 B |

### Behind the design

- **Linear Scaling**: Latency scales predictably relative to the payload size.
- **Pipelined Execution**: Outbound processing streams data directly, enabling sub-microsecond to low-microsecond framing for common payload sizes (e.g. 1.53 μs for a 512B payload).

---

## Frame Transformations

Individual pipeline transformers handle compression/decompression and encryption/decryption routines.

### Transformer Performance (Payload Size = 64)

| Method | PayloadSize | Mean | Error | StdDev | P95 | Allocated |
| :--- | :--- | ---: | ---: | ---: | ---: | ---: |
| **Encrypt_AEAD_ChaCha20Poly1305** | 64 | **610.8 ns** | 3.37 ns | 3.74 ns | 616.3 ns | 0 B |
| **Decrypt_AEAD_ChaCha20Poly1305** | 64 | **1,164.5 ns** | 6.01 ns | 6.92 ns | 1,177.1 ns | 0 B |
| **Encrypt_AEAD_Salsa20Poly1305** | 64 | **560.2 ns** | 5.67 ns | 6.30 ns | 571.0 ns | 0 B |
| **Decrypt_AEAD_Salsa20Poly1305** | 64 | **1,062.4 ns** | 5.68 ns | 6.55 ns | 1,070.1 ns | 0 B |
| **Encrypt_Symmetric_ChaCha20** | 64 | **338.2 ns** | 1.51 ns | 1.61 ns | 340.9 ns | 0 B |
| **Decrypt_Symmetric_ChaCha20** | 64 | **578.4 ns** | 3.50 ns | 3.74 ns | 584.3 ns | 0 B |
| **Encrypt_Symmetric_Salsa20** | 64 | **317.7 ns** | 1.98 ns | 2.20 ns | 320.9 ns | 0 B |
| **Decrypt_Symmetric_Salsa20** | 64 | **537.4 ns** | 4.06 ns | 4.51 ns | 545.0 ns | 0 B |
| **Compress_LZ4** | 64 | **175.7 ns** | 0.80 ns | 0.92 ns | 176.9 ns | 0 B |
| **Decompress_LZ4** | 64 | **242.5 ns** | 2.03 ns | 2.34 ns | 245.3 ns | 0 B |

### Transformer Performance (Payload Size = 1024)

| Method | PayloadSize | Mean | Error | StdDev | P95 | Allocated |
| :--- | :--- | ---: | ---: | ---: | ---: | ---: |
| **Encrypt_AEAD_ChaCha20Poly1305** | 1024 | **1,652.3 ns** | 9.13 ns | 10.52 ns | 1,665.8 ns | 0 B |
| **Decrypt_AEAD_ChaCha20Poly1305** | 1024 | **3,265.3 ns** | 17.39 ns | 19.33 ns | 3,295.6 ns | 0 B |
| **Encrypt_AEAD_Salsa20Poly1305** | 1024 | **2,760.1 ns** | 37.46 ns | 38.47 ns | 2,830.6 ns | 0 B |
| **Decrypt_AEAD_Salsa20Poly1305** | 1024 | **5,463.3 ns** | 51.18 ns | 58.94 ns | 5,553.4 ns | 0 B |
| **Encrypt_Symmetric_ChaCha20** | 1024 | **907.0 ns** | 1.72 ns | 1.77 ns | 909.5 ns | 0 B |
| **Decrypt_Symmetric_ChaCha20** | 1024 | **1,726.8 ns** | 4.48 ns | 4.97 ns | 1,735.4 ns | 0 B |
| **Encrypt_Symmetric_Salsa20** | 1024 | **2,020.7 ns** | 8.57 ns | 9.52 ns | 2,033.1 ns | 0 B |
| **Decrypt_Symmetric_Salsa20** | 1024 | **3,933.9 ns** | 22.79 ns | 26.24 ns | 3,980.6 ns | 0 B |
| **Compress_LZ4** | 1024 | **608.1 ns** | 4.36 ns | 5.03 ns | 614.5 ns | 0 B |
| **Decompress_LZ4** | 1024 | **674.4 ns** | 4.64 ns | 5.15 ns | 681.2 ns | 0 B |

### Why Nalix Data Processing?

- **Zero-Allocation Transforms**: Rather than creating intermediate garbage arrays, encryption and compression operate directly on rentals from `BufferPoolManager` using `Span<byte>`, consuming 0 B of heap allocation for transformer operations.
- **Hardware-Friendly Speeds**: Symmetric encryption of 1 KB of data executes in ~900 ns for ChaCha20 and ~2.0 μs for Salsa20.
- **LZ4 Inline Compression**: Built-in LZ4 compression integrates seamlessly with the buffer pipeline, performing a 1 KB compression in ~608 ns and 64 B compression in under 176 ns.
