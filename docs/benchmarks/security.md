# Security & Cryptography Benchmarks

Detailed performance metrics for the Nalix security primitives, including encryption ciphers, handshake computation, and hashing algorithms.

## Envelope Cipher Suites

Comparison of stream ciphers (Salsa20 and ChaCha20) and AEAD suites (Salsa20-Poly1305 and ChaCha20-Poly1305) over 64 B and 1024 B payloads. All operations are run on zero-allocation spans.

### Envelope Cipher Performance (Payload Size = 64)

| Method | PayloadSize | Mean | Error | StdDev | P95 | Allocated |
| :--- | :--- | ---: | ---: | ---: | ---: | ---: |
| **Encrypt_Salsa20** | 64 | **331.0 ns** | 10.13 ns | 11.66 ns | 342.7 ns | 0 B |
| **Decrypt_Salsa20** | 64 | **260.2 ns** | 5.27 ns | 6.07 ns | 265.0 ns | 0 B |
| **Encrypt_Chacha20** | 64 | **343.4 ns** | 4.49 ns | 5.17 ns | 347.8 ns | 0 B |
| **Decrypt_Chacha20** | 64 | **260.1 ns** | 6.18 ns | 7.12 ns | 263.5 ns | 0 B |
| **Encrypt_Salsa20Poly1305** | 64 | **734.6 ns** | 27.18 ns | 31.30 ns | 752.7 ns | 0 B |
| **Decrypt_Salsa20Poly1305** | 64 | **712.0 ns** | 95.41 ns | 109.87 ns | 773.4 ns | 0 B |
| **Encrypt_Chacha20Poly1305** | 64 | **511.2 ns** | 5.81 ns | 6.69 ns | 520.5 ns | 0 B |
| **Decrypt_Chacha20Poly1305** | 64 | **484.0 ns** | 3.58 ns | 4.13 ns | 492.0 ns | 0 B |

### Envelope Cipher Performance (Payload Size = 1024)

| Method | PayloadSize | Mean | Error | StdDev | P95 | Allocated |
| :--- | :--- | ---: | ---: | ---: | ---: | ---: |
| **Encrypt_Salsa20** | 1024 | **1,895.6 ns** | 25.27 ns | 28.09 ns | 1,940.0 ns | 0 B |
| **Decrypt_Salsa20** | 1024 | **1,887.3 ns** | 21.79 ns | 25.09 ns | 1,928.4 ns | 0 B |
| **Encrypt_Chacha20** | 1024 | **1,127.3 ns** | 265.28 ns | 305.50 ns | 1,419.2 ns | 0 B |
| **Decrypt_Chacha20** | 1024 | **1,301.6 ns** | 59.30 ns | 68.29 ns | 1,334.3 ns | 0 B |
| **Encrypt_Salsa20Poly1305** | 1024 | **4,526.5 ns** | 64.45 ns | 74.22 ns | 4,583.9 ns | 0 B |
| **Decrypt_Salsa20Poly1305** | 1024 | **4,606.4 ns** | 12.49 ns | 14.39 ns | 4,623.0 ns | 0 B |
| **Encrypt_Chacha20Poly1305** | 1024 | **2,695.9 ns** | 59.62 ns | 68.66 ns | 2,743.3 ns | 0 B |
| **Decrypt_Chacha20Poly1305** | 1024 | **2,736.3 ns** | 60.69 ns | 69.89 ns | 2,788.0 ns | 0 B |

### Behind the design

- **Software-Efficient Stream Ciphers**: Salsa20 and ChaCha20 perform encryption at near-hardware speeds. The state is maintained in `stackalloc` memory, ensuring zero allocations.
- **One-Pass AEAD**: The combined ciphers with Poly1305 perform authentication and decryption in a single pass over the memory block, minimizing cache misses.

---

## Handshake Protocol

Performance metrics for the cryptographic handshake phase, establishing session identity.

| Method | Mean | Error | StdDev | P95 | Allocated |
| :--- | ---: | ---: | ---: | ---: | ---: |
| **ComputeMasterSecret** | **2.513 μs** | 0.1719 μs | 0.1980 μs | 2.614 μs | 0 B |
| **ComputeServerProof** | **2.194 μs** | 0.2906 μs | 0.3346 μs | 2.609 μs | 0 B |
| **ComputeClientProof** | **2.368 μs** | 0.3165 μs | 0.3645 μs | 2.654 μs | 0 B |
| **DeriveSessionKey** | **2.619 μs** | 0.0755 μs | 0.0869 μs | 2.675 μs | 0 B |

### Behind the design

- **Zero-Allocation Keys**: High-cost key computations take ~2–2.6 μs each and run completely allocation-free (0 B), isolating cryptographic handshakes from garbage collection impacts.

---

## Hashing & Cryptography

Comparison of cryptographic hashing, verification ciphers, and key derivation algorithms.

### Hashing Performance (Payload Size = 64)

| Method | PayloadSize | Mean | Error | StdDev | P95 | Allocated |
| :--- | :--- | ---: | ---: | ---: | ---: | ---: |
| **Keccak256_Hash** | 64 | **605.3 ns** | 11.16 ns | 12.85 ns | 615.0 ns | 0 B |
| **HmacKeccak256_Compute** | 64 | **2,518.4 ns** | 45.27 ns | 52.13 ns | 2,553.1 ns | 0 B |
| **Poly1305_Compute** | 64 | **111.5 ns** | 0.95 ns | 1.09 ns | 112.3 ns | 0 B |
| **Pbkdf2_Hash** | 64 | **2,561,666.8 ns** | 43,716.23 ns | 50,343.68 ns | 2,609,432.4 ns | 312 B |
| **Pbkdf2_Verify** | 64 | **2,548,772.0 ns** | 39,314.36 ns | 45,274.48 ns | 2,590,398.3 ns | 256 B |

### Hashing Performance (Payload Size = 1024)

| Method | PayloadSize | Mean | Error | StdDev | P95 | Allocated |
| :--- | :--- | ---: | ---: | ---: | ---: | ---: |
| **Keccak256_Hash** | 1024 | **4,862.0 ns** | 64.07 ns | 71.22 ns | 4,967.1 ns | 0 B |
| **HmacKeccak256_Compute** | 1024 | **6,591.5 ns** | 140.27 ns | 161.54 ns | 6,758.3 ns | 0 B |
| **Poly1305_Compute** | 1024 | **888.9 ns** | 57.31 ns | 66.00 ns | 940.5 ns | 0 B |
| **Pbkdf2_Hash** | 1024 | **2,545,073.2 ns** | 49,667.27 ns | 57,196.90 ns | 2,608,114.0 ns | 312 B |
| **Pbkdf2_Verify** | 1024 | **2,552,527.7 ns** | 49,817.62 ns | 57,370.04 ns | 2,601,772.5 ns | 256 B |

### Behind the design

- **Keccak Optimization**: Our custom Keccak256 implementation hashes small structures in ~605 ns and 1KB structures in 4.86 μs without allocating memory.
- **PBKDF2 Overhead & Isolation**: PBKDF2 is a secure key derivation function designed to be intentionally computationally expensive (taking ~2.5 ms per operation). Due to its CPU-bound nature, PBKDF2 checks are isolated from the networking hot path to prevent starvation of the listener thread pool.
