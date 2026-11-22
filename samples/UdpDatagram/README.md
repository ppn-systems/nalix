# UdpDatagram — Nalix UDP Sample

A focused sample for the **Nalix Datagram Protocol (NDP over UDP)**: a client and server that
exchange packets over UDP, with per-message delivery guarantees.

## What This Sample Demonstrates

- Mapping a datagram listener with `MapDatagram<TProtocol>()` next to a TCP listener.
- Getting a UDP ticket: the client connects over TCP, runs `HandshakeAsync()`, then opens a
  `DatagramSession`, which derives its own keys from the TCP session secret.
- Reliable request/response over UDP with `RequestAsync<T>()` (`ReliableOrdered` by default).
- Fire-and-forget streaming with `SendAsync(packet, DeliveryMethod.UnreliableSequenced)`.
- Choosing how a handler's reply travels with `[PacketDelivery]`.
- The UDP session surviving after the TCP connection is closed.

## Folder Structure

```text
UdpDatagram/
├── UdpDatagram.Contracts/   # EchoRequest/EchoResponse/Telemetry packets
├── UdpDatagram.Server/      # MapTcp + MapDatagram, EchoHandlers
├── UdpDatagram.Client/      # TcpSession -> DatagramSession
├── UdpDatagram.sln
└── README.md
```

## How to Build

From the repository root:

```bash
dotnet build samples/UdpDatagram/UdpDatagram.sln
```

## How to Run

Open **two terminals**.

### Terminal 1 — Start the server

```bash
dotnet run --project samples/UdpDatagram/UdpDatagram.Server
```

### Terminal 2 — Run the client

```bash
dotnet run --project samples/UdpDatagram/UdpDatagram.Client
```

| Transport | Address           | Purpose                       |
|-----------|-------------------|-------------------------------|
| TCP       | `127.0.0.1:57220` | Handshake and UDP ticket only |
| UDP       | `127.0.0.1:57221` | All application traffic       |

## Expected Output

**Server:**

```text
UdpDatagram server is running.
  TCP (ticket) : 127.0.0.1:57220
  UDP          : 127.0.0.1:57221
Press Ctrl+C to stop.
  telemetry #1 received
  ...
```

**Client:**

```text
TCP connected and handshake completed.
UDP session established (server UDP port 57221).
UDP echo reply: Sequence=42
Sent 5 telemetry samples (UnreliableSequenced).
TCP closed.
UDP still works without TCP: Sequence=100
Done.
```

With `UnreliableSequenced`, a telemetry sample can legitimately be lost or skipped.

## Delivery Methods

| Method                | Retransmitted | Order                         |
|-----------------------|---------------|-------------------------------|
| `Unreliable`          | No            | Any                           |
| `UnreliableSequenced` | No            | Older than newest is dropped  |
| `ReliableUnordered`   | Yes           | Any                           |
| `ReliableOrdered`     | Yes           | Send order within the channel |
| `ReliableSequenced`   | Newest only   | Older ones may be skipped     |

## Notes

- TCP is needed once per session: the server only issues a ticket to a connection that
  completed the handshake.
- Frames are sealed with ChaCha20-Poly1305 by the session; no extra encryption flag is needed.
- Handlers require `PermissionLevel.USER` by default. A connection that only completed the
  handshake is `NONE`, so the demo handlers use `[PacketPermission(PermissionLevel.NONE)]`.
  A real server authenticates the user and raises the connection's level instead.
- On first run `UseSecureConnections()` generates a server key; the client pins it (TOFU).
- See [`SecureMultiTransportHelloWorld`](../SecureMultiTransportHelloWorld) for datagrams next to WebSocket.
