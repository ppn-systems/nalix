# Nalix.Tunneling

> Server-side reverse TCP tunneling module for the Nalix ecosystem. Routes traffic from consumers to registered providers through authenticated, bandwidth-managed socket pipes.

## Overview

Nalix.Tunneling implements a **reverse tunnel** broker pattern: a **Provider** registers a channel on the server, and a **Consumer** requests a connection to that channel. The server orchestrates the handshake, authenticates the provider's data connection with a cryptographic token, then hands off both raw sockets into a high-performance bi-directional pipe — bypassing the Nalix protocol engine entirely for maximum throughput.

This package is intended for **server hosts**. For client-side packets and protocol definitions, see [Nalix.Tunneling.Contracts](../Nalix.Tunneling.Contracts).

## Key Features

| Feature | Description | Key Concept / Type |
| :--- | :--- | :--- |
| 🔌 **Reverse Tunnel Broker** | Providers register named channels; consumers request connections to those channels. The server pairs them automatically. | `ProviderRegistry`, `TunnelRegistry` |
| 🔑 **Proof-of-Possession Data Connections** | Each tunnel generates a 256-bit one-time token. The token never crosses the wire: the provider proves possession with an HMAC over a fresh server nonce. | `Bytes32`, `TunnelReady`, `TunnelChallenge`, `TunnelProof`, `DataConnectionHandler` |
| 🚀 **Raw Socket Piping** | After handshake, sockets are unwrapped from the Nalix engine and piped bi-directionally at OS level — zero protocol overhead. | `TunnelPipe`, `TunnelSession` |
| 📊 **Bandwidth Control** | Optional per-tunnel byte-level rate limiting with async-aware token bucket. | `TokenBucket`, `TunnelOptions.MaxBytesPerSecond` |
| 🔄 **Stolen Data Recovery** | Transparently recovers any TCP bytes already buffered by the Nalix receive loop before transitioning to raw piping. | `TunnelSession.RecoverStolenDataAsync` |
| 🧹 **Stale Request Cleanup** | Opportunistic sweep cancels pending tunnel requests that exceed the timeout window. | `TunnelRegistry.CleanupStale` |
| 🧩 **Builder Integration** | Single `UseTunneling()` extension call to register all handlers and registries. | `TunnelApplicationBuilderExtensions` |

## Key Namespaces

| Namespace | Purpose | Key Types |
| :--- | :--- | :--- |
| `Nalix.Tunneling` | Core tunnel management, registries, piping, and builder extensions | `TunnelSession`, `TunnelPipe`, `TunnelRegistry`, `TunnelSessionRegistry`, `ProviderRegistry`, `TunnelOptions` |
| `Nalix.Tunneling.Handlers` | Packet handlers for the three-phase tunnel protocol | `ProviderHandler`, `ConsumerHandler`, `DataConnectionHandler` |
| `Nalix.Tunneling.Internal` | Internal rate-limiting primitives | `TokenBucket` |
| `Nalix.Tunneling.Packets` | Wire-format packet definitions (shared with Contracts) | `TunnelProvide`, `TunnelProvideAck`, `TunnelConnect`, `TunnelConnectAck`, `TunnelRequest`, `TunnelReady`, `TunnelChallenge`, `TunnelProof` |

## How It Works

```
  Provider                  Server                    Consumer
     |                        |                          |
     |-- TunnelProvide ------>|                          |
     |   (channelId=42)       |                          |
     |<-- TunnelProvideAck ---|                          |
     |   (success=true)       |                          |
     |                        |                          |
     |                        |<-- TunnelConnect --------|
     |                        |   (channelId=42)         |
     |                        |                          |
     |<-- TunnelRequest ------|                          |
     |   (token=T)            |                          |
     |                        |                          |
     |-- [new TCP conn] ----->|                          |
     |-- TunnelReady -------->|                          |
     |   (id=Keccak(T))       |                          |
     |<-- TunnelChallenge ----|                          |
     |   (nonce=N)            |                          |
     |-- TunnelProof -------->|                          |
     |   (mac=HMAC(T,N))      |                          |
     |                        |                          |
     |                        |-- TunnelConnectAck ----->|
     |                        |   (success=true)         |
     |                        |                          |
     |<========= Raw Socket Bi-directional Pipe ========>|
```

### Protocol Phases

1. **Registration** — Provider sends `TunnelProvide` with a `ChannelId`. Server registers it in `ProviderRegistry` and replies with `TunnelProvideAck`.
2. **Negotiation** — Consumer sends `TunnelConnect` with the target `ChannelId`. Server looks up the provider, generates a 256-bit token via `TunnelRegistry`, forwards a `TunnelRequest` to the provider, and waits for the data connection (15 s timeout).
3. **Authentication** — Provider opens a **new TCP connection** and sends `TunnelReady` carrying the public tunnel id `Keccak256(token)`. The server replies with `TunnelChallenge` (fresh nonce, this connection only). The provider answers with `TunnelProof` = `HMAC-Keccak256(key: token, message: nonce)`. `DataConnectionHandler` verifies it and links the data connection to the consumer's pending task. The token itself is never sent on the data connection.
4. **Pipe** — `TunnelSession` unwraps both raw sockets, recovers any stolen bytes from the Nalix receive loops, and starts `TunnelPipe` — a lock-free bi-directional `Socket.SendAsync`/`Socket.ReceiveAsync` pump with optional bandwidth limiting.

## Installation

```bash
dotnet add package Nalix.Tunneling
```

## Quick Start

Register the Tunneling module into your Nalix network application:

```csharp
using Nalix.Hosting;

NetworkApplication host = NetworkApplication.CreateBuilder()
    .UseTunneling()
    .Build();

await host.RunAsync();
```

## Configuration

Tunnel options are loaded from the INI configuration system (`TunnelOptions`).

| Option | Default | Description |
| :--- | :--- | :--- |
| `MaxConcurrentTunnels` | `100` | Maximum number of concurrent tunnel sessions allowed |
| `MaxBytesPerSecond` | `0` (unlimited) | Per-tunnel bandwidth limit in bytes/sec. `0` disables rate limiting |
| `BufferSize` | `8192` | Buffer size (bytes) for socket read/write operations |

## Related Packages

| Package | Description |
| :--- | :--- |
| [Nalix.Tunneling.Contracts](../Nalix.Tunneling.Contracts) | Lightweight client-side packet definitions for the tunnel protocol. Referenced by game clients without pulling in server dependencies. |
