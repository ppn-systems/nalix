# Dispatch Channel and Router

This page documents the lower-level dispatch queue implementation used by runtime dispatchers.

## Source Mapping

- `src/Nalix.Runtime/Internal/Routing/DispatchChannel.cs`
- `src/Nalix.Runtime/Dispatching/PacketDispatchChannel.cs`

## `DispatchChannel<TPacket>`

`DispatchChannel<TPacket>` is a priority-aware, connection-associated queue implementation used under `PacketDispatchChannel`.

### Why it exists

Dispatch runtime needs efficient enqueue/dequeue behavior with per-connection isolation and priority selection while remaining compatible with `IDispatchChannel<TPacket>`.

### Publicly visible members

- `TotalPackets`
- `HasPacket`
- `Push(IConnection connection, IBufferLease raw)`
- `TryClaim(out IDispatchSession session)` — claims exclusive processing rights over a connection's mailbox

### Internal diagnostics members (not part of `IDispatchChannel<TPacket>`)

- `TotalConnections`
- `ReadyConnections`
- `PendingPerPriority`
- `PendingPerConnection`
- `PushCore(IConnection connection, IBufferLease raw, bool noBlock = false)`

## Architecture Notes

- Maintains per-connection state with per-priority queues.
- Claim/dequeue path prefers higher priority first.
- Enqueue path uses `DispatchOptions` and drop policy behavior.
- Integrates with `IConnectionHub.ConnectionUnregistered` for state cleanup.

## `PacketDispatchChannel`

`PacketDispatchChannel` is the public, high-performance dispatch channel that wires `DispatchChannel<IPacket>` to background worker loops. It implements `IPacketDispatch`, `IActivatable`, and `IDisposable`.

### Key members

- `Activate(CancellationToken)` — starts background dispatch workers.
- `Deactivate(CancellationToken)` — stops dispatch workers.
- `HandlePacket(IBufferLease packet, IConnection connection)` — enqueues a raw packet for async dispatch.
- `GenerateReport()` — returns a human-readable diagnostic report.
- `WriteReportData(Utf8JsonWriter)` — zero-allocation JSON diagnostic snapshot.

### Diagnostics

`PacketDispatchChannel` exposes full diagnostics through `IReportable`:

- `TotalPackets`, `TotalConnections`, `ReadyConnections`
- `PendingPerPriority`, `PendingPerConnection`
- `WakeSignals`, `WakeReads`, `IdleWorkers`
- The worker wake path uses one allocation-free `WorkerWakeSignal` per worker (`src/Nalix.Runtime/Internal/Routing/WorkerWakeSignal.cs`) plus per-worker parked flags in `src/Nalix.Runtime/Dispatching/PacketDispatchChannel.cs`. No timer polling.

## Related APIs

- [Dispatch Contracts](./dispatch-contracts.md)
- [Packet Dispatch](./packet-dispatch.md)
- [Dispatch Options](../../options/runtime/dispatch-options.md)
