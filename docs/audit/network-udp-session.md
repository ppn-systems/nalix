# Network UDP / Session Audit

**Scope:** `src/Nalix.Network` UDP path and session-related code: UDP anti-replay window, sequence handling,
datagram authentication, endpoint pinning, and amplification/spoofing exposure. Session resume (`Nalix.Runtime`)
was reviewed only where the UDP path depends on it.

**Method:** Each finding was confirmed by reading the code, then reproduced by an xUnit test under
`tests/Nalix.Network.Tests/Audit/UdpSession/` (`[Trait("Category","Audit")]`). Each test FAILS on the current code.
Findings without a failing reproduction were discarded (see "Checked and not reported").

**Status:** No production code under `src/` was changed. Findings are not fixed.

## Summary

| ID | Title | Severity | Location | Repro test |
| :--- | :--- | :--- | :--- | :--- |
| NETUDP-01 | Replayed datagram accepted when it is 49152–65535 sequence numbers behind the newest | High | `src/Nalix.Network/Internal/Security/SlidingWindow.cs:65-66` | `SlidingWindowAuditTests.TryCheck_ReplayOfSequenceFarBehindNewest_IsRejected` |
| NETUDP-02 | Legitimate forward jump above 16384 is rejected; UDP session blacks out for about 45000 datagrams | Medium | `src/Nalix.Network/Internal/Security/SlidingWindow.cs:65-66, 84-86` | `SlidingWindowAuditTests.TryCheck_ForwardJumpAfterBurstLossBeyondQuarterRing_IsAccepted` |
| NETUDP-03 | UDP datagram tag is an unkeyed 32-bit XxHash32, so a forged payload with a reused tag is accepted | High | `src/Nalix.Network/Listeners/UdpListener/UdpListener.Receive.cs:355-359` | `UdpDatagramTagAuditTests.ProcessDatagram_ForgedPayloadWithTagOfObservedDatagramWithSameXxHash32_IsRejected` |

NETUDP-01 and NETUDP-02 share one root cause: the 16-bit sequence is compared with a serial-arithmetic
threshold (`maxForwardJump = 16384`). Under 16-bit serial arithmetic, a jump of 20000 forward is the same
value as a jump of 45536 backward, so no single threshold can be right for both cases. The fix therefore has to
widen the sequence (see the suggested fix for NETUDP-01), not just move the constant.

---

## NETUDP-01 — Replay accepted from the wrapped part of the ring

- **Severity:** High (anti-replay bypass)
- **Location:** `src/Nalix.Network/Internal/Security/SlidingWindow.cs:58` (forward distance), `:65-66` (forward-jump bound)
- **Repro test:** `tests/Nalix.Network.Tests/Audit/UdpSession/SlidingWindowAuditTests.cs` →
  `TryCheck_ReplayOfSequenceFarBehindNewest_IsRejected`

**Description.** `TryCheck` computes `forward = (ushort)(seq - currentMax)` and treats any `0 < forward <= 16384` as a
legitimate forward jump. A replayed sequence that is `d` behind the newest has `forward = 65536 - d`. For
`d` in `[49152, 65535]`, `forward` lands in `[1, 16384]`, so the replay is classified as a forward jump. It passes the
check, rewinds `_maxSeen` to the replayed value, and clears the bitmap. The comment at lines 60-64 says a replay after
more than 32768 newer packets "cannot be misinterpreted as a forward jump". The code does not hold that property for
`d >= 49152`.

**Reproduction.** Accept sequences 0, 16000, 32000, 48000, 60000 (each step is within the forward bound). Then
`TryCheck(0)` returns `true`. It must return `false`. Observed: `Expected boolean to be False ... but found True`.

**Impact.** An attacker who captured a valid datagram can replay it once the session is at least 49152 sequence
numbers ahead of it. The UDP datagram MAC is still valid, so the replay passes `ProcessDatagram`'s checks, provided the
source endpoint matches the pinned endpoint (spoofable, since UDP has no handshake). The side effect is also a
denial of service: after the rewind, legitimate datagrams are classified as "too old" until the sequence comes back
within 16384 of the replayed value. Depending on `d`, that drops up to 16384 legitimate datagrams.

