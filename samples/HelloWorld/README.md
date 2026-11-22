# HelloWorld — Nalix Client/Server Sample

A minimal, beginner-friendly client/server sample built with **Nalix**.

## What This Sample Demonstrates

- Defining request/response packets with `[Packet]` and `[PacketOpcode]`
- Writing a server packet handler with `[PacketHandler]`
- Bootstrapping a TCP server using `NetworkApplication.CreateBuilder()`
- Connecting a client with `TcpSession` and calling `RequestAsync<TResponse>()`
- Graceful shutdown with `CancellationToken`

## Folder Structure

```text
HelloWorld/
├── HelloWorld.Contracts/        # Shared packet definitions
│   ├── HelloWorld.Contracts.csproj
│   ├── HelloRequestPacket.cs
│   └── HelloResponsePacket.cs
│
├── HelloWorld.Server/           # Server application
│   ├── HelloWorld.Server.csproj
│   ├── Program.cs
│   └── HelloHandlers.cs
│
├── HelloWorld.Client/           # Client application
│   ├── HelloWorld.Client.csproj
│   └── Program.cs
│
├── HelloWorld.sln               # Solution file
└── README.md
```

## How to Build

From the repository root:

```bash
dotnet build samples/HelloWorld/HelloWorld.sln
```

## How to Run

Open **two terminals**.

### Terminal 1 — Start the server

```bash
dotnet run --project samples/HelloWorld/HelloWorld.Server
```

You should see:

```text
HelloWorld server is running on 127.0.0.1:57206.
Press Ctrl+C to stop.
```

### Terminal 2 — Run the client

```bash
dotnet run --project samples/HelloWorld/HelloWorld.Client
```

You should see:

```text
Connected to 127.0.0.1:57206.
Server replied: Hello from Nalix!
```

Press **Ctrl+C** in Terminal 1 to stop the server.

## Project Overview

### HelloWorld.Contracts

Defines the two packets shared between client and server:

| Packet | Opcode | Direction | Purpose |
|--------|--------|-----------|---------|
| `HelloRequestPacket` | `0x7001` | Client → Server | Sends a greeting |
| `HelloResponsePacket` | `0x7002` | Server → Client | Returns a reply |

Both extend `PacketBase<T>`, which provides automatic serialization, pooling,
and header management. The `[Packet]` attribute enables source-generated
packet registry entries, and `[GenerateFormatter]` produces a high-performance
binary serializer at compile time.

### HelloWorld.Server

Bootstraps the server using the canonical Hosting API:

```csharp
NetworkApplication.CreateBuilder()
    .UseLogger(logger)
    .MapHandlers(typeof(HelloHandlers))
    .MapTcp<DefaultProtocol>().OnPort(57206).Bind()
    .Build();
```

`HelloHandlers` is a static class marked with `[PacketHandler]`.
Its `HandleHelloAsync` method is routed by `[PacketOpcode(0x7001)]`.

### HelloWorld.Client

Uses `Nalix.SDK.Transport.TcpSession` to connect and exchange packets:

```csharp
using TcpSession session = new(options);
await session.ConnectAsync("127.0.0.1", 57206);

HelloResponsePacket response = await session.RequestAsync<HelloResponsePacket>(
    new HelloRequestPacket(),
    RequestOptions.Default.WithTimeout(5_000));
```

`RequestAsync<TResponse>` handles subscribe → send → await → timeout
→ unsubscribe internally, so no response is ever missed.

## Packet Handler Flow

```text
Client                          Server
  │                                │
  │  HelloRequestPacket (0x7001)   │
  │ ─────────────────────────────► │
  │                                │  PacketDispatchChannel
  │                                │  routes to HelloHandlers.HandleHelloAsync
  │                                │
  │  HelloResponsePacket (0x7002)  │
  │ ◄───────────────────────────── │
  │                                │
```

## Troubleshooting

### Port already in use

Another process is listening on port `57206`. Stop it, or change the port
constant in `Program.cs` (server) and the `TransportOptions` (client).

### Client cannot connect

Make sure the server is running **before** you start the client.
Verify that `127.0.0.1:57206` is reachable (no firewall blocking it).

### Request times out although the server is running

Handlers require `PermissionLevel.USER` by default, and a connection that has not
authenticated is `NONE`, so the request is dropped. The sample handler lowers the
requirement with `[PacketPermission(PermissionLevel.NONE)]`; a real server
authenticates the user and raises the connection's level instead.

### Packet handler not found at runtime

Ensure the `HelloWorld.Contracts` assembly is loaded.
The source-generated `ModuleInitializer` registers packets automatically
when the assembly loads. If you removed the `[Packet]` attribute or changed
the base class, re-add it and rebuild.

### Build fails with a restore error

This sample uses the published `Nalix.*` NuGet packages (see the versions in the
`.csproj` files). Make sure `https://api.nuget.org/v3/index.json` is reachable and
that the .NET 10 SDK is installed.
