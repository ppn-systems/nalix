# Hosting & Integration Audit

**Scope:** `src/Nalix.Hosting` startup/shutdown, auto-registered handlers, insecure defaults, and the
handshake/auth path between the SDK client and the server (downgrade, unauthenticated access, graceful
shutdown leaks).

**Method:** Findings were confirmed by reading the code, then reproduced with an xUnit test that fails on the
current code. Each reproducer lives under `tests/Nalix.Hosting.Tests/Audit/` and carries
`[Trait("Category", "Audit")]`. No production code under `src/` was changed. Candidates that could not be
reproduced deterministically are listed under *Discarded / not proven* and are not counted as findings.

**Audit run:** `dotnet test tests/Nalix.Tests.sln --configuration Release --filter Category=Audit`
→ **8 total, 5 passed, 3 failed.** The 3 failures are the reproducers below. The 5 passes are controls
(see *Controls*).

---

## Summary

| ID | Title | Severity | Status | Repro test |
| :--- | :--- | :--- | :--- | :--- |
| HOST-01 | Bare `[PacketPermission]` is silently dropped; handler is reachable without authorization (fail-open) | High | Reproduced (FAILS) | `HOST01a_BarePacketPermission_DeniesUnauthenticatedClient` |
| HOST-02 | `UseSessions()` / `UseGroups()` never wire their disconnect observers with the default hub | Medium | Reproduced (FAILS) | `HOST02a_UseGroups_WithDefaultHub_WiresMembershipObserverOnActivation`, `HOST02b_UseSessions_WithDefaultHub_WiresPersistenceObserverOnActivation` |

---

## HOST-01 — Bare `[PacketPermission]` silently dropped; handler reachable without authorization

- **Severity:** High
- **Location:**
  - Root cause: `analyzers/Nalix.Analyzers.Generators/PacketHandlerGenerator.cs:1077-1079`
    (`name == PacketPermissionAttributeMetadataName && a.ConstructorArguments.Length > 0`). When the guard fails,
    `tPermission` stays `"null"` (line 1051) and the generated metadata gets `permission: null` (line 1118).
  - Enforcement that is skipped: `src/Nalix.Runtime/Internal/Compilation/PacketHandler.cs:119`
    (`if (Metadata.Permission is { } permission && ...)`), inside `CanExecute` (line 114).
  - Intended default: `src/Nalix.Abstractions/Networking/Packets/PacketPermissionAttribute.cs:42-45`
    (parameterless constructor sets `Level = USER`, `Evaluation = MinimumLevel`).

- **Description:** `PacketPermissionAttribute` has a parameterless constructor that defaults to `USER`. The
  generator only forwards the attribute when it has constructor arguments, so a bare `[PacketPermission]` is
  discarded at compile time. The generated handler metadata then has `Permission == null`, and
  `PacketHandler.CanExecute` only checks permission `if (Metadata.Permission is { } permission ...)`. A null
  permission therefore admits every connection, including unauthenticated `NONE`-level clients. The fail-closed
  baseline described in the `SEC-77` comment does not apply to these handlers.

- **Impact:** Any handler written with the bare attribute, which is the natural way to say "authenticated users
  only", is callable by unauthenticated clients. The author believes the handler is protected, and it is not.
  The failure is silent: no build warning, and no runtime error.

- **Repro test:** `tests/Nalix.Hosting.Tests/Audit/HOST01_BareHandlerAttributeAuditTests.cs` →
  `HOST01a_BarePacketPermission_DeniesUnauthenticatedClient`
  - Actual: `1` (handler ran for an unauthenticated client). Expected: `0`.

- **Suggested fix (text only):**
  1. In `PacketHandlerGenerator`, treat a zero-argument `PacketPermission` as
     `new PacketPermissionAttribute()` (use `USER` / `MinimumLevel`). Apply the same to any other attribute that has
     a parameterless form. Do not gate on `ConstructorArguments.Length > 0`; read the attribute's effective
     constructor arguments instead.
  2. Add an analyzer diagnostic (NALIX0xx) that flags a `PacketHandler` method without a `PacketPermission`
     attribute, so public handlers must opt in explicitly with `PacketPermission(PermissionLevel.NONE)`.
  3. Consider failing closed in `CanExecute` when a handler has no permission metadata at all. This is a
     behavior change, so gate it on an explicit opt-in or a major version bump.
  4. Add a generator test that asserts `Permission` is non-null for a bare `[PacketPermission]`.

