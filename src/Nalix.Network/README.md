# Nalix.Network

Low-level TCP, UDP, WebSocket, connection, protocol, and admission-control infrastructure for
Nalix servers.

Nalix.Network owns the transport layer. It accepts sockets, creates connection objects, enforces
connection guard policy, manages active connection registries, and exposes protocol hooks used by
Nalix.Runtime and Nalix.Hosting.

## Install

```bash
dotnet add package Nalix.Network
```

## What It Provides

| Area | Purpose | Main types |
| :--- | :--- | :--- |
| TCP listeners | High-throughput asynchronous TCP accept loops | `TcpListenerBase` |
| UDP listeners | Session-aware UDP receive loops with endpoint pinning | `UdpListenerBase` |
| WebSocket listeners | Raw WebSocket listener support for browser and proxy deployments | `WebSocketListenerBase` |
| Connections | Active connection objects and lifecycle hooks | `Connection`, `WebSocketConnection`, `PassthroughConnection` |
| Connection registry | Sharded active connection storage and broadcast support | `ConnectionHub`, `ConnectionTerminator` |
| Protocols | Transport-neutral protocol lifecycle base | `Protocol` |
| Admission control | Per-IP limits, flood protection, bans, and datagram guards | `ConnectionGuard`, `DatagramGuard` |
| Options | Socket, guard, proxy, WebSocket, and timing-wheel configuration | `NetworkSocketOptions`, `ConnectionGuardOptions`, `ProxyProtocolOptions`, `NetworkWebSocketOptions` |

## Custom TCP Listener

```csharp
using Nalix.Abstractions.Networking;
using Nalix.Network.Listeners.Tcp;

// Frames are handled by the protocol's frame processor (IFrameProcessor.ProcessFrame),
// so a listener subclass only supplies the port, protocol, hub and connection guard.
public sealed class CustomTcpListener(
    ushort port, IProtocol protocol, IConnectionHub hub, IConnectionGuard guard)
    : TcpListenerBase(port, protocol, hub, guard);
```

## Operational Notes

- Use `ConnectionGuard` and `DatagramGuard` for public-facing services.
- Configure trusted proxies before honoring forwarded client IP headers.
- Prefer Nalix.Hosting for application setup unless you are implementing a custom transport.
- TCP supports PROXY protocol handling; WebSocket supports forwarded HTTP headers when enabled.

## Documentation

- Package guide: https://ppn.io.vn/packages/nalix-network/
- API reference: https://ppn.io.vn/api/network/
- WebSocket listener: https://ppn.io.vn/api/network/websocket-listener/
- Trusted proxy options: https://ppn.io.vn/api/options/network/trusted-proxy-options/
