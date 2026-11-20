# Nalix.Traversal

> NAT traversal, STUN signaling, and UDP Reflector relay for peer-to-peer connectivity in the Nalix ecosystem.

## Overview

Nalix.Traversal is a server-side module that enables **NAT hole punching** and **UDP packet reflection** for game clients and peer-to-peer applications running behind NATs. It combines a TCP-based signaling channel for endpoint exchange with a lightweight UDP Reflector relay for cases where direct P2P connectivity cannot be established.

This package is intended for **server hosts**. For client-side packets and utilities, see [Nalix.Traversal.Contracts](../Nalix.Traversal.Contracts).

## Key Features

| Feature | Description | Key Concept / Type |
| :--- | :--- | :--- |
| 🔌 **Signaling Server** | TCP-based handler that brokers STUN-discovered public IP/Port information between two peers for hole punching. | `PeerSignalHandler`, `PeerSignal` |
| 📡 **UDP Reflector Relay** | Raw UDP relay on its own socket that forwards datagrams between peers when direct P2P fails. Token-authenticated, bandwidth-limited. | `ReflectorManager`, `ReflectorSession` |
| 🔑 **Token-Bucket Rate Limiting** | Per-session byte-level rate limiter (burst + sustained fill rate) preventing relay abuse. | `TokenBucket` |
| 🧩 **Builder Integration** | Single `UseTraversal()` extension call to register all handlers, options, and the UDP reflector listener. | `TraversalApplicationBuilderExtensions` |

## Key Namespaces

| Namespace | Purpose | Key Types |
| :--- | :--- | :--- |
| `Nalix.Traversal` | Root namespace with builder extension methods | `TraversalApplicationBuilderExtensions` |
| `Nalix.Traversal.Handlers` | Packet handlers for signaling and reflector session allocation | `PeerSignalHandler`, `ReflectorInitHandler` |
| `Nalix.Traversal.Reflector` | Reflector session management and the UDP relay | `ReflectorManager`, `ReflectorSession` |
| `Nalix.Traversal.Options` | Configuration schema for the Reflector service | `ReflectorOptions` |
| `Nalix.Traversal.Internal` | Internal rate-limiting primitives | `TokenBucket` |
| `Nalix.Traversal.Packets` | Wire-format packet definitions (shared with Contracts) | `PeerSignal`, `NatProbe`, `NatProbeAck`, `ReflectorInit`, `ReflectorAllocated` |

## How It Works

### NAT Hole Punching Flow

```
  Peer A                     Server                    Peer B
    |                          |                          |
    |-- PeerSignal(Request) -->|                          |
    |                          |<-- PeerSignal(Request) --|
    |                          |                          |
    |<-- PeerSignal(CaOffer) --|                          |
    |                          |-- PeerSignal(CaOffer) -->|
    |                          |                          |
    |<====== UDP Hole Punch (NatProbe/NatProbeAck) ======>|
```

### Reflector Relay Flow (Fallback)

```
  Peer A                     Server                    Peer B
    |                          |                          |
    |-- ReflectorInit -------->|                          |
    |   (target=PeerB)         |                          |
    |<-- ReflectorAllocated ---|                          |
    |   (token=T)              |-- ReflectorAllocated --->|
    |                          |   (token=T)              |
    |                          |                          |
    |-- UDP [T | data] ------->|                          |
    |                          |------ UDP [T | data] --->|
    |                          |                          |
    |<============ Reflected UDP Data Stream ============>|
```

## Installation

```bash
dotnet add package Nalix.Traversal
```

## Quick Start

Register the Traversal module into your Nalix network application:

```csharp
using Nalix.Hosting;

NetworkApplication host = NetworkApplication.CreateBuilder()
    .UseTraversal()
    .Build();

await host.RunAsync();
```

## Configuration

Reflector options are loaded from the INI configuration system (`ReflectorOptions`).

| Option | Default | Description |
| :--- | :--- | :--- |
| `Port` | `28001` | UDP port for the Reflector service |
| `BandwidthBurstCapacity` | `512000` (500 KB) | Maximum burst bytes per Reflector session |
| `BandwidthFillRate` | `204800` (200 KB/s) | Sustained bandwidth limit per Reflector session (bytes/sec) |

## Related Packages

| Package | Description |
| :--- | :--- |
| [Nalix.Traversal.Contracts](../Nalix.Traversal.Contracts) | Lightweight client-side packets and protocol definitions. Referenced by game clients without pulling in server dependencies. |

## Documentation

See [Nalix Traversal API Reference](https://ppn.io.vn/api/traversal) for full protocol specifications and integration guides.