---

## HOST-02 — `UseSessions()` / `UseGroups()` never wire their disconnect observers with the default hub

- **Severity:** Medium
- **Location:**
  - `src/Nalix.Hosting/Internal/ServiceRegistrar.cs:76-86` (`RegisterSessions`: creates
    `SessionPersistenceObserver` only if `IConnectionHub` is already registered).
  - `src/Nalix.Hosting/Internal/ServiceRegistrar.cs:101-111` (`RegisterGroups`: creates
    `GroupMembershipObserver` only if `IConnectionHub` is already registered).
  - Called at builder time from `src/Nalix.Hosting/NetworkApplicationBuilder.Extensions.cs:118` (`UseSessions`)
    and `:136` (`UseGroups`).
  - The default hub is registered only in `PrepareCallbacks`, `src/Nalix.Hosting/NetworkApplicationBuilder.cs:458`,
    which runs from `NetworkApplication.ActivateAsync`, `src/Nalix.Hosting/NetworkApplication.cs:145`. Nothing
    re-runs the observer wiring after that.

- **Description:** `UseSessions()` and `UseGroups()` run during builder configuration, before the default
  `ConnectionHub` exists, so `GetExistingInstance<IConnectionHub>()` returns `null` and the observer blocks
  are skipped. The default hub is then created in `PrepareCallbacks` at `ActivateAsync`, and no code path
  creates the observers afterwards. A grep of `src/` confirms no other place constructs
  `SessionPersistenceObserver` or `GroupMembershipObserver`. The builder docs
  (`NetworkApplicationBuilder.Extensions.cs:103-109` and `:123-129`) say these observers are installed, so
  behavior and documentation disagree. The defect only appears with the default hub: when
  `UseConnectionHub(...)` is called first, the observer is created (see control `Control_UseGroups_WithCustomHub_WiresMembershipObserver`).

- **Impact:**
  - `UseGroups()`: when a connection disconnects, it is not removed from its groups. Broadcasts to groups can
    target dead connections, and group memberships accumulate.
  - `UseSessions()`: the disconnect-driven persistence hook is not installed, so session state is not saved on
    disconnect, which weakens resume. The failure is silent.

- **Repro tests:** `tests/Nalix.Hosting.Tests/Audit/HOST02_DefaultHubObserverAuditTests.cs`
  - `HOST02a_UseGroups_WithDefaultHub_WiresMembershipObserverOnActivation`: actual `null`, expected non-null.
  - `HOST02b_UseSessions_WithDefaultHub_WiresPersistenceObserverOnActivation`: actual `null`, expected non-null.

- **Suggested fix (text only):** Move observer creation out of the builder-time path. Either:
  1. Create the observers in `PrepareCallbacks`, after `RegisterConnectionHub(_state)`, by recording a
     "wire observers" intent (for example, a flag set by `UseSessions` / `UseGroups` on `HostingBuilderContext`),
     or
  2. Have `RegisterConnectionHub` (or `RegisterSessions` / `RegisterGroups`) re-run after the hub is registered,
     and make both observer creators idempotent.
  Add a regression test for each path (default hub and custom hub) that asserts the observer is non-null after
  `ActivateAsync`.

---

## Controls (passing, to validate the harness)

These passing tests prove the probe harness delivers packets and that the denial/encryption paths behave
correctly. Without them, the zero-count assertions above would be vacuous.

| Test | What it proves |
| :--- | :--- |
| `Control_Probe_IsDelivered_ToAdmittedHandler` | A probe reaches a handler that admits `NONE`-level clients (count 1). |
| `Control_ExplicitPermission_DeniesUnauthenticatedClient` | An explicit `[PacketPermission(USER)]` denies an unauthenticated client. |
| `Control_ExplicitEncryption_RefusesPlaintextFrame` | An explicit `[PacketEncryption(true)]` refuses a plaintext frame. |
| `Control_BarePacketEncryption_RefusesPlaintextFrame` | A bare `[PacketEncryption]` is still enforced (see *Discarded*). |
| `Control_UseGroups_WithCustomHub_WiresMembershipObserver` | With a hub supplied first, the group observer is created (isolates HOST-02 to the default-hub path). |

