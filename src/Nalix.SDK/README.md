# Nalix.SDK

Client-side transport sessions, handshakes, request/response helpers, subscriptions, and cipher
management for Nalix applications.

Nalix.SDK is the client package. It exposes TCP, UDP, and WebSocket sessions, typed request
correlation, session resume helpers, subscription dispatch, and runtime cipher updates.

## Install

```bash
dotnet add package Nalix.SDK
```

## What It Provides

| Area | Purpose | Main types |
| :--- | :--- | :--- |
| Transport sessions | Shared client session base and concrete transports | `TransportSession`, `TcpSession`, `UdpSession`, `WebSocketSession` |
| Request/response | Correlated typed request matching with timeout support | `RequestAsync<TResponse>`, `RequestOptions` |
| Handshake and resume | X25519 connection setup and session resumption | `ConnectAsync`, `ConnectWithResumeAsync` |
| Cipher control | Runtime cipher switching and rotation | `UpdateCipherAsync` |
| Subscriptions | Typed packet event handlers | `On<TPacket>()`, `OnExact<TPacket>()` |
| Options | Client transport and request configuration | `TransportOptions`, `WebSocketTransportOptions`, `RequestOptions` |

## TCP Request

```csharp
using Nalix.SDK.Options;
using Nalix.SDK.Transport;
using Nalix.SDK.Transport.Extensions;

TransportOptions options = new()
{
    Address = "127.0.0.1",
    Port = 57200
};

using TcpSession session = new(options);
await session.ConnectAsync(options.Address, options.Port);

MyResponse response = await session.RequestAsync<MyResponse>(
    new MyRequest { Id = 1 },
    RequestOptions.Default.WithTimeout(5_000));
```

## Subscriptions

```csharp
using Nalix.SDK.Transport.Extensions;

using IDisposable subscription = session.On<ChatMessage>(message =>
{
    Console.WriteLine(message.Text);
});
```

## Design Notes

- Use one `TransportSession` per active client connection.
- Request helpers correlate responses and clean up pending waiters on timeout.
- After `HandshakeAsync()`, a request is encrypted only when `RequestOptions.Encrypt` is `true`, or when it is left `null` and `TransportOptions.EncryptRequestsByDefault` is enabled. The default (`false`) leaves encryption to an outer TLS layer.
- WebSocket clients are useful for browser-compatible deployments behind reverse proxies.

## Documentation

- Package guide: https://ppn.io.vn/packages/nalix-sdk/
- API reference: https://ppn.io.vn/api/sdk/
- WebSocket session: https://ppn.io.vn/api/sdk/websocket-session/
