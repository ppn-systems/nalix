# SDK Audit — `src/Nalix.SDK`

Scope: client connect and handshake flow, server identity verification, `RequestAsync` correlation,
`On<T>()` / subscription handler lifetime, reconnect races, and cancellation/timeout behavior.

Method: each finding was confirmed by reading the code, then reproduced with an xUnit test under
`tests/Nalix.SDK.Tests/Audit/` tagged `[Trait("Category", "Audit")]`. No production code under `src/` was changed.
Findings without a reproducing test were discarded (see "Considered and not recorded").

## Summary

| ID | Title | Severity | Location | Repro test |
| :--- | :--- | :--- | :--- | :--- |
| SDK-01 | `DisconnectAsync()` during auto-reconnect backoff does not stop the reconnect loop | High | `src/Nalix.SDK/Transport/Internal/ReconnectSupervisor.cs:62`, `:86-104`; `src/Nalix.SDK/Transport/TcpSession.cs:295-307` | `ReconnectDisconnectAuditTests.DisconnectAsync_WhenIssuedDuringReconnectBackoff_StopsReconnectLoop` |
| SDK-02 | Proof-of-work solver runs on server-chosen difficulty with no timeout or cancellation | Medium | `src/Nalix.SDK/Transport/Extensions/HandshakeExtensions.cs:130`; `src/Nalix.Codec/ProtocolFrames/Security/ProofOfWorkChallenge.cs:82-91`; `src/Nalix.Codec/Security/ProofOfWorkSolver.cs:25-50` | `HandshakePowAuditTests.HandshakeAsync_WhenServerDemandsHighDifficultyProofOfWork_HonorsConnectTimeout` |
| SDK-03 | A drop between the supervisor's final check and its ready publish is lost; ready is reported for a dead session | Low | `src/Nalix.SDK/Transport/Internal/ReconnectSupervisor.cs:131-141` | `ReconnectReadyRaceAuditTests.ReconnectLoop_WhenDropLandsAfterResumeBeforeReadyPublish_DoesNotReportReadyForDeadSession` |

## Test run

Command: `dotnet test tests/Nalix.Tests.sln --configuration Release --filter Category=Audit`

Result: **3 failed, 0 passed** (Total 3). All three tests are in `Nalix.SDK.Tests`; the other test projects
report no matching tests. Every failure is the intended reproduction, not a fixture error:

- `DisconnectAsync_WhenIssuedDuringReconnectBackoff_StopsReconnectLoop`: "…a new connection was opened after an application-initiated disconnect."
- `HandshakeAsync_WhenServerDemandsHighDifficultyProofOfWork_HonorsConnectTimeout`: "HandshakeAsync did not finish within ConnectTimeoutMillis (500 ms)…"
- `ReconnectLoop_WhenDropLandsAfterResumeBeforeReadyPublish_DoesNotReportReadyForDeadSession`: "Ready was published for a session that had already dropped…"

Stability: SDK-01 failed in 3 of 3 consecutive isolated runs; SDK-02 and SDK-03 failed in every run so far.

---

## SDK-01 — `DisconnectAsync()` during auto-reconnect backoff does not stop the reconnect loop

- **Severity:** High
- **Location:** `src/Nalix.SDK/Transport/Internal/ReconnectSupervisor.cs:62` (flag consumed only on `OnDisconnected`), `:86-104` (loop checks only `AutoReconnectEnabled`); `src/Nalix.SDK/Transport/TcpSession.cs:295-307` (`DisconnectAsync`)
- **Repro test:** `tests/Nalix.SDK.Tests/Audit/ReconnectDisconnectAuditTests.cs` — `DisconnectAsync_WhenIssuedDuringReconnectBackoff_StopsReconnectLoop`

**Description.** `DisconnectAsync()` calls `MarkIntentionalDisconnect()`, then tears down the socket. The flag is only
consumed by `ReconnectSupervisor.OnDisconnected`, which runs only when a live socket is torn down. When the
supervisor is in its backoff delay (or in `ConnectWithResumeDetailedAsync`), there is no live socket. So
`DisconnectInternalAsync` raises no event, the `finally` in `DisconnectAsync` clears the flag, and nothing stops the
loop. `ReconnectLoopAsync` only checks `AutoReconnectEnabled` on each iteration, so it keeps connecting.