**Suggested fix (text only).** Do not classify by 16-bit serial distance alone. Options:
(a) carry a 32-bit or 64-bit datagram sequence number in the header (or in the authenticated trailer) so that the window
compares full-width values with no wrap ambiguity; or
(b) if the wire format must stay 16-bit, require forward jumps to be confirmed before they rewind the window (for
example, keep a second-stage "pending jump" that is only committed once a second in-range packet arrives), and never
let a single packet move `_maxSeen` backward or clear the bitmap by more than the window size. Add a regression test for
`d` in `[49152, 65535]` alongside the existing `d = 34900` test in `SlidingWindowTests`.

---

## NETUDP-02 — Burst loss larger than 16384 blacks out the UDP session

- **Severity:** Medium (availability, unauthenticated only through normal network loss)
- **Location:** `src/Nalix.Network/Internal/Security/SlidingWindow.cs:65-66` (bound), `:84-86` (rejection as "too old")
- **Repro test:** `tests/Nalix.Network.Tests/Audit/UdpSession/SlidingWindowAuditTests.cs` →
  `TryCheck_ForwardJumpAfterBurstLossBeyondQuarterRing_IsAccepted`

**Description.** The comment at lines 61-64 says a forward jump "may exceed the window size (burst loss)" and that
the window must be able to advance, but the cap of 16384 rejects any forward jump above a quarter of the ring. A
legitimate client that loses 20000 consecutive datagrams and then sends the next one is rejected (`diff = 45536 >= 1024`
returns `false` at line 86). `_maxSeen` never moves, so every later datagram is rejected too, until the 16-bit counter
wraps back into range (about 45000 datagrams later).

**Reproduction.** `TryCheck(0)` is accepted, then `TryCheck(20000)` returns `false`. It must return `true`.
Observed: `Expected boolean to be True ... but found False`.

**Impact.** A long network outage (for example a mobile handover) on a UDP-heavy session silently disables UDP for
the rest of the connection's lifetime. At 60 datagrams per second that is about 12 minutes; at 20 per second it is
about 37 minutes. Nothing is logged except `replay-window-drop` at Trace level (`UdpListener.Receive.cs:387`).

**Suggested fix (text only).** Same root cause as NETUDP-01, so the same fix applies. Also add a resynchronization
path: when N consecutive datagrams are rejected as "too old" but all pass the MAC check, reset the window to the newest
authenticated sequence and count the event as a metric. Such a reset is safe because the MAC already authenticates
each datagram.

---

## NETUDP-03 — The datagram tag is an unkeyed 32-bit XxHash32, not a MAC

- **Severity:** High (integrity of authenticated UDP payloads)
- **Location:** `src/Nalix.Network/Listeners/UdpListener/UdpListener.Receive.cs:355-359`
  (tag construction and comparison). The client mirrors the same construction at
  `src/Nalix.SDK/Transport/Internal/Udp/UdpFrameSender.cs:105-108` and `UdpFrameReader.cs:146-149`.
- **Repro test:** `tests/Nalix.Network.Tests/Audit/UdpSession/UdpDatagramTagAuditTests.cs` →
  `ProcessDatagram_ForgedPayloadWithTagOfObservedDatagramWithSameXxHash32_IsRejected`
  (control: `ProcessDatagram_GenuineTaggedDatagram_IsAccepted`, which passes and proves the harness works)

**Description.** The tag is `XxHash32.Compute(secret, seed: XxHash32.Compute(datagram))`. XxHash32 is a
non-cryptographic 32-bit hash. The secret enters only as a seed-like input, and the tag is a pure function of the
32-bit digest of the datagram. Any datagram with the same XxHash32 digest as an observed valid datagram therefore
carries the same valid tag, whatever the secret is. The attacker needs only one observed datagram (UDP is plaintext)
and a birthday or second-preimage search over 32-bit digests. The search is cheap: the repro test finds a collision
in well under a second.

