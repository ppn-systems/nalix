# Nalix.Observability

Server-side observability for Nalix applications. Pairs with the [n12d dashboard](https://github.com/ppn-systems/n12d).

## Protocol

| Opcode | Packet | Permission | Purpose |
| :--- | :--- | :--- | :--- |
| `0x00FF` | `ObservabilityAccess` | `NONE` | Presents the 32-byte admin key; grants `OBSERVER` on match. `OBSERVER` grants nothing else. |
| `0x00FE` | `RuntimeObservation` | `OBSERVER` (exact match) | Samples every subsystem selected by the `RuntimeObservationTargets` mask in one round-trip, or queries recorded history. |

Each `RuntimeObservation` response chunk carries one UTF-8 JSON object whose top-level keys are the
`RuntimeObservationSections` names (`Process`, `Dispatch`, `Tasks`, …). Subsystems not registered on the
server are omitted, and each chunk's `Targets` lists only the sections it actually contains. `SampledAt` is the
server time in Unix milliseconds; the `Process` section reports cumulative counters (CPU time, allocations,
GC counts) so clients compute rates from consecutive samples.

`RuntimeObservation` requires exactly `OBSERVER` (`StrictMatch`), so a `USER`, `SUPERVISOR` or `OWNER` connection
must present the key to read observations; the key itself does not raise the connection above `OBSERVER`.

Both packets are encrypted; payloads above the compression threshold (`CompressionOptions.MinSizeToCompress`, 512 B by default) are compressed automatically.

### Payload budget

Every chunk stays under `ObservabilityOptions.MaxPayloadBytes` (96 KiB by default), below the 128 KiB transport
frame limit (`VarIntFramingOptions.MaxPayloadSize`). Sections are packed into as many chunks as needed, streamed
back in order, each listing its sections in `Targets`; no follow-up request is required. A single section larger
than the budget (long worker or instance lists) has its arrays capped and is marked `"_truncated": true`. History series lower their bucket count to fit, and a catalog cuts
its metric list (`"truncated": true`).

## History

Unless disabled, `UseObservability` also records numeric metrics to disk so a dashboard can browse past days:

- One append-only binary file per UTC day, `yyyyMMdd.nxh`, in `ObservabilityOptions.HistoryDirectory` (default
  `observability-history` under the Nalix data directory). Each sample stores only the metrics whose value changed
  since the previous sample in that file (`u16` id + `f32` value); metric names are written once per file. File size
  therefore grows with the number of metrics that change between samples.
- The file keeps the last `HistoryRetentionDays` UTC days (today included, 7 by default); older files are deleted.
- Sampling runs as a non-reentrant `TaskManager` recurring job (`HistoryInterval`, 10 s by default, never below 1 s):
  one `ObservationWriter` pass into a pooled buffer, one forward-only JSON walk (object fields only, per-item arrays
  skipped) and one file append.
- Samples are not kept in memory. A query streams the day files once and folds samples into at most 2 000 buckets
  (average and maximum), so the query's memory use is bounded by the number of metrics times the bucket count.
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
    .MapWebSocket<DefaultProtocol>()
        .OnPort(57207)
        .WithPath("/ws/")
        .Bind()
    .Build();
```

The key file holds one 64-character hex key (`#` comments allowed). When it is missing, a key is generated and
written there with private file permissions. Read the active key in code through `ObservabilityKey.Current`.