**Impact.** An application that calls `DisconnectAsync()` (logout, user-initiated close) while the session is
waiting to reconnect is reconnected silently after the backoff. The session resumes or re-handshakes, and
`OnReauthenticateAsync` / `OnResumedAsync` run against a session the user believes is closed. The intentional
disconnect is effectively ignored.

**Reproduction.** A raw `TcpListener` accepts the first connection, the test closes it (deterministic unexpected
drop), calls `DisconnectAsync()` during the 1.5 s backoff, and then checks `listener.Pending()` after 5 s. A pending
connection means the loop reconnected. Observed: pending connection present, test fails.

**Suggested fix (text only).** Make the intentional stop a property of the reconnect state, not of a one-shot event.
For example, have `DisconnectAsync()` set a "stop reconnect" flag that `ReconnectLoopAsync` checks before each
attempt and after each delay, and have the loop observe a cancellation token that `DisconnectAsync()` cancels. Clear
the flag when the application explicitly calls `ConnectAsync()`. The current `ConsumeIntentionalDisconnect()` can
then be removed from the loop-start decision, or kept only for the event path.

---

## SDK-02 — Proof-of-work solver runs on server-chosen difficulty with no timeout or cancellation

- **Severity:** Medium
- **Location:** `src/Nalix.SDK/Transport/Extensions/HandshakeExtensions.cs:130` (solver call, no `ct`, no deadline); `src/Nalix.Codec/ProtocolFrames/Security/ProofOfWorkChallenge.cs:82-91` (`Validate` checks nonce, MAC and timestamp, but not `Difficulty`); `src/Nalix.Codec/Security/ProofOfWorkSolver.cs:25-50` (unbounded `while (true)` loop)
- **Repro test:** `tests/Nalix.SDK.Tests/Audit/HandshakePowAuditTests.cs` — `HandshakeAsync_WhenServerDemandsHighDifficultyProofOfWork_HonorsConnectTimeout`

**Description.** When the server answers with `POW_REQUIRED`, the client solves the challenge in
`ProofOfWorkSolver.SolveChallenge(...)`. That call takes no cancellation token and runs synchronously until a
solution is found. `ProofOfWorkChallenge.Validate` never bounds `Difficulty` (a `byte`, so up to 255), and the
challenge's MAC is only checked for being non-zero, so the client spends CPU work before it can authenticate the
challenge. `ConnectTimeoutMillis` and the caller's `CancellationToken` only apply to the waits around the solve,
not to the solve.

**Impact.** A server, or an on-path party that can inject a `POW_REQUIRED` answer, can pin a client thread for an
unbounded time during connect, and cancellation and the connect timeout do not interrupt it. The SDK cannot
abandon the attempt. Because the solve happens on whichever thread continues the handshake, it can also hold a
thread-pool worker. Difficulty 40 is about 2^40 hashes, which is effectively unbounded.

**Reproduction.** A scripted session answers `SessionInit` with `POW_REQUIRED` and the PoW request with a challenge
of difficulty 40, with `ConnectTimeoutMillis = 500`. `HandshakeAsync` must complete within 5 s. Observed: it does
not, so the test fails. The handshake runs on the thread pool in the test, since the fake delivers the challenge
synchronously.

**Caveat.** While the bug is present, the failing run leaves a thread-pool worker spinning in the solver for the rest
of the test process. This follows from the bug being reproduced; it is not a fixture defect.

**Suggested fix (text only).** Bound the accepted difficulty on the client (for example, reject `Difficulty` above a
configured maximum in `ProofOfWorkChallenge.Validate` or at the call site). Thread a cancellation token and a
deadline into `ProofOfWorkSolver.SolveChallenge` and check them periodically (for example, every 2^16 iterations).
Derive the deadline from `ConnectTimeoutMillis`. Consider verifying the challenge MAC before solving if the MAC key
is available to the client.

---

## SDK-03 — A drop between the supervisor's final check and its ready publish is lost

- **Severity:** Low
- **Location:** `src/Nalix.SDK/Transport/Internal/ReconnectSupervisor.cs:131-141`
- **Repro test:** `tests/Nalix.SDK.Tests/Audit/ReconnectReadyRaceAuditTests.cs` — `ReconnectLoop_WhenDropLandsAfterResumeBeforeReadyPublish_DoesNotReportReadyForDeadSession`

