# UdpDatagram — Nalix UDP Sample

A focused sample for Nalix's **authenticated UDP transport**: a client and server that
exchange packets over UDP, secured by the TCP handshake.

## What This Sample Demonstrates

- Hosting TCP and UDP on one port with `MapTcp<T>()` and `MapUdp<T>()`.
- Enabling `UseSecureConnections()` so the X25519 handshake yields a session token and secret.
- Opening a `UdpSession` on the `SessionState` filled in by the TCP handshake.
- Request/response over UDP with `RequestAsync<T>()`.
- Fire-and-forget streaming with `SendAsync()`.
- A handler that is identical for TCP and UDP.

## Folder Structure

```text
UdpDatagram/
├── UdpDatagram.Contracts/   # EchoRequest/EchoResponse/Telemetry packets
├── UdpDatagram.Server/      # MapTcp + MapUdp, EchoHandlers
├── UdpDatagram.Client/      # TcpSession -> UdpSession
├── UdpDatagram.sln
└── README.md
```

## How to Build

```bash
dotnet build samples/UdpDatagram/UdpDatagram.sln
```

## How to Run

Terminal 1 — server:

```bash
dotnet run --project samples/UdpDatagram/UdpDatagram.Server
```

Terminal 2 — client:

```bash
dotnet run --project samples/UdpDatagram/UdpDatagram.Client
```

The server listens on `127.0.0.1:57220` for both TCP and UDP.

## Expected Output

**Client:**

```text
TCP connected and handshake completed.
  SessionToken : <number>
UDP session opened.
UDP echo reply: Sequence=42
Sent 5 telemetry samples.
Done.
```

**Server:** prints `telemetry #n received` for each sample that arrives. UDP does not retransmit,
so a sample can be lost.

## Notes

- **TCP first.** Each datagram carries the session token negotiated during the TCP handshake
  and is authenticated with the shared secret. Without the handshake the server cannot route
  or verify it.
- **Same port.** The server pins a datagram's source endpoint to the TCP connection's endpoint,
  so TCP and UDP share a port.
- On first run `UseSecureConnections()` generates a server key; the client pins it (TOFU).
- This sample targets the published `Nalix.*` 14.3.10 packages. See
  [`SecureMultiTransportHelloWorld`](../SecureMultiTransportHelloWorld) for UDP alongside WebSocket.
