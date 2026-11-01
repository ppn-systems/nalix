# SDK Audit Review — `docs/audit/sdk.md` (branch `audit/sdk`)

Reviewer pass over the SDK audit. Each finding was re-read against the cited source, and each repro test
was run with `dotnet test tests/Nalix.Tests.sln --configuration Release --filter Category=Audit`.
No production code under `src/` was changed, and no tests were edited.

## Test run

Result: **3 failed, 0 passed** (Total 3), all in `Nalix.SDK.Tests`. Each failure matches the claimed
reason, not a fixture error:

| Test | Observed failure |
| :--- | :--- |
| `ReconnectDisconnectAuditTests.DisconnectAsync_WhenIssuedDuringReconnectBackoff_StopsReconnectLoop` | "DisconnectAsync() was ignored by the auto-reconnect loop: a new connection was opened…" (`ReconnectDisconnectAuditTests.cs:61`) |
| `HandshakePowAuditTests.HandshakeAsync_WhenServerDemandsHighDifficultyProofOfWork_HonorsConnectTimeout` | "HandshakeAsync did not finish within ConnectTimeoutMillis (500 ms)…" (`HandshakePowAuditTests.cs:42`) |
| `ReconnectReadyRaceAuditTests.ReconnectLoop_WhenDropLandsAfterResumeBeforeReadyPublish_DoesNotReportReadyForDeadSession` | "Ready was published for a session that had already dropped…" (`ReconnectReadyRaceAuditTests.cs:50`) |

Environment note: the first attempt failed with exit 127 because the .NET SDK was not installed in the
container. The run above used the .NET 10.0.403 SDK installed to `$HOME/.dotnet`.

## Verdict table — recorded findings

| ID | Title | Audit severity | Verdict | Review severity | Notes |
| :--- | :--- | :--- | :--- | :--- | :--- |
| SDK-01 | `DisconnectAsync()` during backoff does not stop reconnect loop | High | **CONFIRMED** | High | Code matches: flag is consumed only in `OnDisconnected` (`ReconnectSupervisor.cs:62`); `ReconnectLoopAsync` checks only `AutoReconnectEnabled` (`:88`, `:102`). Repro fails for the claimed reason. Impact requires `AutoReconnectEnabled` and a prior unexpected drop. |
| SDK-02 | PoW solver has no bound on difficulty, timeout or cancellation | Medium | **CONFIRMED (with severity correction)** | **Low** | Code matches: `ProofOfWorkChallenge.Validate` (`:82-91`) never bounds `Difficulty`, and `ProofOfWorkSolver.SolveChallenge` (`:41-52`, not `:25-50`) is an unbounded loop with no `ct`. The honest server caps difficulty at 24 (`HandshakeHandlers.cs:72`, `Math.Clamp(…, 0, 24)`), so the 40-difficulty case needs a non-conforming or injected `POW_REQUIRED` answer. Downgraded because the damage is client CPU pinning on an attacker-chosen challenge. The timeout gap still applies to difficulty up to 24 on legitimate traffic, which the report does not mention. |
| SDK-03 | Drop between final check and ready publish is lost | Low | **CONFIRMED** | Low | Window is `ReconnectSupervisor.cs:131` (check) to `:139-140` (publish). `OnDisconnected` sets the flag and returns, and nothing re-reads it. The repro injects the drop at the exact read, so it demonstrates the window rather than measuring real-world frequency. Impact is a misreported ready state, as the report says. |

### Test quality

- **SDK-01 test:** faithful. It uses a raw `TcpListener` and checks `Pending()`, which is direct evidence of a reconnect.
- **SDK-02 test:** valid for the unbounded-solve defect. It has two caveats. It asserts a 5 s total budget against a 500 ms `ConnectTimeoutMillis`, which is stricter than the report's own "Considered" item on total handshake time (see below). It also leaves a thread-pool worker spinning for the rest of the process, since the solver has no cancellation. The spin is inherent to the defect, so it should stay, but the report's suggestion to move it to a separate collection is worth doing.
- **SDK-03 test:** faithful to the code window. The injected drop is in the `IsConnected` read after the flag check, so it lands in the same window the code has.

