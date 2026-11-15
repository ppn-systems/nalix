# Nalix.Runtime

Packet dispatch, middleware, handlers, throttling, session persistence, and runtime services for
Nalix applications.

Nalix.Runtime sits above transport and codec packages. It routes decoded packets to handlers,
executes middleware, applies rate policies, handles handshake/session flows, and coordinates
dispatch work away from socket threads.

## Install

```bash
dotnet add package Nalix.Runtime
```

## What It Provides

| Area | Purpose | Main types |
| :--- | :--- | :--- |
| Dispatching | Shard-aware packet dispatch channels and contexts | `PacketDispatchChannel`, `PacketContext`, `PacketDispatcherBase` |
| Middleware | Inbound and outbound packet pipeline | `MiddlewarePipeline`, `IPacketMiddleware`, `RateLimitMiddleware`, `TimeoutMiddleware` |
| Handlers | Built-in handshake, proof-of-work, and session handlers | `HandshakeHandlers`, `ProofOfWorkHandlers`, `SessionHandlers` |
| Throttling | Token-bucket rate limiting | `TokenBucketLimiter`, `PolicyRateLimiter` |
| Sessions | Session services, persistence observers, and in-memory store | `SessionService`, `InMemorySessionStore`, `SessionPersistenceObserver` |
| Timekeeping | Clock skew and synchronization helpers | `TimeSynchronizer` |
| Options | Runtime dispatch and throttling configuration | `DispatchOptions`, `TokenBucketOptions`, `SessionStoreOptions` |

## Middleware

```csharp
using Nalix.Abstractions.Middleware;
using Nalix.Abstractions.Networking.Packets;

public sealed class AuditMiddleware<TPacket> : IPacketMiddleware<TPacket>
    where TPacket : IPacket
{
    public async ValueTask InvokeAsync(
        IPacketContext<TPacket> context,
        Func<CancellationToken, ValueTask> next)
    {
        await next(context.CancellationToken);
    }
}
```

## Packet Handler

```csharp
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Security;

[PacketHandler("Chat")]
public static class ChatHandlers
{
    [PacketOpcode(201)]
    [PacketPermission(PermissionLevel.USER)]
    public static ValueTask HandleMessageAsync(IPacketContext<ChatMessage> context)
    {
        ChatMessage message = context.Packet;
        return ValueTask.CompletedTask;
    }
}
```

## Design Notes

- Handlers are registered explicitly for Native AOT compatibility.
- Dispatch channels keep application handlers off socket receive loops.
- Session resume is single-use and proof-verified before token consumption.

## Documentation

- Package guide: https://ppn.io.vn/packages/nalix-runtime/
- API reference: https://ppn.io.vn/api/runtime/
- Middleware: https://ppn.io.vn/api/runtime/middleware/
- Session resume: https://ppn.io.vn/api/security/session-resume/
