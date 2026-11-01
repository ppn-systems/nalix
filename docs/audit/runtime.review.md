# Runtime audit review (`docs/audit/runtime.md`)

Reviewer pass over the audit on `audit/runtime` (commit `9378f5a`). Production code under `src/` was **not** changed.
Only `tests/Nalix.Runtime.Tests/Audit/PipelineAuditTests.cs` was edited.

## Verdict table

| ID | Claim (audit) | Verdict | Severity (audit → review) | Repro result |
| :--- | :--- | :--- | :--- | :--- |
| RT-01 | `AlwaysExecute` outbound middleware skipped when handler throws | **CONFIRMED (narrowed)** | Medium → Medium | Fails for the claimed reason (sync handler throw). Async handler throws are not affected; see note. |
| RT-02 | Failed sync execution leaks `ActiveExecutions` and a runner slot | **CONFIRMED** | Low → Low | Fails for the claimed reason (`ActiveExecutions` = 1, expected 0). Fallback detail corrected; see note. |
| RT-03 | Session resume proof is a replayable bearer credential | **CONFIRMED** | High → High (conditional on on-path capture) | Fails for the claimed reason (`attacker.Secret` restored). |
| RT-04 | Dispatcher permission gate fails open without `[PacketPermission]` | **CONFIRMED** | Medium → Medium | Fails for the claimed reason (`CanExecute` = True). |

Lead verdicts (from "Considered and not filed"):

| Lead | Verdict | Reason |
| :--- | :--- | :--- |
| Dispatch-channel lease double-release on rollback (`PacketDispatchChannel.cs:287-293`, `DispatchChannel.cs:513-525`) | **NEEDS-MORE-EVIDENCE** | Code confirms the ordering: `TryEnqueue` takes ownership, then the `!IsActive` rollback disposes the *dequeued* item and returns `false`, and the caller disposes its own lease again. The double release only hits our lease when the queue holds nothing ahead of it, and it needs `IsActive` to flip between two checks. No deterministic repro. |
| Lock-free tombstone ABA (`DispatchChannel.cs:708-729`) | NEEDS-MORE-EVIDENCE | Not reproduced or re-read in depth. |
| Priority reordering (`TryDequeueHighest`, `:640-679`) | NEEDS-MORE-EVIDENCE | Ordering is intentional by priority, so the question is per-connection FIFO expectations. Needs a spec decision. |
| Silent worker death without restart (`PacketDispatchChannel.cs:603-614`) | NEEDS-MORE-EVIDENCE | The catch logs and the `finally` decrements `generation.Active`. A restart path was not found in the read, but a deterministic fault injection was not built. Worth a follow-up test. |
| Non-atomic counter clamps (`DispatchChannel.cs:915-931`) | NEEDS-MORE-EVIDENCE | Not reproduced. |
| `PacketContext.Initialize` strands a context on a throw (`PacketContext.cs:183-186`) | **FALSE-POSITIVE** | Sets `InUse` before null checks, but only a programmer error (null packet or connection) reaches the throw. The GC reclaims the instance, and no valid-input path triggers it. |
| `PolicyRateLimiter.cs:194` (`RequestsPerSecond <= 0` allowed) | **FALSE-POSITIVE (by design)** | The inline comment at that branch deliberately treats non-positive rates as "no limit". The attribute's XML doc does not state this, so it is a documentation gap, not a defect. |
| `continueOnError = true` falls through a throwing permission middleware | **FALSE-POSITIVE (by design)** | Documented in `.claude/skills/nalix-runtime.md` ("logs exceptions and skips the failing middleware"). Opt-in. |
| Docs drift: `ConcurrencyMiddleware` in `.claude/skills/nalix-runtime.md:82` | **CONFIRMED (docs only)** | No such type exists in `src/Nalix.Runtime/Middleware/Standard/`, which contains only `PermissionMiddleware`, `RateLimitMiddleware`, and `TimeoutMiddleware`. Low. Fix the skill table. |

## Per-finding notes

### RT-01: `AlwaysExecute` outbound skipped on handler throw: CONFIRMED, narrowed

