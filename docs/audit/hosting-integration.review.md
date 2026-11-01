# Hosting & Integration Audit — Adversarial Review

**Reviewed:** `docs/audit/hosting-integration.md` (audit branch `audit/hosting-integration`, commit `a7db3d4`)
**Reviewer scope:** re-read every cited location, re-run the `Category=Audit` reproducers, verify the
verdict for each candidate, and check the performance-related candidates (allocations, LINQ, locks, copies).
No production code under `src/` was changed. No test was changed: all 8 reproducers were confirmed as written.

**Verdict legend:** `CONFIRMED` = defect exists as described. `FALSE-POSITIVE` = not a defect, or intentional
design per code comments / docs. `NEEDS-MORE-EVIDENCE` = plausible, but not shown deterministically here.

**Test run:** `dotnet test tests/Nalix.Tests.sln --configuration Release --filter Category=Audit`
→ **Total 8, Passed 5, Failed 3.** Failure messages match the audit report:

| Reproducer | Result | Failure reason observed |
| :--- | :--- | :--- |
| `HOST01a_BarePacketPermission_DeniesUnauthenticatedClient` | FAIL | `Expected: 0, Actual: 1` (handler ran for an unauthenticated client) |
| `HOST02a_UseGroups_WithDefaultHub_WiresMembershipObserverOnActivation` | FAIL | `Assert.NotNull() Failure: Value is null` |
| `HOST02b_UseSessions_WithDefaultHub_WiresPersistenceObserverOnActivation` | FAIL | `Assert.NotNull() Failure: Value is null` |
| `Control_Probe_IsDelivered_ToAdmittedHandler` | PASS | harness delivers probes |
| `Control_ExplicitPermission_DeniesUnauthenticatedClient` | PASS | explicit `USER` denies |
| `Control_ExplicitEncryption_RefusesPlaintextFrame` | PASS | explicit `[PacketEncryption(true)]` refuses plaintext |
| `Control_BarePacketEncryption_RefusesPlaintextFrame` | PASS | bare `[PacketEncryption]` enforced |
| `Control_UseGroups_WithCustomHub_WiresMembershipObserver` | PASS | custom hub path wires observer |

The controls pass, so the harness can observe both delivery and denial. The three failing tests fail for the
reasons claimed, so the zero-count and non-null assertions are not vacuous.

---

## Verdict table — audit findings

| ID | Audit severity | Review verdict | Review severity | Notes |
| :--- | :--- | :--- | :--- | :--- |
| HOST-01 | High | **CONFIRMED** | High | `PacketHandlerGenerator.cs:1077-1079` only forwards `PacketPermission` when `ConstructorArguments.Length > 0`. A bare `[PacketPermission]` yields `permission: null` (line 1118). `PacketHandler.CanExecute` (`PacketHandler.cs:119`) only checks when `Metadata.Permission is { }`, so a null permission admits every connection. The parameterless ctor really does default to `USER` / `MinimumLevel` (`PacketPermissionAttribute.cs:42-45`). Silent fail-open on a documented attribute. |
| HOST-02 | Medium | **CONFIRMED** | Medium | `ServiceRegistrar.cs:76-86` and `:101-111` create the observers only when `GetExistingInstance<IConnectionHub>()` is non-null. The default hub is registered in `PrepareCallbacks` (`NetworkApplicationBuilder.cs:458`) during `ActivateAsync`, after the builder-time `UseSessions`/`UseGroups` call. A grep of `src/` confirms the two observers are constructed only in `ServiceRegistrar`, so nothing creates them later. The builder XML docs (`NetworkApplicationBuilder.Extensions.cs:103-109`, `:123-129`) describe behavior the code does not deliver. |

## Verdict table — discarded / not-proven candidates

| Candidate | Audit outcome | Review verdict | Evidence |
| :--- | :--- | :--- | :--- |
| Bare `[PacketEncryption]` not enforced | Disproven | **FALSE-POSITIVE** | Control test passes. Optional ctor params are materialized, so the generator forwards the attribute. |
| `Bytes32` comparison not constant-time | Disproven | **FALSE-POSITIVE** | `Bytes32.cs:228-245` reduces with `Avx2.Xor`/`Avx.TestZ` (and SSE4.1 fallback), with no early exit. |
| SDK trusts server key by default (`AllowTofu = true`) | Design decision | **FALSE-POSITIVE (by design)** | `TransportOptions.cs` documents TOFU and defaults it to `true`. The SDK verifies the server proof (`HandshakeExtensions.cs:209`). Keep as a documentation / production-hardening note, not a defect. |
| UDP 32-bit `XxHash32` tag, not a MAC | Suspected, not reproduced | **NEEDS-MORE-EVIDENCE** | `UdpListener.Receive.cs:356-358` compares `XxHash32.Compute(secret, seed: payloadHash)` with a 32-bit tag. A seeded non-cryptographic hash is not a MAC, so the design weakness is real. Its exploitability (online attempts, anti-replay limits) is unproven. Severity should be set by a follow-up. |
| Session resume plaintext, proof not bound to connection | Suspected replay | **NEEDS-MORE-EVIDENCE** | `SessionHandlers.cs:39-40` sets `[PacketEncryption(false)]` on `SESSION_SIGNAL`, so the resume frame is plaintext by annotation. Whether a captured frame replays successfully depends on the full SDK resume flow, which was not exercised. |
| Ctrl+C / `ProcessExit` skips `NetworkApplication` deactivation | Suspected | **NEEDS-MORE-EVIDENCE** | `Bootstrap.cs:50-82` disposes `InstanceManager` and cancels workers. It does not call `DeactivateAsync` on the application. Whether that leaks listeners is unproven. The audit's reasoning is sound; a dedicated isolated test is needed. |
| Plaintext `DISCONNECT`/`ERROR`/`FAIL` on post-handshake session | Design | **FALSE-POSITIVE (by design)** | `SystemControlHandlers.cs:37-41` explicitly sets `[PacketEncryption(false)]` for control frames. Impact is limited, since TCP already permits RST. |
| Handshake / rekey handlers allow unauthenticated access | By design | **FALSE-POSITIVE (by design)** | `HandshakeHandlers.cs:44-47` and `SystemControlHandlers.cs:37-40` use `PacketPermission(NONE)` for pre-auth frames. Post-handshake gates (`HandshakeEstablished`, `EncryptedOnWire`) exist per `CanExecute`. |
| `FileCertificateStore` writes server private key | Verified OK | **FALSE-POSITIVE** | `FileCertificateStore.cs:92` passes `isPrivate: true`. |

