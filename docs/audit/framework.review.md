# Nalix.Framework Audit — Review

Reviewed: `docs/audit/framework.md` and `tests/Nalix.Framework.Tests/Audit/FrameworkAuditTests.cs` from `origin/audit/framework` (commit `81980e4`).

Method: re-read every cited location in `src/Nalix.Framework`, checked the design intent in XML docs (`src/Nalix.Abstractions/IPoolable.cs`) and `.claude/skills/nalix-framework.md`, then ran `dotnet test tests/Nalix.Tests.sln --configuration Release --filter Category=Audit` against the audit branch. No production code was changed.

## Verdict table

| ID | Claimed severity | Verdict | Review severity | Repro result | Notes |
| :--- | :--- | :--- | :--- | :--- | :--- |
| FW-01 | Medium | **FALSE-POSITIVE (by design)** | — | Test removed | `IPoolStateTracked` is documented as an *optional* ownership marker "used to reject duplicate returns". Untracked types skipping the check is the documented opt-in, not a defect. The "cross-user leak" also needs caller misuse. |
| FW-02 | Medium | **CONFIRMED** | **Low** | Fails: expected 16 distinct, got 15 | `BufferPoolManager.Return` forwards to `ArrayPool.Shared` without a guard when leak detection is off. Reachable only by a caller double-return, which `ArrayPool` itself does not guard against. Hardening, not a reachable defect in correct code. |
| FW-03 | Low | **CONFIRMED** | Low | Fails: expected 0 dropped, got 6 | `TypePool._slots` is sized once in the constructor and `TryPush` loops only over `_slots.Length`, so `SetMaxCapacity` increases beyond the original size are silently capped. |
| FW-04 | Medium | **CONFIRMED** | Medium | Fails: no `ArgumentOutOfRangeException` for 1024 | `Snowflake` masks `machineId & 0x3FF` silently; `WorkerOptions.MachineId` has no `[ValueRange]` or `Validate()`, unlike `SnowflakeOptions` (`[ValueRange(1, 1023)]`). A worker configured with 1024 gets machine 0 and collides with it across nodes. |
| FW-05 | Low | **CONFIRMED** | Low | Fails: tracker held 5000 entries for 5000 returned buffers | `Rent` appends a `WeakReference` on every call, `Return` never removes it, and pruning happens only in `Report()` above 10,000 entries. Diagnostic mode only (`EnableBufferLeakDetection`, default off). |

Summary: 4 confirmed (FW-04 Medium; FW-02, FW-03, FW-05 Low, with FW-02 downgraded from Medium), 1 false positive removed (FW-01).

## Changes made in this review

- **FW-01 test removed** from `FrameworkAuditTests.cs`. The `PlainPoolable` helper stays because FW-03 uses it.
- **FW-04 test comment corrected.** The original said 1024 "collides with the 1023/1024 pair". The masking maps 1024→0 and 1025→1, so 1024 collides with machine 0 only.
- Test file copied to `review/framework` so the review branch holds the repros it verifies.

## Test run (after changes)

Command: `dotnet test tests/Nalix.Tests.sln --configuration Release --filter Category=Audit`

Result: **Failed: 4, Passed: 0, Total: 4**. Every remaining repro fails for the reason described in the table above. Each one still shows the defect, so none is a stale or passing test.

## Missed issues

No new confirmed defects were found and added. The review pass covered the five findings thoroughly but did not reach new areas, so this is not a clean bill for the module. Still uncovered, as the original report also states:

- `TaskManager` worker and recurring lifecycle, cancellation, and group concurrency.
- `ObjectPoolManager` cleanup, trim, and report paths.
- `InstanceManager` activator caching and the signature-instance cache.
- `Options/` loader behavior beyond the validation attributes.

Minor documentation inconsistency, not a finding: `Snowflake.Serialization.cs` describes `MachineId` as 16 bits, while the code uses 10 bits (`& 0x3FF`).

## Performance and allocation notes

Not a full pass. Two observations from reading the code during this review:

- `BufferPoolManager.Rent` with leak detection on allocates a `BufferSentinel`, a `WeakReference`, and a `ConditionalWeakTable` entry per rent. This is the cost of diagnostic mode, and is covered by FW-05.
- `Snowflake.NewId` takes a `lock` on every call. This is a contention point under heavy ID generation, which the original report notes as the 16,384 IDs/s ceiling.
