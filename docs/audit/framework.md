# Nalix.Framework Audit

Scope: `src/Nalix.Framework` — object/buffer pools and `Reset()` completeness, `InstanceManager` thread-safety, Snowflake ID generation, task/timer scheduling, and configuration options.

Method: code reading of each named area, then an xUnit repro per finding under `tests/Nalix.Framework.Tests/Audit/FrameworkAuditTests.cs` (`[Trait("Category","Audit")]`). Every test below fails on the current code. No production code was changed.

## Summary

| ID | Title | Severity | Location | Repro test |
| :--- | :--- | :--- | :--- | :--- |
| FW-01 | `ObjectPool` accepts duplicate returns for `IPoolable` types that do not implement `IPoolStateTracked` | Medium | `src/Nalix.Framework/Memory/Pools/ObjectPool.cs:193-199`, `:626-634` | `FW01_ObjectPool_DoubleReturn_PlainPoolable_ShouldNotHandOutSameInstanceTwice` |
| FW-02 | `BufferPoolManager.Return` has no double-return guard when leak detection is off (default) | Medium | `src/Nalix.Framework/Memory/Buffers/BufferPoolManager.cs:91-113` | `FW02_BufferPoolManager_DoubleReturn_ShouldNotHandOutSameBufferTwice` |
| FW-03 | `ObjectPool.SetMaxCapacity` increases are ignored beyond the original slot count | Low | `src/Nalix.Framework/Memory/Internal/PoolTypes/TypePool.cs:38`, `:76-89`; `src/Nalix.Framework/Memory/Pools/ObjectPool.cs:352-363` | `FW03_ObjectPool_SetMaxCapacity_Increase_ShouldRetainReturnedObjects` |
| FW-04 | Out-of-range Snowflake machine IDs are silently truncated, so they collide | Medium | `src/Nalix.Framework/Identifiers/Snowflake.cs:122`, `:133`, `:228-231`; `src/Nalix.Framework/Options/WorkerOptions.cs:24`; `src/Nalix.Framework/Tasks/TaskManager.cs:268` | `FW04_Snowflake_NewId_MachineIdOutOfRange_ShouldBeRejected` |
| FW-05 | Leak-detection sentinel tracker grows without bound between diagnostic reports | Low | `src/Nalix.Framework/Memory/Buffers/BufferPoolManager.cs:36`, `:79-80`; `BufferPoolManager.Report.cs:110-118` | `FW05_BufferPoolManager_LeakDetection_ReturnedBuffers_ShouldNotAccumulateTrackerEntries` |

## Findings

### FW-01 — Duplicate returns accepted for non-tracked poolables
- **Severity:** Medium
- **Location:** `src/Nalix.Framework/Memory/Pools/ObjectPool.cs:193-199` (`ReturnFast`), `:626-634` (`TryMarkReturned`)
- **Description:** Duplicate-return detection exists only for objects implementing `IPoolStateTracked`. `TryMarkReturned` returns `true` unconditionally for any other `IPoolable`, so a second `Return` of the same instance pushes it into the pool again.
- **Impact:** Two independent renters receive the same live object. Per-user state written by one renter is visible to the other. This is the cross-user state leak the pool is meant to prevent. `TotalRejectedReturnCount` also reports zero for these types, which hides the misuse.
- **Repro:** `FW01_ObjectPool_DoubleReturn_PlainPoolable_ShouldNotHandOutSameInstanceTwice`
- **Suggested fix (text only):** Track ownership for every poolable, not only `IPoolStateTracked`. Options: a `ConditionalWeakTable<object, StrongBox<int>>` flag, or a debug-only rented-set check. At minimum, document that `Return` for untracked types is unchecked, and surface a diagnostic counter.

### FW-02 — No double-return guard for pooled byte buffers
- **Severity:** Medium
- **Location:** `src/Nalix.Framework/Memory/Buffers/BufferPoolManager.cs:91-113`
- **Description:** `Return` forwards the array to `ArrayPool<byte>.Shared.Return` unconditionally. The sentinel lookup that could detect a second return runs only when `EnableBufferLeakDetection` is true, which is off by default. `ArrayPool` does not detect duplicates in release builds.
- **Impact:** The same `byte[]` is stored in the shared pool twice and handed to two concurrent renters. One renter's payload, including any secrets or packet bytes, is readable and writable by the other.
- **Repro:** `FW02_BufferPoolManager_DoubleReturn_ShouldNotHandOutSameBufferTwice`
- **Suggested fix (text only):** Keep a cheap always-on ownership check (for example a `ConditionalWeakTable<byte[], object>` of rented arrays, or a per-array flag tied to rent/return) independent of the leak-detection option. On a rejected duplicate, skip the pool return and increment a counter.

### FW-03 — `SetMaxCapacity` cannot grow a per-type pool
- **Severity:** Low
- **Location:** `src/Nalix.Framework/Memory/Internal/PoolTypes/TypePool.cs:38` (`_slots` sized once in the constructor), `:76-89` (`SetMaxCapacity`); `src/Nalix.Framework/Memory/Pools/ObjectPool.cs:352-363`
- **Description:** `TypePool` allocates its slot array once, with the size passed at construction. `SetMaxCapacity` updates `_maxCapacity`, but `TryPush` can never place more objects than `_slots.Length`. Raising the limit later is silently capped at the original value. The per-type pool is usually created on first `Get`, with the default size.
- **Impact:** Configured capacity is not honored. Returned objects are dropped (`TotalDroppedCount` rises) and allocation pressure stays higher than configured.
- **Repro:** `FW03_ObjectPool_SetMaxCapacity_Increase_ShouldRetainReturnedObjects`
- **Suggested fix (text only):** Allocate slots for the largest capacity that can be set, or grow the slot array under a lock in `SetMaxCapacity` and publish the new array atomically. Alternatively reject increases above the allocated size and return `false`.