## Review corrections to "Other observations"

| Item | Verdict | Evidence |
| :--- | :--- | :--- |
| `.claude/skills/nalix-hosting.md:59`: calling `ActivateAsync()` twice deadlocks | **CONFIRMED documentation error (no defect)** | `NetworkApplication.cs:137-146` returns early when `_isStarted`, and the gate is released in `finally`. No deadlock. Fix the skill doc. |
| `.claude/skills/nalix-hosting.md:114`: "The four system handlers are always added" | **CONFIRMED documentation error** | Handlers are mapped only through `UseTimeSync` / `UseSystemControl` / `UseSecureConnections` / `UseSessions` (`NetworkApplicationBuilder.Extensions.cs`). Fix the skill doc. |
| `NetworkApplication.DeactivateAsync` empty `if` swallows error | **CONFIRMED, Low** | `NetworkApplication.cs:262-265`: `if (_logger.IsEnabled(LogLevel.Error)) { }` in a catch. The exception is dropped with no log. |
| `ServiceRegistrar.RegisterObjectPoolManager` empty `catch` is "dead code" | **CONFIRMED, but overstated; Informational** | `ServiceRegistrar.cs:218-227`: the catch body is a comment-only `if` followed by `throw;`. It rethrows, so it does not swallow the error. It is a no-op, not a defect. |

---

## Performance verification (requested)

The requested performance pass covered allocations, LINQ, locks, and copies on the hosting, dispatch,
handshake, and SDK transport paths. **No performance finding is confirmed in this scope.** Candidates
considered:

| Candidate | Verdict | Evidence |
| :--- | :--- | :--- |
| LINQ or allocating calls on the hosting / runtime-internal / listener / SDK-transport hot paths | **FALSE-POSITIVE (none found)** | Grep for `Where/Select/Any/All/ToList/ToArray/OrderBy/First/Count`, `new List<`, and string concatenation. The only hit in SDK transport is the `ms.ToArray()` below. |
| `DirectiveGuard.TryAcquire` takes a `lock` per call | **FALSE-POSITIVE** | `DirectiveGuard.cs:46` locks the per-connection `state.DirectiveGuardLock`, so it is uncontended across connections. It is also skipped when cooldown is `<= 0`. |
| `DispatchChannel` `_stateMutationLock` (`DispatchChannel.cs:741`, `:827`) | **NEEDS-MORE-EVIDENCE** | A single channel-wide lock on state create and remove. It is taken on connection state creation and removal, not on every packet, but this was not benchmarked under connection churn. Do not treat it as a finding without a measurement. |
| `HandshakeHandlers` `s_initLock` (`:307`, `:326`) | **FALSE-POSITIVE** | Init-only, double-checked with `Volatile`. Not on the per-packet path. |
| `WsFrameReader.cs:160` `ms.ToArray()` copy per frame | **FALSE-POSITIVE** | `new MemoryStream()` (line 123) always exposes its buffer, so `TryGetBuffer` succeeds and the `ToArray` fallback is unreachable in practice. The large-message path does grow a `MemoryStream`, but that is bounded by `MaxMessageSize` and is not a regression. |

## Missed issues (added)

No new in-scope defect was confirmed with a deterministic reproducer in this pass, so **no new repro tests were
added**. The reviewer did not invent findings to pad the list.

## Test changes

None. The 8 reproducers in `tests/Nalix.Hosting.Tests/Audit/` were checked against their stated purpose and
failure messages, and all are correct. The reproducers and the audit report were brought onto this branch from
`audit/hosting-integration` so the review is self-contained.

## Recommended follow-ups (ordered)

1. **HOST-01 fix** — treat zero-argument `PacketPermission` as the parameterless default in
   `PacketHandlerGenerator`, and add a generator test for the bare attribute. Consider an analyzer diagnostic
   for handlers with no permission attribute. Fail-closed in `CanExecute` needs a deliberate opt-in because it
   changes behavior.
2. **HOST-02 fix** — wire the observers after the default hub exists (flag from `UseSessions`/`UseGroups`, or
   re-run wiring from `RegisterConnectionHub`), and add default-hub and custom-hub regression tests.
3. **Skill doc corrections** — remove the false deadlock claim and the "always added" claim from
   `.claude/skills/nalix-hosting.md`.
4. **Follow-up investigations (NEEDS-MORE-EVIDENCE)** — UDP tag strength (upgrade to a real MAC), session
   resume binding, shutdown ordering in `Bootstrap`, and `DispatchChannel` lock contention under churn.
