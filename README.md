<p align="center">
  <img src="docs/assets/!/banner.svg" alt="Nalix Banner" width="100%">
</p>

<p align="center">
  <a href="https://dotnet.microsoft.com/"><img src="https://img.shields.io/badge/.NET-10.0-blueviolet?logo=dotnet&logoColor=white" alt=".NET"></a>
  <a href="https://www.nuget.org/packages/Nalix.Network"><img src="https://img.shields.io/nuget/v/Nalix.Network?logo=nuget&label=NuGet" alt="NuGet"></a>
  <a href="https://www.nuget.org/packages/Nalix.Network"><img src="https://img.shields.io/nuget/dt/Nalix.Network?logo=nuget&label=Downloads" alt="Downloads"></a>
  <a href="https://github.com/ppn-systems/nalix/actions/workflows/ci-linux.yml"><img src="https://github.com/ppn-systems/nalix/actions/workflows/ci-linux.yml/badge.svg?branch=master" alt="CI Linux"></a>
  <a href="https://github.com/ppn-systems/nalix/actions/workflows/ci-windows.yml"><img src="https://github.com/ppn-systems/nalix/actions/workflows/ci-windows.yml/badge.svg?branch=master" alt="CI Windows"></a>
  <a href="LICENSE"><img src="https://img.shields.io/github/license/ppn-systems/nalix" alt="License"></a>
</p>

<p align="center">
  <b><a href="DOCUMENTATION.md">Documentation</a></b> · <b><a href="samples/">Samples</a></b> · <b><a href="#-benchmarks">Benchmarks</a></b> · <b><a href="CONTRIBUTING.md">Contributing</a></b>
</p>

---

**Nalix** is a modular, high-performance networking framework for .NET 10. It provides a complete stack for building real-time server applications — from low-level transport (TCP/UDP) to middleware pipelines, packet routing, and client SDKs — with a focus on zero-allocation hot paths, pluggable protocols, and enterprise-grade security.

## Table of Contents

