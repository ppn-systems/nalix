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

## One Host Per Process

A process runs **one** `NetworkApplication` at a time. A host is a façade over process-wide state, so a second
host in the same process does not get its own copy of that state. It replaces the first host's state:

| State | Where it lives | What a second host does to the first |
| :--- | :--- | :--- |
| Service registry (`IConnectionHub`, `IConnectionGuard`, `IPacketDispatch`, `IBufferPoolManager`, `IObjectPoolManager`, `ILogger`, session services) | `InstanceManager.Instance`, one per process. Registration is by type and the last one wins. | Code that resolves a service through the registry, such as handlers, middleware and connections, gets the second host's instance. |
| Server identity for the X25519 handshake | Static fields of `HandshakeHandlers`, also exposed as `HandshakeHandlers.ServerPublicKey` | Both hosts answer handshakes with one identity, so a client that pinned the first host's key fails the handshake. |
| Buffer and object pools | `BufferPoolManager` / `ObjectPoolManager.Shared`, one per process | Pool sizing and reports cover both hosts. |
| Background workers | `TaskManager` from the registry. `DeactivateAsync` waits on the process-wide `net/*` and `time/*` groups. | Stopping one host also waits on the other host's workers, so its shutdown stalls until the other host stops. |
| Options | `ConfigurationManager.Instance`, one per process | Both hosts read the same option instances. |

This is by design. Hot-path code resolves these services from static or process-wide holders, so no per-host
lookup sits on the receive and dispatch paths. The handshake identity is part of the public API as a static
member (`HandshakeHandlers.ServerPublicKey`). Giving each host its own copy would change the public API of
several packages, not just Hosting.

What to do instead:

- **Several ports or transports:** use one host and call `MapTcp<T>()`, `MapUdp<T>()` and `MapWebSocket<T>()`
  as many times as needed. All bindings share one dispatch pipeline, one identity and one set of pools.
- **Isolated servers** (different identities, handlers or limits): run each one in its own process.
- **Tests:** dispose a host (`await app.DisposeAsync()`) before you build the next one. Do not run tests that
  build hosts in parallel.

## Migration Note

`ListenTcp<TProtocol>()`, `ListenUdp<TProtocol>()`, and `ListenWebSocket<TProtocol>()` are
obsolete. Use `MapTcp<TProtocol>()`, `MapUdp<TProtocol>()`, and
`MapWebSocket<TProtocol>()` for new code.

## Documentation

- Package guide: https://ppn.io.vn/packages/nalix-hosting/
- API reference: https://ppn.io.vn/api/hosting/network-application/
