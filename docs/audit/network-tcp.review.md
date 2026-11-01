# Review: Nalix.Network — TCP side audit

Reviews `docs/audit/network-tcp.md` (audit commit `a7843f1` on `audit/network-tcp`). Method: re-read every
cited code path in `src/Nalix.Network/RateLimiting/`, check the design docs (`.claude/skills/nalix-network.md`,
XML docs on `ConnectionBanStoreOptions` and `ConnectionLimitEntry`) for intent, then run
`dotnet test tests/Nalix.Tests.sln --configuration Release --filter Category=Audit`. No production code under
`src/` was changed. No tests were changed.

## Test run

`Category=Audit`: **2 run, 2 failed, 0 passed**. Each failure is the intended reproduction and fails for the
claimed reason:

| Test | Observed failure | Matches claim? |
| :--- | :--- | :--- |
| `NETTCP01_BanCountDecay_IsAppliedOncePerElapsedWindow_NotOnEveryAttempt` | First attempt 10 → 7 (passes). Second attempt 7 → 4, expected 7. | Yes |
| `NETTCP02_Cleanup_DoesNotEvictIdleEntry_ThatCarriesEscalatedBanCount` | `RUN_CLEANUP_ONCE` removes the entry; test expected it to survive. | Yes |

## Verdict table

| ID | Audit severity | Verdict | Reviewed severity | Repro fails for claimed reason | Notes |
| :--- | :--- | :--- | :--- | :--- | :--- |
| NETTCP-01 | Medium | **CONFIRMED** | Medium | Yes | Code at `Connection.Guard.Slot.cs:156-163` and `:522-529` matches. `LastBanTimeTicks` is only reset when `BanCount` reaches 0, so the same tiers are subtracted on every attempt. No design doc describes per-attempt decay; the skill doc describes decay as a one-time, idle-period effect, so this is a defect and not intent. |
| NETTCP-02 | Medium | **CONFIRMED** | Medium | Yes | `SHOULD_REMOVE_ENTRY` (`Connection.Guard.Cleanup.cs:306-316`) never reads `BanCount`. The sampler (`Connection.Guard.Slot.cs:386`) and the slot-victim check (`:437`) both protect `BanCount > 0`, so the cleanup path is inconsistent with them, not an intentional exception. |
| PROXY sweep race (investigated, not reported) | — | **NEEDS-MORE-EVIDENCE** | — | n/a | Agree it is not reported. Needs a fault-injection seam before it can be reproduced. |
| Accept/close accounting parity (investigated, not reported) | — | Agree, no finding | — | n/a | Not contradicted by the code read in this review. |
| Frame length validation, UInt16 and VarInt (investigated, not reported) | — | Agree, no finding | — | n/a | Not contradicted by the code read in this review. |
| Global counter drift correction (investigated, not reported) | — | Agree, no finding | — | n/a | Not contradicted by the code read in this review. |

**By-design findings:** none. Neither finding is an intentional design per the docs, XML comments, or skills.

**Performance / optimization findings:** the audit reports none, so there was nothing to verify. Not added.

## Notes on the audit's suggested fixes

- **NETTCP-01:** The suggested fix `LastBanTimeTicks += decayTiers * decayWindowTicks` is sound and keeps the
  remainder. It must be applied to both overloads, as the report says. The current repro drives only the
  `(SocketEndpoint, DateTime, IPAddress)` overload via `TryAccept(IPEndPoint)`, which is the TCP accept path. The
  `(SocketEndpoint, DateTime)` overload at `Slot.cs:156` (used by `TryAccept(SocketEndpoint)`) has no test, so add one
  before fixing it.
- **NETTCP-02:** Keeping every `BanCount > 0` entry has an unbounded-growth risk unless retention is capped. The
  report mentions a bounded retention option; make that part of the fix, not an optional extra. The fix needs a
  test that confirms a `BanCount > 0` entry is eventually removed after `BanCountDecayWindow`, so that memory stays
  bounded.

## Unverified observation (not a finding)

`Connection.Guard.Slot.cs:798` releases with `Math.Max(0, CurrentConnections - 1)`. A double `Release` for one
connection would silently clamp and under-count a different live connection, but no repro was attempted in this
review, so it is not reported. Worth a targeted test if release ordering is audited later.

## Result

- Both findings: **CONFIRMED**, severity unchanged (Medium).
- No false positives. No tests added, removed, or changed.
- Open follow-ups: PROXY sweep race (needs a fault-injection seam); the `Release` clamp observation above.