- [Features](#-features)
- [Architecture](#-architecture)
- [Requirements](#-requirements)
- [Benchmarks](#-benchmarks)
- [Packages](#-nuget-packages)
- [Quick Start](#-quick-start)
- [Installation](#-installation)
- [Contributing](#-contributing)
- [Security](#-security)
- [License](#-license)

---

## ✨ Features

| Category | Highlights |
| :--- | :--- |
| **Cross-Platform** | Runs on Windows, Linux, and macOS with .NET 10+. |
| **High Performance** | Zero-allocation serialization, shard-aware dispatch, and buffer pooling for thousands of concurrent connections. |
| **Security-First** | AEAD encryption (ChaCha20-Poly1305), Static-Ephemeral X25519 (Noise Protocol) with server identity pinning, and zero-RTT session resumption. |
| **Pluggable Protocols** | Swap network, serialization, or security protocols without modifying core logic. |
| **Middleware Pipeline** | Built-in authentication, rate limiting, traffic shaping, and audit logging — or write your own. |
| **Real-Time Updates** | Instant messaging, state synchronization, and live event broadcasting. |
| **Extensible** | Attribute-based packet routing, auto-discovered controllers, and fluent builder APIs. |
| **Modern C#** | Leverages C# 14 features — `Span<T>`, `ref struct`, pattern matching, and more. |

---

## 🏛️ Architecture

Nalix is a layered stack — each package depends only on lower levels, so you install just the layers you need. There are **no circular references**.

```plaintext
Level 4  Nalix.Hosting        Host & builder APIs, bootstrap
Level 3  Nalix.Runtime        Dispatch, middleware, throttling
         Nalix.Network        TCP/UDP transport, sessions
         Nalix.SDK            Client-side sessions & requests
Level 2  Nalix.Codec          Framing, crypto, serialization
         Nalix.Framework      Identity, DI, task orchestration
Level 1  Nalix.Environment    IO primitives, buffer leasing
Level 0  Nalix.Abstractions   Contracts, enums (zero deps)
```

---

## 🔧 Requirements

| Requirement | Version |
| :--- | :--- |
| .NET SDK | [10.0+](https://dotnet.microsoft.com/download/dotnet/10.0) |
| C# Language | 14+ |
| IDE | [Visual Studio 2026](https://visualstudio.microsoft.com/downloads/) / [VS Code](https://code.visualstudio.com/) / [Rider](https://www.jetbrains.com/rider/) |

---

## 📈 Benchmarks

> All benchmarks run on **.NET 10.0**, **Windows 11**, using **BenchmarkDotNet v0.15.8**.

### Environment

- CPU: 13th Gen Intel Core i7-13620H (10C/16T)
- Runtime: .NET `10.0.5` (X64 RyuJIT, Server GC)
- SDK: .NET SDK `10.0.201`
- Job config: `IterationCount=20`, `LaunchCount=3`, `WarmupCount=10`, `RunStrategy=Throughput`

### 🔄 Serialization (128 items, DTO payload)

| Serializer | Serialize | Deserialize | Allocated |
| :--- | ---: | ---: | ---: |
| LiteSerializer | 149.9 ns | 142.9 ns | 664–856 B |
| MemoryPack | 121.6 ns | 145.0 ns | 664–888 B |
| MessagePack | 422.5 ns | 1,095.2 ns | 504–888 B |
| System.Text.Json | 897.7 ns | 2,548.2 ns | 1,976–7,200 B |

> **More details:** See the [`docs/benchmarks`](docs/benchmarks/) folder for full data and additional test cases.

---

## 📦 NuGet Packages

Nalix is composed of several modular packages — install only what you need.

### 🏗️ Foundation

| Package | Description |
| :--- | :--- |
| **[Nalix.Abstractions](src/Nalix.Abstractions)** | Base abstractions, enums, and shared contracts for the Nalix ecosystem. |
| **[Nalix.Codec](src/Nalix.Codec)** | High-performance framing, cryptography, and serialization. |
| **[Nalix.Environment](src/Nalix.Environment)** | Low-level IO primitives, buffer leasing, and configuration loading. |
| **[Nalix.Framework](src/Nalix.Framework)** | High-performance core: cryptography, identity, DI, serialization, and task orchestration. |
| **[Nalix.Runtime](src/Nalix.Runtime)** | Packet dispatching, middleware pipelines, protection primitives, and throttling. |

### 📡 Networking & Hosting

| Package | Description |
| :--- | :--- |
| **[Nalix.Network](src/Nalix.Network)** | High-performance TCP/UDP transport, connection management, and session persistence. |
| **[Nalix.Hosting](src/Nalix.Hosting)** | Microsoft-style host and builder APIs for quick bootstrapping. |

### 🛠️ Utilities & Tooling

| Package | Description |
| :--- | :--- |
| **[Nalix.SDK](src/Nalix.SDK)** | Client-side SDK: transport sessions, request/response patterns, and encryption. |
| **[Nalix.Analyzers](analyzers/Nalix.Analyzers)** | Roslyn analyzers, code fixes, and source generators — packed into `Nalix.Abstractions`. |

---

## 🚀 Quick Start

Build a high-performance network application in minutes:

```csharp
using Nalix.Hosting;
using Nalix.Network.Options;
using Nalix.Runtime.Handlers;
using Nalix.Hosting.Protocols;

// Initialize and configure the application host
using var host = NetworkApplication.CreateBuilder()
    .MapTcp<DefaultProtocol>().OnPort(8080).Bind()
    .MapHandlers<HandshakeHandlers>()
    .Configure<NetworkSocketOptions>(opt => opt.NoDelay = true)
    .Build();

// Run the server
await host.RunAsync();
```

### 📂 Samples

Runnable end-to-end projects, from beginner to production-grade:

| Sample | Demonstrates |
| :--- | :--- |
| **[HelloWorld](samples/HelloWorld)** | Minimal TCP client/server — request/response packets, `[PacketHandler]`, graceful shutdown. Start here. |
| **[ChatRoom](samples/ChatRoom)** | Server push — broadcasting to all clients via `IConnectionBroadcaster`, `session.On<T>()` on the client. |
| **[SecureMultiTransportHelloWorld](samples/SecureMultiTransportHelloWorld)** | TCP + UDP + WebSocket under one secure session — X25519 handshake, AEAD encryption, authenticated UDP. |
| **[BlazorWasm](samples/BlazorWasm)** | Browser client — Blazor WebAssembly over WebSocket with the X25519 handshake, request/response, server push, reconnect, DI-registered session. [Guide](docs/guides/blazor-wasm.md) |

---

## 📦 Installation

```bash
# Core server setup
dotnet add package Nalix.Hosting

# Optional: client SDK
dotnet add package Nalix.SDK

# Optional: Roslyn analyzers + source generators (packed into Abstractions)
dotnet add package Nalix.Abstractions
```

---

## 🛠️ Contributing

Please read [CONTRIBUTING.md](CONTRIBUTING.md) for the development workflow, commit conventions, and pull request guidelines. Follow our [Code of Conduct](CODE_OF_CONDUCT.md) and submit PRs with proper documentation and tests.

## 🛡️ Security

Please review our [Security Policy](SECURITY.md) for supported versions and vulnerability reporting procedures.

## 📜 License

Nalix is copyright &copy; PhcNguyen — provided under the [Apache License, Version 2.0](http://apache.org/licenses/LICENSE-2.0.html).

## 📬 Contact

For questions, suggestions, or support, open an issue on [GitHub Issues](https://github.com/ppn-systems/Nalix/issues) or start a [GitHub Discussion](https://github.com/ppn-systems/Nalix/discussions).

