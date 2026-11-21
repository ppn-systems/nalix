# Nalix.Hosting

## Triggers
- Bootstrapping a new Nalix server application
- Adding handlers, middleware, or custom protocols
- Configuring lifecycle (start/stop) behavior
- Modifying transport bindings or options

---

## Rules

### Builder API Surface

Key methods on `INetworkApplicationBuilder`:

| Method | Purpose |
| :--- | :--- |
| `Configure<TOptions>(Action<TOptions>)` | Bind config POCO (INI → options) |
| `UseLogger(ILogger)` | Inject logger into the application |
| `UseConnectionHub(IConnectionHub)` | Override default connection hub |
| `UseConnectionGuard(IConnectionGuard)` | Override default connection guard |
| `UseBufferPoolManager(IBufferPoolManager)` | Explicitly register buffer pool manager |
| `UseObjectPoolManager(IObjectPoolManager)` | Explicitly register object pool manager |
| `UseTimeSync()` | Opt-in to time sync packet handlers |
| `UseSystemControl()` | Opt-in to system control packet handlers |
| `UseSecureConnections(certPath?)` | Opt-in to X25519 secure handshake handlers |
| `UseSessions()` | Opt-in to session handlers & registration |
| `UseSessionService / Store / Factory` | Explicitly override session management services |
| `ConfigureDispatchOptions(Action<PacketDispatchOptions<IPacket>>)` | Wire middleware, tune dispatch |
| `MapHandlers<THandler>()` | Register a packet controller |
| `ListenTcp<TProtocol>().Bind()` | Bind a TCP listener |
| `MapDatagram<TProtocol>().Bind()` | Bind a datagram (NDP over UDP) listener; maps the ticket handler |
| `ListenWebSocket<TProtocol>().Bind()` | Bind a WebSocket listener |
| `Build()` | Produce a `NetworkApplication` |

### Startup Sequence (Fixed Order)

1. `Configure<TOptions>()` — config loading/binding
2. `Use*()` calls — service wiring, opting into security, sessions, time sync, system control
3. Handler registration via `MapHandlers<T>()` — opcode-keyed, order does not matter
4. Middleware wiring via `ConfigureDispatchOptions(opts => opts.WithMiddleware(...))` — **execution order = registration order**
5. `Build()` — finalizes the builder
6. `await host.ActivateAsync()` or `await host.RunAsync()`

### Built-in System Handlers (Opt-in)

The system handlers are registered by calling the corresponding builder `Use*` extension method:
- `UseSecureConnections()` registers `HandshakeHandlers` & `ProofOfWorkHandlers`
- `UseSessions()` registers `SessionHandlers`
- `UseSystemControl()` registers `SystemControlHandlers`
- `UseTimeSync()` registers `SystemTimeSyncHandlers`

### Lifecycle

- `ActivateAsync()` starts packet dispatch — does not directly manage socket open/close
- `DeactivateAsync()` stops dispatch — listener lifecycle is separate
- `RunAsync()` = `ActivateAsync()` + await cancellation + `DeactivateAsync()`
- Start/stop is guarded by a `SemaphoreSlim` — not reentrant; calling `ActivateAsync()` twice without `DeactivateAsync()` will deadlock

### Resources

- `Resource.Designer.cs` is auto-generated from `Resource.resx` on every build
- **Never edit `Resource.Designer.cs` directly** — changes are silently overwritten on next build

---

## Checklists

### Bootstrap a new server
```csharp
using var host = NetworkApplication.CreateBuilder()
    .ListenTcp<MyProtocol>().Bind()
    .UseLogger(logger)                               // any Microsoft.Extensions.Logging ILogger
    .Configure<NetworkSocketOptions>(opt => opt.Port = 8080)
    .MapHandlers<MyPacketHandler>()
    .ConfigureDispatchOptions(opts => {
        opts.WithMiddleware(new AuthMiddleware());       // order = execution order
        opts.WithMiddleware(new RateLimitMiddleware());
    })
    .Build();

await host.RunAsync();                      // ActivateAsync → wait → DeactivateAsync
```

### Add a custom protocol
1. Implement `IProtocol` — use `DefaultProtocol` as reference
2. `builder.MapTcp<MyProtocol>().Bind()` or `builder.MapDatagram<MyProtocol>().Bind()`
3. Protocol receives the parsed packet before dispatch — inject pre-processing here if needed

### Add error/status strings
1. Edit `Resource.resx` — add key/value pair
2. Build — `Resource.Designer.cs` regenerates automatically
3. Access via `Resource.MyNewKey`

### Graceful shutdown
1. Call `await host.DeactivateAsync()` — stops new packet processing
2. Allow in-flight handler tasks to complete (await TaskManager workers if needed)
3. Close listeners

---

## Gotchas

- **Middleware registration order is execution order**: Middleware is wired via `ConfigureDispatchOptions(opts => opts.WithMiddleware(...))`. The first call to `WithMiddleware` runs first. If you register business logic before an auth middleware, unauthenticated requests will reach business logic.

- **`DeactivateAsync()` does not flush in-flight packets**: Stopping dispatch immediately stops processing. Packets queued in the channel but not yet handled are dropped. If graceful drain is required, wait for the dispatch channel to empty before calling `DeactivateAsync()`.

- **`SemaphoreSlim` gate is not reentrant**: `ActivateAsync()` acquires the gate; calling it again without `DeactivateAsync()` will block forever. This includes any code path that calls `ActivateAsync()` conditionally.

- **`Resource.Designer.cs` overwrites manual edits silently**: The `ResXFileCodeGenerator` runs on build. Any edits directly to `.Designer.cs` are lost on next `dotnet build`.

- **Handler auto-registration cannot be suppressed**: The four system handlers are always added by `NetworkApplicationBuilder`. You cannot remove or replace them via the builder API — modifying them requires forking the builder.
