# Nalix.Hosting

Builder-style hosting API for Nalix network servers.

Nalix.Hosting provides the top-level application bootstrap layer. It wires protocols, listeners,
packet handlers, options, logging integration, diagnostics, and lifecycle management behind a
compact builder API.

## Install

```bash
dotnet add package Nalix.Hosting
```

## What It Provides

| Area | Purpose | Main types |
| :--- | :--- | :--- |
| Application host | Start, stop, run, and dispose a Nalix server | `NetworkApplication` |
| Builder API | Configure listeners, handlers, options, and services | `NetworkApplicationBuilder`, `INetworkApplicationBuilder` |
| Protocol binding | Bind TCP, UDP, and WebSocket protocols | `MapTcp<TProtocol>()`, `MapUdp<TProtocol>()`, `MapWebSocket<TProtocol>()` |
| Handler registration | Register generated packet handlers explicitly | `MapHandlers<T>()`, `MapHandlers(Type)` |
| Default protocol | Forward packets into the dispatch pipeline with minimal setup | `DefaultProtocol` |
| Options | Host bootstrap and runtime settings | `HostingOptions` |

## Minimal Server

```csharp
using Nalix.Hosting;

await using NetworkApplication app = NetworkApplication.CreateBuilder()
    .MapTcp<DefaultProtocol>()
        .OnPort(8080)
        .Bind()
    .MapHandlers<MyPacketHandler>()
    .Build();

await app.RunAsync();
```

## Multiple Transports

```csharp
using Nalix.Hosting;
using Nalix.Network.Options;

NetworkApplicationBuilder builder = NetworkApplication.CreateBuilder();

builder.MapTcp<DefaultProtocol>()
    .OnPort(57200)
    .Bind();

builder.MapUdp<DefaultProtocol>()
    .OnPort(57201)
    .Bind();

builder.MapWebSocket<DefaultProtocol>()
    .OnPort(57207)
    .WithPath("/ws/")
    .Bind();

builder.Configure<NetworkSocketOptions>(options =>
{
    options.NoDelay = true;
    options.BufferSize = 65536;
});

builder.MapHandlers<MyPacketHandler>();

await using NetworkApplication app = builder.Build();
await app.RunAsync();
```

## Migration Note

`ListenTcp<TProtocol>()`, `ListenUdp<TProtocol>()`, and `ListenWebSocket<TProtocol>()` are
obsolete. Use `MapTcp<TProtocol>()`, `MapUdp<TProtocol>()`, and
`MapWebSocket<TProtocol>()` for new code.

## Documentation

- Package guide: https://ppn.io.vn/packages/nalix-hosting/
- API reference: https://ppn.io.vn/api/hosting/network-application/