- **Contract source.** `.claude/skills/nalix-runtime.md` ("Middleware Execution") says `AlwaysExecute = true` middleware runs "even when it throws". The `MiddlewareStageAttribute.AlwaysExecute` XML doc (`src/Nalix.Abstractions/Middleware/MiddlewareStageAttribute.cs:21-24`) describes only the `SkipOutbound` case and says nothing about handler exceptions. The throw-path contract therefore rests on the skill doc alone. Recommend aligning the attribute XML doc.
- **Scope narrowed.** The sync path is the defect. `ExecuteTerminalHandler` → `AwaitHandlerAndRespondAsync` (`PacketDispatchOptions.Execution.cs:~270-310`) catches non-fatal handler exceptions, sends a FAIL control reply, and returns normally. For an async handler that throws, the pipeline therefore never sees the exception and `OutboundAlways` runs. The audit's mention of `AWAIT_HANDLER_AND_OUTBOUND_ASYNC` as a path that skips is not reachable for handler exceptions in practice. `AWAIT_HANDLER_AND_OUTBOUND_ASYNC` only swallows cancellation.
- **Consequence noticed in passing (not filed).** Sync handler exceptions propagate to the caller of `ExecuteAsync`, while async handler exceptions are converted into a FAIL reply and swallowed. That asymmetry may be intentional; it is not documented. Needs a design decision.
- **Repro.** `PipelineAuditTests.RT01_AlwaysExecuteOutboundMiddleware_RunsWhenHandlerThrows`: fails with `Invocations` = 0, expected 1. Re-run confirmed.
- **Guard added.** `PipelineAuditTests.RT01_Guard_AlwaysExecuteOutboundMiddleware_RunsWhenAsyncHandlerThrows`. It passes, and documents that the defect is bounded to sync throws. An earlier draft asserted a throw on the async path. That was wrong because the exception is intentionally converted to a FAIL reply, so it was rewritten as a guard.

### RT-02: failed sync execution leaks `ActiveExecutions`: CONFIRMED, Low

- Verified at `MiddlewarePipeline.cs:~204` and `:270-275`. `runner.RunAsync()` is outside any try/finally on the non-empty pipeline path, so a synchronous handler throw skips `RECORD_EXECUTION` (which decrements `_activeExecutions`) and skips `RETURN_RUNNER_SYNC`.
- **Correction to the audit.** After 32 leaked runner slots, `ACQUIRE_RUNNER` returns `null` and the code falls back to the **global** `s_pool.Get<PooledPipelineContext>()`. It is not a fresh allocation per call. The impact is pool pressure and metric drift, which matches "no security impact".
- **Repro.** `PipelineAuditTests.RT02_SyncHandlerException_DoesNotLeakActiveExecutionCounter`: fails with `ActiveExecutions` = 1, expected 0.

### RT-03: replayable session-resume proof: CONFIRMED, High (conditional)

- The resume handler is `[PacketEncryption(false)]` (`SessionHandlers.cs:42-47`). The proof is HMAC over token ‖ 30 s window with ±1 window tolerance (`IsResumeProofValid`), with no server nonce and no binding to a challenge. `RestoreSessionSnapshot` copies `Secret`, `Level`, and `Algorithm` onto the presenting connection.
- **By design?** Not as far as the docs go. `.claude/skills/nalix-network.md` documents zero-RTT resume and remove-on-read consumption, but nothing about transferability, nonces, or transport protection. Zero-RTT resume is intentionally transport-agnostic, so the fix must be a challenge or a channel binding, not connection binding.
- **Transport check.** No TLS path exists for the TCP or UDP session transports under `src/`. The only `SslStream` / TLS usage is the WebSocket client option (`src/Nalix.SDK/Options/WebSocketTransportOptions.cs`, `WebSocketSession.cs`). So a resume request on TCP/UDP is plaintext on the wire.
- **Severity.** High is defensible: a captured proof yields full session takeover (Secret and Level) for about 90 s. The precondition is on-path capture plus winning a race with the legitimate client. The audit's wording already states that precondition. Keep High, and say so in the report.
- **Repro.** `SessionResumeAuditTests.RT03_CapturedResumeProof_CannotRestoreSessionOnAnotherConnection`: fails with `attacker.Secret.IsZero` = False. The test is a valid property check: a captured request should not be sufficient on its own. It cannot distinguish attacker from legitimate client on a new connection, so any fix must add a server-issued nonce, which changes zero-RTT.

### RT-04: permission gate fails open without `[PacketPermission]`: CONFIRMED, Medium