---

## Discarded / not proven

These candidates were investigated and are **not** counted as findings.

| Candidate | Outcome |
| :--- | :--- |
| Bare `[PacketEncryption]` not enforced | **Disproven.** `PacketEncryptionAttribute` has optional parameters, which the compiler materializes in `ConstructorArguments`, so the generator forwards it. Covered by a control test. |
| `Bytes32` proof comparison is not constant-time | **Disproven.** `Bytes32.Equals` (`src/Nalix.Abstractions/Primitives/Bytes32.cs:228`) is implemented with an XOR/`TestZ` reduction. |
| SDK trusts server-supplied key by default (`TransportOptions.AllowTofu = true`) | **Design decision, not a reproducible defect.** TOFU is documented (`TransportOptions.cs:97-100`), and the SDK does verify the server proof (`HandshakeExtensions.cs:209`). Owner should confirm the default is intended for production. |
| UDP datagram integrity uses a 32-bit `XxHash32`-based tag, not a MAC (`src/Nalix.Network/Listeners/UdpListener/UdpListener.Receive.cs:356-358`) | **Suspected weakness, not reproduced.** A forgery test needs about 2^31 attempts, so there is no deterministic repro. Recommend a cryptographic MAC (for example, Poly1305 or HMAC) with a full-length tag in a follow-up. |
| Session resume request is sent in plaintext and its proof is not bound to the connection (`src/Nalix.Runtime/Handlers/SessionHandlers.cs:39-40`, `:135-163`) | **Suspected replay, not reproduced in this pass.** A deterministic repro needs the full SDK resume flow with a captured frame. Recommend a follow-up that binds the proof to the transport or transcript. |
| Ctrl+C / `ProcessExit` disposes `InstanceManager` without deactivating `NetworkApplication` listeners (`src/Nalix.Hosting/Bootstrap.cs:50-82`) | **Suspected graceful-shutdown gap, not reproduced.** Reproducing it means invoking the private hook, which disposes process-wide singletons and would contaminate other tests. Recommend a dedicated, isolated test. |
| Plaintext `DISCONNECT` / `ERROR` / `FAIL` accepted on a post-handshake session (`src/Nalix.Runtime/Handlers/SystemControlHandlers.cs:37-41`) | **Design, not a defect.** `[PacketEncryption(false)]` is intentional for control frames. Impact is limited to on-path injection on TCP, which already permits RST. |
| Handshake and rekey handlers allow unauthenticated access (`HandshakeHandlers.cs:44-47`, `SystemControlHandlers.cs:37-40`) | **By design.** The `NONE` permission is required for pre-auth handshake frames. The post-handshake checks (`HandshakeEstablished`, `EncryptedOnWire`) are present and correct. |
| `FileCertificateStore` writes the server private key | **Verified OK.** The key is written with `isPrivate: true` (`src/Nalix.Runtime/Security/FileCertificateStore.cs:92`). |

---

## Other observations (not findings)

- `.claude/skills/nalix-hosting.md` documents two behaviors that the code does not implement:
  - "Calling `ActivateAsync()` twice without `DeactivateAsync()` will deadlock." `ActivateAsync` returns early
    when `_isStarted` is set and releases the gate in `finally` (`NetworkApplication.cs:137-193`), so it does not
    deadlock.
  - "The four system handlers are always added." They are added only when the matching `Use*` method is called
    (`NetworkApplicationBuilder.Extensions.cs`).
- `NetworkApplication.DeactivateAsync` (`NetworkApplication.cs:262-265`) has an empty `if` block that swallows
  the error instead of logging it. Low severity.
- `ServiceRegistrar.RegisterObjectPoolManager` (`ServiceRegistrar.cs:223-227`) has an empty `catch` body that
  does nothing before `throw;`. Dead code.