**Reproduction.** Find two datagrams with the same XxHash32 digest and the same token and header but different
payloads. Send the second one with the first one's tag through the real `ProcessDatagram`. It must be rejected.
Observed: `ReceivedPackets` is `1`, not `0`, so the forged payload was authenticated.

**Impact.** An attacker who can observe UDP traffic (on-path, or any observer of the path) can inject arbitrary
authenticated UDP payloads for a connection, such as movement or action commands, without the session secret. This
breaks the integrity property the UDP layer relies on, and the replay window does not help because the forged
datagram can carry a fresh sequence number. The 4-byte tag also gives only 2^-32 forgery resistance per random
attempt, even without the collision attack. This conflicts with the project rule "Never invent crypto. Reuse existing
primitives in `Nalix.Codec.Security`", which already provides `HmacKeccak256` (used for session resume).

**Suggested fix (text only).** Replace the XxHash32 tag with a keyed MAC over the full datagram (token, header and
payload) using an existing primitive from `Nalix.Codec.Security`, for example `HmacKeccak256` truncated to at least
128 bits (or a full 256 bits, if the datagram budget allows). Compare in constant time. Apply the same change to the SDK
sender and reader so both sides stay in sync, and bump the wire-format version. The current tag size constant
(`UdpMacTagSize = sizeof(uint)`, line 43) must change with it.

---

## Checked and not reported

These were examined and either ruled out or discarded for lack of proof:

- **Amplification:** no UDP response is ever sent for rejected datagrams. Every reject path only disposes the lease. Only
  `SocketUdpTransport.Send*` sends, and only to the pinned endpoint of an authenticated connection.
- **Endpoint pinning port check** (`UdpListener.Receive.cs:447`): the SDK binds its UDP socket to the TCP local port
  (`src/Nalix.SDK/Transport/UdpSession.cs:167-172`, `TcpSession.cs:182`), so the pinned TCP remote port matches the
  UDP source port by design. Not a defect.
- **Passthrough listener** (`UdpPassthroughListener.cs`): bypasses all Nalix auth, replay and token checks. This is
  documented in its class remarks (Minecraft/RakNet). Not a defect.
- **Session resume and replay across resume:** resume rotates the connection ID (`newToken = connection.ConnectionId`
  in `SessionHandlers.cs:115`), so datagrams sent before a resume carry a token that no longer resolves. The UDP
  sequence counters are reset on resume (`SessionHandlers.cs:105-111`), and the new connection gets a fresh replay window.
  No cross-resume replay was found.
- **Resume proof** (`SessionHandlers.cs:135-158`): HMAC-Keccak256 over token and a 30-second window (±1), consumed
  atomically on success. Replays are blocked by single-use consumption. Not reported.
- **Session expiry:** `InMemorySessionStore` checks `ExpiresAtUnixMilliseconds` on read and during cleanup
  (`src/Nalix.Runtime/Sessions/InMemorySessionStore.cs:242`). Outside the Network project; not deep-audited.
- **Datagram rate limiter** (`DatagramGuard`): fails closed by default (`FailOpenWhenFull = false`).
- **Lazy `UdpReplayWindow` initialization** (`Connection.cs:311-318`, `??=` is not atomic): discarded. The receive loop
  is single-threaded per listener, so no race was demonstrated.
- **UDP `ReceiveSequence` counters** are updated on resume but never used for validation. The security check uses
  `UdpReplayWindow` only. Observation only, not a finding.

## Test results

Commands run:

- `dotnet test tests/Nalix.Network.Tests/Nalix.Network.Tests.csproj --configuration Release --filter Category=Audit`
  → **Failed: 3, Passed: 1, Total: 4** (the failures are the three reproductions above; the passing test is the control).
- `dotnet test tests/Nalix.Tests.sln --configuration Release --filter Category=Audit` → **Network: Failed 3, Passed 1
  (Total 4)**, the same as above. The other test projects (Abstractions, Environment, Runtime, Codec, Framework,
  Hosting, Analyzers, Integration, SDK) reported "No test matches the given testcase filter", because they have no
  Audit tests. That is why the solution-level command exits with code 1. It is not a test failure.