- `PacketHandler.CanExecute` (`src/Nalix.Runtime/Internal/Compilation/PacketHandler.cs:119-124`) checks permission only when `Metadata.Permission` is non-null. The SEC-77 comment calls the same method a "fail-closed baseline", which is contradicted by the code. `PermissionMiddleware` (SEC-37) denies the same case.
- **Default enforcement.** `PermissionMiddleware` is **not registered anywhere in `src/`**, so the dispatcher gate is the only default permission enforcement. That makes the fail-open reachable by default, not only when middleware is configured.
- **No backstop.** Grep of `analyzers/` finds no diagnostic that requires `[PacketPermission]`. The generator only forwards the attribute (`PacketHandlerGenerator.cs:1051-1079`).
- **Built-in handlers.** Verified that every built-in handler in `src/Nalix.Runtime/Handlers/` declares `[PacketPermission]`. The second `[PacketOpcode]` hit in `SystemControlHandlers.cs` is only a comment.
- **Repro.** `HandlerPermissionAuditTests.RT04_HandlerWithoutPermissionAttribute_IsDeniedByDispatcherGate`: fails with `CanExecute` = True.
- **Severity.** Medium is kept. The skill doc requires the attribute on every method, so omission is a developer error. Because nothing backstops it, High would also be defensible.

## Performance review (requested scope)

Method: static scan of `src/Nalix.Runtime` hot paths (dispatch, middleware, routing, throttling). No benchmarks were run, so these are code-level findings only.

- **LINQ.** No LINQ operators (`Select`, `Where`, `Any`, `ToList`, `ToArray`, `FirstOrDefault`, `OrderBy`) in `src/Nalix.Runtime`. No finding.
- **Locks.** `MiddlewarePipeline` registration locks (`:81`, `:103`, `:143`) are cold-path. `TokenBucketLimiter` per-state locks are per connection, so not a shared hot lock. `HandshakeHandlers` `s_initLock` is initialization only. `DispatchChannel._stateMutationLock` and `TimeSynchronizer._gate` were located but not profiled. No finding filed.
- **Per-packet diagnostics.** `PacketSender` builds an interpolated diagnostic string per send, but only under `#if DEBUG` and behind `IsEnabled`. `PacketDispatchOptions.Execution.cs` builds interpolated diagnostics only on failure paths, behind `IsEnabled`. No finding.
- **Allocations.** Not measured. The only allocation finding in the audit is RT-02's fallback, corrected above to the global pool, not a per-call allocation.

## Test run

Command: `dotnet test tests/Nalix.Runtime.Tests/Nalix.Runtime.Tests.csproj --configuration Release --filter Category=Audit` (SDK 10.0.403, local install at `/root/.dotnet`).

Result: 5 total, 4 failed, 1 passed. The four failures are the intended repros (RT-01 sync, RT-02, RT-03, RT-04), each failing at its assertion for the claimed reason. The passing test is the RT-01 async guard.

Build and test were run against `tests/Nalix.Runtime.Tests`. The full `tests/Nalix.Tests.sln` audit run showed no other `Category=Audit` tests.

## Changes made by this review

- `tests/Nalix.Runtime.Tests/Audit/PipelineAuditTests.cs`:
  - Added `RT01_Guard_AlwaysExecuteOutboundMiddleware_RunsWhenAsyncHandlerThrows` (passing guard) to bound RT-01 to sync throws.
- No production code changed. No finding deleted, since none is a false positive that was filed. The audit's "not filed" list was re-classified above rather than edited.

## Open items

1. Align `MiddlewareStageAttribute.AlwaysExecute` XML doc with the skill doc's handler-exception contract (or vice versa).
2. Decide whether async handler exceptions should be converted to FAIL (current) while sync ones propagate. Document whichever is intended.
3. Fix the `ConcurrencyMiddleware` row in `.claude/skills/nalix-runtime.md`.
4. Build a deterministic repro for the lease double-release (requires an `IsActive` flip hook between `TryEnqueue` and the rollback check).
5. Document `PacketRateLimitAttribute` `RequestsPerSecond <= 0` as "unlimited", matching the dispatcher comment.
6. RT-03: design decision needed (server nonce for resume, which trades off zero-RTT) and whether plaintext resume over TCP/UDP is acceptable.
7. RT-04: add an analyzer diagnostic for handler methods without `[PacketPermission]`, and/or deny on `Permission is null` in `CanExecute`.
