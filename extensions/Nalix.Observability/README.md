# Nalix.Observability

Server-side observability for Nalix applications. Pairs with the [n12d dashboard](https://github.com/ppn-systems/n12d).

## Protocol

| Opcode | Packet | Permission | Purpose |
| :--- | :--- | :--- | :--- |
| `0x00FF` | `ObservabilityAccess` | `NONE` | Presents the 32-byte admin key; grants `SUPERVISOR` on match. |
| `0x00FE` | `RuntimeObservation` | `SUPERVISOR` | Samples every subsystem selected by the `RuntimeObservationTargets` mask in one round-trip. |

A `RuntimeObservation` response carries one UTF-8 JSON object whose top-level keys are the
`RuntimeObservationSections` names (`Process`, `Dispatch`, `Tasks`, …). Subsystems not registered on the
server are omitted, and the response's `Targets` lists only the sections actually written. `SampledAt` is the
server time in Unix milliseconds; the `Process` section reports cumulative counters (CPU time, allocations,
GC counts) so clients compute rates from consecutive samples.

Both packets are encrypted; large payloads are compressed by the frame pipeline automatically.

## Usage

```csharp
using Nalix.Hosting;
using Nalix.Observability;

NetworkApplication host = NetworkApplication.CreateBuilder()
    .UseObservability()                       // or .UseObservability("/etc/myapp/observability.private")
    .ListenWebSocket<DefaultProtocol>()
        .OnPort(57207)
        .WithPath("/ws/")
        .Bind()
    .Build();
```

The key file holds one 64-character hex key (`#` comments allowed). When it is missing, a key is generated and
written there with private file permissions. Read the active key in code through `ObservabilityKey.Current`.