**Description.** After resume or re-auth, the loop checks `Volatile.Read(ref _disconnectDuringAttempt) != 0 ||
!_session.IsConnected` (line 131), then sets `_readyTcs = null` and publishes ready (lines 139-140). A drop that
happens after `IsConnected` is read and before the publish is handled by `OnDisconnected`. That handler sees
`_readyTcs` still set, so it sets `_disconnectDuringAttempt = 1` and returns. Nothing reads the flag again, so the
loop publishes ready for a session that has dropped, and no reconnect follows.

**Impact.** `WaitUntilReadyAsync()` and `RequestAsync` waiting on the reconnect return success for a dead session.
Requests then fail with "not connected" rather than waiting for the next reconnect. The window is narrow, so
the impact is limited to a misreported ready state and a failed request, not a hang or data exposure.

**Reproduction.** A scripted session answers the resume request with a valid success response and injects one drop
at the first `IsConnected` read after the resume hook has run. That read is the post-resume check, so the drop lands
exactly in the window. Observed: ready completes while `IsConnected` is false, so the test fails.

**Suggested fix (text only).** Re-check the disconnect state after publishing, or publish ready only after
re-checking under the same lock that `OnDisconnected` uses to set the flag (for example, make publish and flag set
mutually exclusive with a lock or a single `Interlocked` state machine). If the flag is set after the check, loop
again instead of publishing.

---

## Considered and not recorded

These were examined and dropped, either because no failing test could be demonstrated or because the behavior is
by design:

- **TOFU key fetch is unauthenticated** (`HandshakeExtensions.cs:172-187`). This is the documented trust-on-first-use
  mode. The proof check still runs against the fetched key. Not a defect.
- **`On<T>` / `OnOnce` / `SubscribeTemp` leaks.** Subscriptions are removed on the normal paths (`OnOnce` unsubscribes
  itself on fire; `CompositeSubscription` and `DelegateDisposable` clean up). The handshake's `serverHello`
  (`HandshakeExtensions.cs:147`, `:139-143`) is never disposed. That is a pooled-object return issue only if
  `SessionChallenge` pools its buffers, which was not verified. Not recorded.
- **Unsolicited `SessionChallenge` injection** (`HandshakeExtensions.cs:92-95`, `predicate: _ => true`). An on-path
  attacker can inject a challenge that wins the race. The client then fails closed on the proof check, which is
  equivalent to dropping packets. Fail-closed availability only; no key or identity impact.
- **Non-constant-time proof comparison** (`HandshakeExtensions.cs:210`, `:244`, `serverHello.Proof != expectedProof`).
  Possible timing signal on a MAC. Not measured, so not recorded. Worth a separate look at `Bytes32` equality.
- **Stale-frame delivery during pool re-rent** (`PendingRequest.OnMessageReceived` reads the generation at call time,
  after `TransportSession.DispatchPendingMessage` has released the lock). A stale frame of the same opcode can reach a
  re-rented request. Mitigated for default requests by per-request SequenceId correlation (`RequestExtensions.cs:137-139`),
  but reachable with a custom predicate. Requires a precise race; not reproduced.
- **Handshake timeout covers only the first wait.** `CancelAfter(ConnectTimeoutMillis)` (`HandshakeExtensions.cs:87`)
  applies to the first `SessionChallenge` wait, while the later `RequestAsync` steps use their own per-step timeouts
  (`:121`, `:141`, `:177`, `:234`). The total handshake can exceed `ConnectTimeoutMillis`. This is consistent with how
  the per-step timeouts are documented, so not recorded as a defect.

## Notes for the test suite

- The raw-`TcpListener` fixture is used for SDK-01 because, in this environment, `NetworkApplication.DeactivateAsync()`
  often did not produce an observable client-side drop within 40 s. The cause was not isolated. `TcpFrameReader` treats
  a zero-byte read as a fault (`n == 0`), so the SDK's EOF handling is not the apparent cause. The existing
  `ReconnectSupervisorTests` passes, so this is a fixture-timing difference, not a general failure.
- SDK-02's test spins a thread-pool worker for the rest of the test process. Consider moving it to a separate test
  collection or a separate test process if the spin affects timing-sensitive tests.
