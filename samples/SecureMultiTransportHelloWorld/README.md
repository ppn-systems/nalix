# SecureMultiTransportHelloWorld

A Nalix sample demonstrating a server with **TCP**, **UDP** (Nalix Datagram Protocol), and **WebSocket**
listeners, all protected by the secure transport model (X25519 handshake + AEAD encryption).
For a UDP-only walkthrough, see [`UdpDatagram`](../UdpDatagram).

## What This Sample Demonstrates

- Building a multi-transport Nalix server using the canonical Hosting API.
- Enabling `UseSecureConnections()` to require X25519 key exchange before any
  application packet is processed.
- Establishing a secure TCP session first, then opening a `DatagramSession` that gets a
  UDP ticket over it and runs on UDP alone (ChaCha20-Poly1305 sealed datagrams).
- Request/response over UDP and over WebSocket with the same handler.
- WebSocket server binding (requires elevated privileges on Windows).

## Why TCP Must Connect Before UDP

Nalix's datagram transport (NDP over UDP) is ticket based. The client opens a secure TCP
session, completes the X25519 handshake, and asks the server for a UDP ticket over it.
`DatagramSession` then derives its own keys from the TCP session secret, and every datagram
is sealed with ChaCha20-Poly1305. After that the session runs on UDP alone.

Without a completed TCP handshake there is no shared secret, so the server refuses the ticket.

## Why `UseSecureConnections()` Is Enabled

`UseSecureConnections()` registers the server-side handshake handlers
(`HandshakeHandlers`, `ProofOfWorkHandlers`) and initializes the X25519
certificate. Without it, clients cannot perform the key exchange that
the datagram session is built on.

## Folder Structure

```text
SecureMultiTransportHelloWorld/
  SecureMultiTransportHelloWorld.Contracts/
    HelloRequestPacket.cs
    HelloResponsePacket.cs
    SecureMultiTransportHelloWorld.Contracts.csproj

  SecureMultiTransportHelloWorld.Server/
    HelloHandlers.cs
    Program.cs
    SecureMultiTransportHelloWorld.Server.csproj

  SecureMultiTransportHelloWorld.Client/
    Program.cs
    SecureMultiTransportHelloWorld.Client.csproj

  SecureMultiTransportHelloWorld.sln
  README.md
```

## How to Build

```bash
dotnet build samples/SecureMultiTransportHelloWorld/SecureMultiTransportHelloWorld.sln
```

## How to Run the Server

```bash
dotnet run --project samples/SecureMultiTransportHelloWorld/SecureMultiTransportHelloWorld.Server
```

The server listens on:

| Transport   | Address                    |
|-------------|----------------------------|
| TCP         | `127.0.0.1:57210`         |
| UDP (NDP)   | `127.0.0.1:57211`         |
| WebSocket   | `ws://127.0.0.1:57212/ws` |

> **Note:** The UDP port is announced to the client in the ticket, so it can differ
> from the TCP port.

## How to Run the Client

In a separate terminal:

```bash
dotnet run --project samples/SecureMultiTransportHelloWorld/SecureMultiTransportHelloWorld.Client
```

## Expected Output

**Server:**

```text
SecureMultiTransport server is running.
  TCP        : 127.0.0.1:57210
  UDP (NDP)  : 127.0.0.1:57211
  WebSocket  : ws://127.0.0.1:57212/ws
Press Ctrl+C to stop.
```

**Client:**

```text
Connected over TCP.
TCP handshake completed. Secure state is ready.
  SessionToken : 822530074371686401
  Encryption   : True
TCP replied: Hello from Nalix!

Sending UDP hello (request/response)...
UDP replied: Hello from Nalix!
TCP connection is still alive: True

Connecting WebSocket...
WebSocket replied: Hello from Nalix!

Done.
```

## Known Limitations

1. **WebSocket requires elevated privileges on Windows.** The server's
   `HttpListener`-based WebSocket listener requires administrator rights.
   The client gracefully handles this by catching the connection error and
   printing `WebSocket test skipped`. On Linux, no special privileges are needed.

2. **Demo handlers use `PermissionLevel.NONE`.** Handlers require
   `PermissionLevel.USER` by default, and a connection that only completed the
   handshake is `NONE`. A real server authenticates the user and raises the
   connection's level instead of lowering the handler's requirement.

3. **First connection generates server certificate.** On first run,
   `UseSecureConnections()` auto-generates an X25519 key pair if no
   certificate file exists. The client uses Trust-On-First-Use (TOFU) to
   pin the server's public key.