## Verdict table — items in "Considered and not recorded"

| Item | Verdict | Evidence |
| :--- | :--- | :--- |
| TOFU key fetch is unauthenticated | **FALSE-POSITIVE (by design)** | `TransportOptions.AllowTofu` defaults to `true` and is documented as TOFU discovery (`TransportOptions.cs:97-100`). The proof check still runs against the fetched key. |
| `On<T>` / `OnOnce` / `SubscribeTemp` leaks; `serverHello` not disposed | **NEEDS-MORE-EVIDENCE** | Subscription cleanup is as described. `serverHello` (`HandshakeExtensions.cs:108`, `:147`) is never disposed. Whether it holds pooled memory was not checked, so the report's decision to drop it is reasonable but unproven either way. |
| Unsolicited `SessionChallenge` injection | **FALSE-POSITIVE (impact)** | The client fails closed on the proof check (`:210`). Agree with the report that this is availability only. |
| Non-constant-time proof comparison | **FALSE-POSITIVE** | `Bytes32.Equals` (`src/Nalix.Abstractions/Primitives/Bytes32.cs:228-262`) XOR-accumulates over all 32 bytes on the AVX2, SSE4.1 and scalar paths, with no early exit. The `!=` at `HandshakeExtensions.cs:210` and `:244` is therefore constant-time. |
| Stale-frame delivery during pool re-rent | **NEEDS-MORE-EVIDENCE** | Not reproduced. The report's own mitigation (per-request SequenceId correlation) is a reason to deprioritize, not to reject. |
| Handshake timeout covers only the first wait | **NEEDS-MORE-EVIDENCE (doc ambiguity)** | `ConnectTimeoutMillis` is documented as "Connect attempt timeout" (`TransportOptions.cs:38`), which does not say whether it bounds the whole handshake. The report calls this by design; SDK-02's test assumes the opposite. Resolve the contract first. SDK-02 itself stands on the missing deadline inside the solve, regardless of this question. |

## Performance and allocation checks

These were checked because the report contains no performance findings. Nothing new is recorded.

| Candidate | Verdict | Evidence |
| :--- | :--- | :--- |
| WebSocket message `ms.ToArray()` copy per frame (`WsFrameReader.cs:160`) | **FALSE-POSITIVE** | The stream is created with `new MemoryStream()` (`WsFrameReader.cs:123`). A probe on net10.0 showed `TryGetBuffer` returns `true` for that constructor, so the `ToArray()` fallback is unreachable in practice. |
| LINQ on SDK hot paths | **FALSE-POSITIVE** | No `using System.Linq` in `src/Nalix.SDK` (grep). |
| Per-message locks in `TransportSession` (`_pendingRequestsLock`, `_keepAliveLock`) | **NEEDS-MORE-EVIDENCE** | Locks exist (`TransportSession.cs:90`, `:119`, `:150`, `:241`, `:393`, `:422`). Contention was not measured, so no finding is recorded. |

## Missed in-scope issues

No new defect with a verified repro was found in this pass. I did not add tests, and I did not change any
test on `audit/sdk`. Items that look worth a dedicated look, without a verdict:

- The ready-publish window in SDK-03 also involves `_readyTcs = null` (`ReconnectSupervisor.cs:139`) being a non-atomic write next to `OnDisconnected`'s `CompareExchange` (`:69`). The window is narrow.
- The exhausted-attempts path (`:158-161`) clears `_readyTcs` after the loop. A concurrent drop that installs a new `tcs` in between could be cleared. Not reproduced.

## Summary of corrections to the audit report

1. SDK-02 severity: Medium → **Low**. The honest server caps difficulty at 24 (`HandshakeHandlers.cs:72`).
2. SDK-02 line range: `ProofOfWorkSolver.cs:25-50` → **`:41-52`** for the unbounded loop.
3. Add to the report's "Considered" list: the constant-time `Bytes32.Equals` evidence (`Bytes32.cs:228-262`), which closes the proof-comparison item as a false positive.
4. Resolve the `ConnectTimeoutMillis` contract before treating "handshake timeout covers only the first wait" as by design. The SDK-02 test currently assumes a total budget.
