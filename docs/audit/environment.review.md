# Review: `docs/audit/environment.md` (`src/Nalix.Environment`, `src/Nalix.Abstractions`)

Reviewed branch: `audit/environment` (commit `a8856f5`), read in a detached worktree.
Run: `dotnet test tests/Nalix.Tests.sln --configuration Release --filter Category=Audit`
Result: **2 failed, 0 passed** (`Nalix.Environment.Tests`). Both repros fail for the claimed reason.

No production code was changed. No tests were changed (both repros are valid as written).

## Verdict table

| ID | Claimed severity | Verdict | Corrected severity | Repro fails for claimed reason? | Evidence |
| :--- | :--- | :--- | :--- | :--- | :--- |
| ENV-01 | High | **CONFIRMED** (mechanism); reachability NEEDS-MORE-EVIDENCE | **Medium** (raise to High if a production call site disposes twice) | Yes: `A stale second Dispose released the buffer owned by the new lease.` | `BufferLease.Initialize` sets `_refCount = 1` (`BufferLease.cs:261`) on a shell that `Dispose` has pushed back to the thread-local cache (`:555-563`). A stale `Dispose` then decrements the new owner's count to 0 and returns `_buffer` to `ByteArrayPool` (`:477-537`). The `Debug.Fail` guard only fires at `<= 0`, so it stays silent. |
| ENV-02 | Medium | **CONFIRMED** | **Low** | Yes: `No exception was thrown` | `Leb128.TryRead` fast path (`Leb128.cs:140-146`) shifts `(b4 & 0x7F) << 28` and checks only the continuation bit. Bits 4-6 of the 5th byte are lost, so `80 80 80 80 10` decodes to `0`. The slow path (`:170-190`) has the same gap. Impact is narrower than stated: the framer still rejects negative and over-max payload lengths (`SocketConnection.Receive.VarInt.cs:41-50`), so the effect is that a non-canonical prefix is accepted as a small valid length. No memory-safety or amplification impact. |

## Finding notes

### ENV-01 (BufferLease stale double dispose)

- **By design?** No. `Dispose` documents "Double-dispose is tolerated" (`BufferLease.cs:472`). That is an explicit contract, and the bug is that the tolerance only holds until the shell is reused. Not a false positive.
- **Severity.** The report's High assumes a caller double-disposes. This review did not find a production call site that does so, and did not audit every call site, so reachability is open. Medium until a real path is shown. The report's "Suggested fix" options are reasonable; option (a) (generation token) is the smallest change that keeps the API shape.
- **Repro quality.** The test is sound. It uses `Assert.Same` to confirm shell reuse before the stale dispose, so it cannot pass vacuously.

### ENV-02 (LEB128 5th byte)

- **By design?** No. The existing overlong check and its `Throw.OverlongLeb128` ("corrupt") message show the intent is to reject invalid sequences. No existing vector (`Leb128Tests.cs:65-69` uses a 6-byte input) depends on the lenient 5th byte.
- **Severity.** Lowered to Low. The payload-length cap bounds the effect. The issue is wire-level ambiguity, not a resource or safety bug.
- **Suggested fix.** Agree with the report: on the 5th byte, reject `(b & 0x70) != 0`. Apply the same check in `TryReadSlow` when `shift == 28`.

## Performance and allocation pass (requested update)

The report contains no performance findings. In-scope source (`src/Nalix.Environment`, `src/Nalix.Abstractions`) was scanned for LINQ, `lock`, `ToArray`/`ToList`, and `new List<`:

- `Nalix.Abstractions/Networking/Packets/MemoryPacket.cs:34` `Serialize() => this.Memory.ToArray()`: **FALSE-POSITIVE (by design)** if raised as a perf finding. The public API returns `byte[]`, so one copy is the contract. Changing it is an API change.
- `Nalix.Environment/Random/OsCsprng.cs:122,304` (`lock (s_devUrandomLock)`): **FALSE-POSITIVE**. The lock is inside a double-checked pattern (`GET_DEV_URANDOM_HANDLE`, `:296-320`). The fast path is a `Volatile.Read` with no lock, and the lock is taken only on first open and on `Shutdown`.
- `Nalix.Environment/Configuration/ConfigurationManager.cs:286,725,799` and `IniConfig.cs:1258`: configuration state, not on the packet path. **Not a finding.**
- No LINQ in the reviewed hot paths (`BufferLease`, `Leb128`, `DataReader`, `FragmentAssembler`).

No new performance finding was confirmed with a repro, so no test was added for this pass.

## Missed issues

No additional in-scope issue was confirmed with a repro test in this pass. The report's own "Not reviewed" list stays open and is the main gap:

- `Configuration/Binding/IniConfig.cs` (1562 lines)
- `Configuration/ConfigurationManager.cs`
- `IO/Directories.*` (permission handling)
- `Random/Csprng.cs`, `OsCsprng.cs` (only the lock path was checked above)
- `Hashing/Sha1.cs`, `Hashing/XxHash32.cs`
- `Memory/DataWriter.cs`
- `Sequencing/SequenceCounter.cs`

These are the next audit targets.

## Summary

- 2 of 2 findings reproduced and confirmed. Neither is a false positive.
- Severity corrections: ENV-01 High → Medium (pending reachability); ENV-02 Medium → Low.
- No by-design false positives in the report. The one perf-style copy observed is by API contract.
- Coverage gap: 7 files listed as "not reviewed" by the audit remain unreviewed.
