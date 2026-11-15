# Nalix.Environment

Runtime bootstrap, configuration loading, memory I/O primitives, filesystem setup, and timing
utilities for Nalix.

Nalix.Environment is intentionally low level. It provides the infrastructure used by higher-level
packages without depending on networking, hosting, or runtime dispatch code.

## Install

```bash
dotnet add package Nalix.Environment
```

## What It Provides

| Area | Purpose | Main types |
| :--- | :--- | :--- |
| Memory I/O | Span-based readers, writers, and leased buffers | `DataReader`, `DataWriter`, `BufferLease` |
| Configuration | AOT-safe INI loading and option binding | `ConfigurationManager`, `ConfigurationLoader`, `IniConfig` |
| Fragmentation | Multi-chunk payload reassembly and timeout tracking | `FragmentAssembler`, `FragmentHeader`, `FragmentAssemblyResult`, `FragmentStreamId` |
| Filesystem | Cross-platform application directories and permission hardening | `Directories` |
| Time | Unix clock helpers and timing scopes | `Clock`, `TimingScope` |
| Hashing | Non-cryptographic hashing | `XxHash32` |
| Random | Thread-safe pseudo-random and OS CSPRNG helpers | `Csprng`, `OsCsprng` |
| Sequencing | Monotonic sequence validation | `SequenceCounter` |
| Options | Environment-level option models | `MemoryOptions`, `FragmentOptions`, `SequenceOptions` |

## Minimal Usage

```csharp
using Nalix.Environment.Memory;

using DataWriter writer = new(256);

Span<byte> buffer = writer.FreeBuffer;
buffer[0] = 0xAA;
buffer[1] = 0xBB;

writer.Advance(2);

byte[] payload = writer.ToArray();
```

## Design Notes

- `DataReader` and `DataWriter` are ref structs for predictable stack-friendly access.
- `BufferLease` is pooled and must be disposed or returned according to ownership rules.
- Configuration binding is designed for Native AOT scenarios.

## Documentation

- Package guide: https://ppn.io.vn/packages/nalix-environment/
- API reference: https://ppn.io.vn/api/environment/
