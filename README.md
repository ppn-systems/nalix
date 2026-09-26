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
  <b><a href="DOCUMENTATION.md">Documentation</a></b> · <b><a href="samples/">Samples</a></b> · <b><a href="docs/benchmarks/network-comparison.md">Benchmarks</a></b> · <b><a href="CONTRIBUTING.md">Contributing</a></b>
</p>

---

**Nalix** is low-latency binary realtime networking for .NET 10 — for Blazor WebAssembly, MAUI and game servers — with a pooled, near-zero-allocation hot path (~120 B per message on the server, the lowest of the frameworks measured below) and built-in X25519 + AEAD encryption.

You define packets as plain C# classes, write handlers keyed by opcode, and talk to the server over TCP, UDP or WebSocket from the Nalix client SDK. There is no HTTP layer in between.

## Table of Contents

- [Features](#-features)
- [Why Nalix](#-why-nalix)
- [Benchmarks](#-benchmarks)
- [Quick Start](#-quick-start)
- [Samples](#-samples)
- [Requirements](#-requirements)
- [Architecture](#%EF%B8%8F-architecture)
- [Packages](#-nuget-packages)
- [Installation](#-installation)
- [Contributing](#%EF%B8%8F-contributing)
- [Security](#%EF%B8%8F-security)
- [License](#-license)

---

## ✨ Features

| Category | Highlights |
| :--- | :--- |
| **Security built in** | X25519 handshake with server identity pinning, ChaCha20-Poly1305 per packet, and session resumption. No certificates needed. |
| **Multi-transport** | TCP, UDP and WebSocket behind one packet model; browser clients through Blazor WebAssembly. |
| **Performance** | Source-generated serializers, pooled buffers and packets, and shard-aware dispatch. |
| **Middleware pipeline** | Permission, rate limiting, timeout and traffic shaping built in, or plug in your own. |
| **Developer experience** | Attribute-based packet routing, fluent builder APIs, and Roslyn analyzers that catch mistakes at compile time. |
| **Native AOT** | Every package is trimmable and AOT-compatible. |

---

## 🤔 Why Nalix

How Nalix compares with the usual .NET choices for realtime traffic (competitor columns are based on each project's public documentation as of September 2026; corrections welcome):

| | **Nalix** | **SignalR** | **gRPC (.NET)** | **MagicOnion** |
| :--- | :--- | :--- | :--- | :--- |
| Wire format | Custom binary frames, source-generated serializers | JSON or MessagePack over HTTP transports | Protobuf over HTTP/2 | MessagePack over gRPC (HTTP/2) |
| Encryption and handshake | Built in: X25519 handshake with server key pinning, ChaCha20-Poly1305 per packet; no certificate needed | TLS (HTTPS) | TLS | TLS |
| Session resume after reconnect | Yes (session token resume) | Stateful reconnect (.NET 8+) | No | No |
| Transports | TCP, UDP, WebSocket | WebSocket, SSE, long polling | HTTP/2 (HTTP/3) | HTTP/2 |
| Native AOT | Yes (`IsAotCompatible` on every package) | Partial | Yes | Via source generator |
| Browser (Blazor WASM) client | Yes, over WebSocket ([sample](samples/BlazorWasm)) | Yes | gRPC-Web only | No |
| Server middleware | Permission, rate limit, timeout, plus your own | Hub filters | Interceptors | Filters |

**When NOT to use Nalix:**

- You need **HTTP/REST interop**, proxies or API gateways that understand HTTP semantics. Use gRPC or plain ASP.NET Core.
- You have **polyglot clients** (JavaScript, Go, Python, Java). The only client SDK is .NET; gRPC and SignalR have clients for many languages.
- You are on **.NET 8/9 or .NET Framework**. Nalix targets .NET 10 only.
- You are building a **Unity** client. Unity does not run .NET 10, so the SDK cannot be used there today; MagicOnion supports Unity.
- You need **large encrypted payloads at peak throughput**. For 1 KB encrypted messages gRPC over TLS is faster (see below).

---

## 📈 Benchmarks

End-to-end echo over loopback, every framework with its own client, same machine, one session: 4 vCPU Xeon @ 2.80 GHz container,
.NET 10.0.12, `master` 0b58c2824, median of 3 runs. 32 B payload unless noted. Raw data and all cells: **[full comparison](docs/benchmarks/network-comparison.md)**.

| Library | p50 latency (µs) | p99 latency (µs) | ops/s, 64 clients | ops/s, 64 clients, 1 KB | server alloc/op (B) | server CPU/op (µs) |
| :--- | ---: | ---: | ---: | ---: | ---: | ---: |
| **Nalix TCP** | **92** | **312** | 44,287 | 36,880 | **120** | 45.2 |
| Nalix WebSocket | 115 | 403 | 43,016 | 35,809 | 256 | 45.2 |
| SignalR (WebSocket, MessagePack) | 157 | 492 | **45,904** | **38,780** | 672 | **36.3** |
| gRPC bidi stream (h2c) | 123 | 379 | 44,388 | 37,740 | 264 | 39.8 |
| gRPC unary (h2c) | 217 | 748 | 33,187 | 32,226 | 969 | 48.1 |
| MagicOnion StreamingHub | 161 | 557 | 39,715 | 33,729 | 200 | 44.8 |
| MagicOnion unary | 239 | 812 | 32,050 | 28,380 | 1,433 | 49.7 |
| *Nalix TCP + X25519/ChaCha20-Poly1305* | 126 | 416 | 34,645 | 20,171 | 120 | 60.2 |
| *gRPC bidi stream + TLS* | 209 | 702 | 31,970 | 27,049 | 804 | 55.7 |

Latency is one client, sequential calls; allocation and CPU are per message at 64 clients.

- **Nalix wins** single-client latency (p50 and p99, at 32 B and 1 KB) and server allocations per message.
- **Nalix loses** peak throughput at 64 clients to SignalR (by 3–5 %) and roughly ties gRPC bidi streaming; its server CPU per message is about 25 % higher than SignalR's.
- **Encrypted:** Nalix AEAD beats gRPC + TLS for small messages but is about 25 % slower for 1 KB messages (managed ChaCha20-Poly1305 vs hardware AES-GCM).

> **Caveats:** 4 shared vCPUs over container loopback — compare the stacks with each other, not as absolute capacity. Differences of a few percent are within run-to-run noise. The encrypted rows are not like-for-like (per-packet AEAD vs TLS stream).

Micro-benchmarks (serialization, codec, memory, dispatch) are in [`docs/benchmarks`](docs/benchmarks/) — including the dispatch ready-queue's `Channel<T>` → `ConcurrentQueue<T>` swap ([−12% per message at that hop](docs/benchmarks/infrastructure.md#dispatch-ready-queue), not yet distinguishable from noise in the end-to-end numbers above).

---

## 🚀 Quick Start

A request/response round trip, taken from [`samples/HelloWorld`](samples/HelloWorld). Define a packet (shared by client and server):

```csharp
[Packet]
[GenerateFormatter]
[SerializePackable(SerializeLayout.Explicit)]
public sealed partial class HelloRequestPacket
    : PacketBase<HelloRequestPacket>,
      IFixedSizeSerializable,
      IPacketStaticOpcode
{
    public static ushort StaticOpCode => 0x7001;

    [SerializeOrder(0)]
    public byte Greeting { get; set; }
}
// HelloResponsePacket is the same shape: opcode 0x7002, one byte `Message`.
```

Handle it on the server and start a TCP listener:

```csharp
[PacketHandler("HelloWorld.Greetings")]
public static class HelloHandlers
{
    [PacketOpcode(0x7001)]
    public static async ValueTask HandleHelloAsync(IPacketContext<HelloRequestPacket> context)
    {
        // Rent a response packet from the pool (zero-allocation on repeat calls).
        using PacketScope<HelloResponsePacket> lease = PacketFactory<HelloResponsePacket>.Acquire();
        HelloResponsePacket response = lease.Value;
        response.Message = 1; // "Hello from Nalix!"

        await context.Sender.SendAsync(response).ConfigureAwait(false);
    }
}

await using NetworkApplication app = NetworkApplication.CreateBuilder()
    .MapHandlers(typeof(HelloHandlers))
    .MapTcp<DefaultProtocol>().OnPort(57206).Bind()
    .Build();

await app.RunAsync();
```

Call it from the client:

```csharp
using TcpSession session = new(new TransportOptions { Address = "127.0.0.1", Port = 57206 });
await session.ConnectAsync();

HelloResponsePacket response = await session.RequestAsync<HelloResponsePacket>(
    new HelloRequestPacket(), // Greeting defaults to 1
    RequestOptions.Default.WithTimeout(5_000));
```

To encrypt the connection, add `.UseSecureConnections()` to the server builder and `await session.HandshakeAsync();` after connecting on the client. See [`samples/SecureMultiTransportHelloWorld`](samples/SecureMultiTransportHelloWorld).

Run it: `dotnet run --project samples/HelloWorld/HelloWorld.Server`, then `dotnet run --project samples/HelloWorld/HelloWorld.Client` in a second terminal. The snippets above are trimmed from the sample (doc comments, logging and Ctrl+C handling removed) and use the current `MapTcp` API; the sample itself builds and runs as-is against its pinned package version.

## 📂 Samples

Runnable end-to-end projects, from beginner to production-grade:

| Sample | Demonstrates |
| :--- | :--- |
| **[HelloWorld](samples/HelloWorld)** | Minimal TCP client/server — request/response packets, `[PacketHandler]`, graceful shutdown. Start here. |
| **[ChatRoom](samples/ChatRoom)** | Server push — broadcasting to all clients via `IConnectionBroadcaster`, `session.On<T>()` on the client. |
| **[SecureMultiTransportHelloWorld](samples/SecureMultiTransportHelloWorld)** | TCP + UDP + WebSocket under one secure session — X25519 handshake, AEAD encryption, authenticated UDP. |
| **[BlazorWasm](samples/BlazorWasm)** | Browser client — Blazor WebAssembly over WebSocket with the X25519 handshake, request/response, server push, reconnect, DI-registered session. [Guide](docs/guides/blazor-wasm.md) |

---

## 🔧 Requirements

| Requirement | Version |
| :--- | :--- |
| .NET SDK | [10.0+](https://dotnet.microsoft.com/download/dotnet/10.0) — **.NET 10 only** |
| C# Language | 14+ |
| IDE | [Visual Studio 2026](https://visualstudio.microsoft.com/downloads/) / [VS Code](https://code.visualstudio.com/) / [Rider](https://www.jetbrains.com/rider/) |

Every package targets `net10.0` only. The hot path relies on recent runtime and language features (C# 14, `Span<T>`/`ref struct` APIs, static abstract interface members for packet opcodes and formatters), and a single target keeps the packages trimmable and Native AOT-compatible without multi-targeting shims.

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