### FW-04 — Machine ID truncation and missing validation
- **Severity:** Medium
- **Location:** `src/Nalix.Framework/Identifiers/Snowflake.cs:122`, `:133` (`machineId & 0x3FFu`), `:228-231` (`NewId(SnowflakeType, ushort)`); `src/Nalix.Framework/Options/WorkerOptions.cs:24` (no `[ValueRange]`, no `Validate`); `src/Nalix.Framework/Tasks/TaskManager.cs:268`
- **Description:** The 10-bit machine field is produced by masking with `0x3FF`, so out-of-range values are silently wrapped. `WorkerOptions.MachineId` is unvalidated and flows directly into `Snowflake.NewId(options.IdType, options.MachineId)`. `SnowflakeOptions` enforces `1..1023`, but this path does not.
- **Impact:** A worker configured with `1024` gets machine ID `0`, and `1025` gets `1`. IDs from nodes that were meant to be distinct can therefore share a machine ID and collide across processes. Cross-process uniqueness is the purpose of the machine field.
- **Repro:** `FW04_Snowflake_NewId_MachineIdOutOfRange_ShouldBeRejected`
- **Suggested fix (text only):** Reject machine IDs above 1023 in `NewId` with `ArgumentOutOfRangeException`, and add `[ValueRange(1, 1023)]` plus a `Validate()` call to `WorkerOptions`, matching `SnowflakeOptions`.

### FW-05 — Sentinel tracker grows per rent with leak detection on
- **Severity:** Low
- **Location:** `src/Nalix.Framework/Memory/Buffers/BufferPoolManager.cs:36`, `:79-80` (`_sentinelTracker.Add` on every `Rent`); `src/Nalix.Framework/Memory/Buffers/BufferPoolManager.Report.cs:110-118` (pruning only inside `Report`)
- **Description:** When `EnableBufferLeakDetection` is true, every `Rent` appends a `WeakReference` to an unbounded `ConcurrentBag`. Entries are removed only during a diagnostic report, and only when the count exceeds 10,000. `Return` does not remove its entry.
- **Impact:** Memory grows linearly with total rents between reports, even when every buffer is returned. This is a long-running-server leak in the diagnostic mode.
- **Repro:** `FW05_BufferPoolManager_LeakDetection_ReturnedBuffers_ShouldNotAccumulateTrackerEntries`
- **Suggested fix (text only):** Prune on the hot path, for example every N rents or when the bag exceeds a bound. Alternatively, drop the tracker entry on `Return` by keeping the `WeakReference` on the sentinel and removing it lazily.

## Considered and not recorded

These were reviewed and are not findings, either because no failing case could be shown or because they are design notes.

- **Snowflake predictability.** `Snowflake` is a timestamp, sequence, and fixed-machine construction, not a CSPRNG. `Connection`, `WebSocketConnection`, and `PassthroughConnection` assign `ConnectionId = Snowflake.NewId(SnowflakeType.Session)`. No credential or resume-token use of `ConnectionId` was found in `Nalix.Network`, so this is not recorded. If any path uses it as a bearer token, it should be re-rated High.
- **Snowflake clock rollback and sequence overflow.** Rollback is clamped to the last timestamp, and overflow borrows the next second under `s_genLock`. Both paths keep IDs unique within a process. Under sustained load above 16,384 IDs/s, timestamps run ahead of wall-clock time. This was not reproduced deterministically because it depends on timing.
- **`InstanceManager` L1 thread-static cache.** A reader can publish a value read just before a `Register` or `RemoveInstance`. The next read compares the global version and clears the slot, so staleness is bounded to one call. No failing interleaving was constructed.
- **`TaskManager` backoff shift** (`TaskManager.PrivateMethods.cs:852-855`). `(long)ms << pow` can overflow, but `BackoffMaxPower` is capped at 16 by `[ValueRange(0, 16)]`. Overflow requires a base interval of years. Not reachable under valid configuration.
- **`TypePool` count bookkeeping.** `_count` is maintained with CAS and clamped. No interleaving was found that leaks or duplicates a slot.
- **`ThreadLocalCache` single slot.** A stale entry can remain in a thread's slot until the owner pops it. This retains one object per thread and does not cross users.

## Not fully covered

Time did not allow full review of these areas, so they should not be treated as clean:

- `TaskManager` worker and recurring lifecycle, cancellation, and group concurrency (beyond the timer and backoff math).
- `ObjectPoolManager` cleanup, trim, and report paths (`ObjectPoolManager.cs`, `ObjectPoolManager.Cleanup.cs`).
- `InstanceManager` activator caching and the signature-instance cache (`InstanceManager.Activators.cs`).
- Configuration loading in `Options/` beyond the validation attributes checked for FW-04. Loader internals live in `Nalix.Environment`, which is outside this scope.

## Test run

Command: `dotnet test tests/Nalix.Tests.sln --configuration Release --filter Category=Audit`

Result: **Failed: 5, Passed: 0, Total: 5** (`Nalix.Framework.Tests.dll`). Each repro fails for the reason documented above:

| Test | Observed |
| :--- | :--- |
| FW01 | Same instance handed to two independent renters |
| FW02 | 15 distinct buffers out of 16 rents (one buffer handed out twice) |
| FW03 | 6 returned objects dropped after `SetMaxCapacity` increase (expected 0) |
| FW04 | No `ArgumentOutOfRangeException` for machine ID 1024 |
| FW05 | Tracker held 5000 entries for 5000 returned buffers (limit 1000) |

Note: `dotnet` is installed at `/root/.dotnet/dotnet` and must be on `PATH` for the command above.
