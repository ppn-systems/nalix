# Nalix.Observability

Server-side observability for Nalix applications. Pairs with the [n12d dashboard](https://github.com/ppn-systems/n12d).

## Protocol

| Opcode | Packet | Permission | Purpose |
| :--- | :--- | :--- | :--- |
| `0x00FF` | `ObservabilityAccess` | `NONE` | Presents the 32-byte admin key; grants `SUPERVISOR` on match. |
| `0x00FE` | `RuntimeObservation` | `SUPERVISOR` | Samples every subsystem selected by the `RuntimeObservationTargets` mask in one round-trip, or queries recorded history. |

A `RuntimeObservation` response carries one UTF-8 JSON object whose top-level keys are the
`RuntimeObservationSections` names (`Process`, `Dispatch`, `Tasks`, …). Subsystems not registered on the
server are omitted, and the response's `Targets` lists only the sections actually written. `SampledAt` is the
server time in Unix milliseconds; the `Process` section reports cumulative counters (CPU time, allocations,
GC counts) so clients compute rates from consecutive samples.

Both packets are encrypted; large payloads are compressed by the frame pipeline automatically.

## History

Unless disabled, `UseObservability` also records numeric metrics to disk so a dashboard can browse past days:

- One append-only binary file per UTC day (`<DataDirectory>/observability-history/yyyyMMdd.nxh`). Each sample stores
  only the metrics whose value changed (`u16` id + `f32` value); names are written once per file. A day at the
  default 10 s interval is about 1 MB; files older than the retention window (7 days) are deleted.
- Sampling runs as a non-reentrant `TaskManager` recurring job: one `ObservationWriter` pass into a pooled buffer, one
  forward-only JSON walk (object fields only, per-item arrays skipped) and one file write — about 15 µs of I/O per sample.
- Nothing is kept in memory. A query streams the day files once and folds samples into at most 2 000 buckets
  (average and maximum), so memory is O(metrics × points) whatever the range.
- Derived rates are recorded too: `Process.CpuPercent`, `Process.AllocationBytesPerSecond`, `Process.GcPerSecond`.

Query it with a `RuntimeObservation` at stage `HISTORY_REQUEST` whose `ObservationData` is a UTF-8 JSON query; the
reply is a `HISTORY_RESPONSE`. The JSON shapes are documented on `RuntimeObservationHistory`.

```csharp
builder.UseObservability(options =>
{
    options.HistoryInterval = TimeSpan.FromSeconds(5);
    options.HistoryRetentionDays = 14;
    options.HistoryTargets |= RuntimeObservationTargets.TASKS;
    // options.HistoryEnabled = false;  // live snapshots only
});
```

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
