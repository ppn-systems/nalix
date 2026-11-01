# Runtime audit (`src/Nalix.Runtime`)

Scope: packet dispatch, handler invocation, middleware stages and ordering, throttling, protocol ordering, exception leakage, concurrency.
Reproduction tests: `tests/Nalix.Runtime.Tests/Audit/` (`[Trait("Category","Audit")]`). All four fail on the current code.

Run: `dotnet test tests/Nalix.Runtime.Tests/Nalix.Runtime.Tests.csproj -c Release --filter Category=Audit`
Result: 4 failed, 0 passed (each failure is the expected assertion; see per-finding notes).

No production code was changed.

## Findings

### RT-01 — `AlwaysExecute` outbound middleware is skipped when the handler throws
- **Severity:** Medium
- **File:** `src/Nalix.Runtime/Middleware/MiddlewarePipeline.cs:620-626, 670-675, 712-718` (handler exception propagates before `SET_STAGE(OutboundAlways)` at 679/712)
- **Description:** A handler exception (non-cancellation) escapes `EXECUTE_HANDLER_FULL_ASYNC` / `AWAIT_HANDLER_AND_OUTBOUND_ASYNC` before the `OutboundAlways` stage runs. The repo's documented invariant (`.claude/skills/nalix-runtime.md`, "Middleware Execution") states `AlwaysExecute = true` middleware "runs after the handler even when it throws".
- **Impact:** Cleanup, audit, and metrics middleware registered as `AlwaysExecute` silently do not run on handler failures, so audit trails are incomplete and any per-request resource release they perform is lost.
- **Repro:** `PipelineAuditTests.RT01_AlwaysExecuteOutboundMiddleware_RunsWhenHandlerThrows` — fails: `Invocations` is 0, expected 1.
- **Suggested fix (text only):** In the handler-exception path, run the `OutboundAlways` stage in a `finally`-equivalent (capture the exception, run `OutboundAlways`, then rethrow), in both the sync fast path and the async wrapper. Update the attribute XML doc to state the handler-exception behaviour explicitly.

### RT-02 — Failed synchronous execution leaks `ActiveExecutions` (and a runner slot)
- **Severity:** Low
- **File:** `src/Nalix.Runtime/Middleware/MiddlewarePipeline.cs:190` (`runner.RunAsync()` unguarded), `:171-178` and `:204-206` (`RECORD_EXECUTION` only on success)
- **Description:** When the terminal handler throws synchronously and no inbound middleware exists, the exception escapes `ExecuteAsync` before `RECORD_EXECUTION` runs and before `RETURN_RUNNER_SYNC` returns the pooled runner. `_activeExecutions` stays incremented and the local-pool bit stays set.
- **Impact:** Metrics drift upward. After 32 such failures the 32-slot local pool is exhausted and the fallback allocates per call. No security impact.
- **Repro:** `PipelineAuditTests.RT02_SyncHandlerException_DoesNotLeakActiveExecutionCounter` — fails: `ActiveExecutions` is 1, expected 0.
- **Suggested fix (text only):** Wrap the synchronous `RunAsync()` / `GetResult()` in try/finally that always calls `RECORD_EXECUTION` and returns the runner.

### RT-03 — Session resume proof is a replayable bearer credential
- **Severity:** High (requires an on-path observer of a plaintext resume request and winning a race against the legitimate client)
- **File:** `src/Nalix.Runtime/Handlers/SessionHandlers.cs:42-93` (`[PacketEncryption(false)]`), `:135-163` (`IsResumeProofValid`: HMAC over token ‖ 30 s window, ±1 window), `:170-175` (`RestoreSessionSnapshot` copies `Secret` and `Level`)
- **Description:** The resume request is accepted on a plaintext transport and its proof is not bound to the presenting connection or to any server-issued challenge. Any party that captures the request can present it first on its own connection; the token is consumed on success, so the legitimate client then fails with `SESSION_EXPIRED`. The attacker's connection receives the session's `Secret` and `Level`.
- **Impact:** Full session takeover (the attacker obtains the session key and the elevated level). Persists for the proof's ~90 s validity window.
- **Repro:** `SessionResumeAuditTests.RT03_CapturedResumeProof_CannotRestoreSessionOnAnotherConnection` — fails: `attacker.Secret.IsZero` is False.
- **Suggested fix (text only):** Require a server-issued single-use challenge (nonce) per resume attempt, bound into the proof, and/or send the resume request only over an authenticated/encrypted channel. Note that resume must still work on a new transport, so binding to the original `ConnectionId` is not the right fix.

### RT-04 — Dispatcher permission gate fails open when `[PacketPermission]` is omitted
- **Severity:** Medium
- **File:** `src/Nalix.Runtime/Internal/Compilation/PacketHandler.cs:119-124` (`if (Metadata.Permission is { } p && …)`), generator `analyzers/Nalix.Analyzers.Generators/PacketHandlerGenerator.cs:1051` (`tPermission = "null"` when the attribute is absent)
- **Description:** `CanExecute` checks permission only when the metadata carries one. A handler method without `[PacketPermission]` gets `permission: null` and is executed for every connection level, including `NONE`. The SEC-77 comment in the same method calls this a "fail-closed baseline", and `PermissionMiddleware` (SEC-37) denies the same case, so the two gates disagree. No analyzer in `analyzers/` enforces the attribute. All built-in Runtime handlers declare it, so exposure depends on application handlers.
- **Impact:** A developer omission silently grants unauthenticated access to an application opcode.
- **Repro:** `HandlerPermissionAuditTests.RT04_HandlerWithoutPermissionAttribute_IsDeniedByDispatcherGate` — fails: `CanExecute` returns True, expected False.
- **Suggested fix (text only):** Treat `Permission is null` as deny in `CanExecute` (matching `PermissionMiddleware`), and/or add an analyzer diagnostic that requires `[PacketPermission]` on every `[PacketOpcode]` handler.

## Considered and not filed

- **Dispatch-channel lease double-release on rollback (subagent finding, arithmetic verified by reading).** `PacketDispatchChannel.cs:287-293` retains the lease and, when `PushCore` returns false, disposes it again. `DispatchChannel.cs:513-525` already disposes the dequeued reference on the `!IsActive` rollback. That is one reference released twice on that path. No reproduction was written, so it is not filed. **Recommended follow-up:** build a reproduction that deactivates the connection between enqueue and the `IsActive` check, and assert the lease refcount.
- **Other unverified leads from the same subagent:** a cross-connection tombstone ABA in the lock-free lookup (`DispatchChannel.cs:708-729`), priority reordering of per-connection packets (`TryDequeueHighest`, `:640-679`), silent worker death without restart (`PacketDispatchChannel.cs:603-614`), non-atomic counter clamps (`DispatchChannel.cs:915-931`), and `PacketContext.Initialize` stranding on a throw (`PacketContext.cs:183-186`). Not reproduced; treat as leads.
- **`PolicyRateLimiter.cs:194`:** `RequestsPerSecond <= 0` returns Allowed, while invalid burst (`:200-211`) denies. Could be intentional ("no limit"); needs a design decision.
- **`continueOnError = true`** lets a throwing permission middleware fall through to the handler. Opt-in and documented as cautious, so not filed.
- **Docs drift:** `.claude/skills/nalix-runtime.md` lists `ConcurrencyMiddleware`, which does not exist in `src/Nalix.Runtime/Middleware/Standard/`.
