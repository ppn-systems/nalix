# Review of Network UDP / Session Audit

**Reviewed:** `docs/audit/network-udp-session.md` (audit branch `audit/network-udp-session`, commit `3f025fd`).
**Method:** Re-read every cited source location, ran the audit repro tests in Release, and checked each failure
message against the claimed cause. No production code under `src/` was changed.

Classification rule applied: UDP unreliability (loss, reordering, no retransmit, connectionless delivery) is by
design and is not reported unless it contradicts documented intent or is an exploitable hole.

## Verdict table

| ID | Audit severity | Verdict | Review severity | Repro result (Release) | Note |
| :--- | :--- | :--- | :--- | :--- | :--- |
| NETUDP-01 | High | CONFIRMED | High | Fails: `Expected False ... but found True` | Mechanism verified: `TryCheck(0)` after `60000` gives `forward = 5536 <= 16384`, so the replay is treated as a forward jump. |
| NETUDP-02 | Medium | CONFIRMED | Medium | Fails: `Expected True ... but found False` | Not "by design" loss: `SlidingWindow.cs:61-62` states the window must advance on burst loss, and the code violates that. Blackout is about 45,536 datagrams before the 16-bit wrap. |
| NETUDP-03 | High | CONFIRMED | High (mechanism corrected) | Fails: `ReceivedPackets` expected 0, found 1 | See correction below. Control test `ProcessDatagram_GenuineTaggedDatagram_IsAccepted` passes. |

Command: `dotnet test tests/Nalix.Network.Tests/Nalix.Network.Tests.csproj --configuration Release --filter Category=Audit`
→ Total 4, Passed 1 (control), Failed 3 (the three reproductions above).

## Corrections to the audit

### NETUDP-03: attacker cost is overstated

The audit says an attacker needs one observed datagram and a "cheap" birthday search, which "finds a collision in well
under a second". That is not the attack. A birthday pair only helps if the attacker controls both datagrams, and the
attacker does not control the genuine one. The real forgery is a **second preimage** against the observed datagram's
digest: find a forged payload `F` with `XxHash32(F) = XxHash32(G)`, where `G` is the observed datagram. That costs about
2^32 offline XxHash32 evaluations and needs no secret. It is still a practical break, so the severity stays High.
The repro test models the outcome (a payload with an identical digest is accepted), and its collision pair is a
stand-in for that search. I updated the test's doc comment to say so. The test assertions are unchanged.

Online guessing is also possible: a random 4-byte tag matches with probability 2^-32 per datagram, and the audit
already notes this.

### Fix scope is incomplete (NETUDP-03)

The audit's fix text lists `UdpListener.Receive.cs`, `UdpFrameSender.cs`, and `UdpFrameReader.cs`. It misses the
third producer of the same tag: `src/Nalix.Runtime/Internal/Pipelines/PacketPipeline.cs:207-216`
(`SEND_UDP_ASYNC`, which computes `XxHash32(secret, seed: XxHash32(datagram))` and uses its own
`UdpMacTagSize`). All three must change together with the wire-format bump, or the server and client will disagree.

## Additional observations (not repro-tested)

| ID | Area | Verdict | Evidence | Why no failing test |
| :--- | :--- | :--- | :--- | :--- |
| PERF-01 | `PacketPipeline.cs:207-214` (`SEND_UDP_ASYNC`) | Optimization, verified by reading | Each UDP send rents a second buffer and copies the full payload (`current.Span.CopyTo(signedLease.SpanFull)`) only to append a 4-byte tag. | A copy count is not a correctness defect, so there is no pass/fail repro. Needs a benchmark to size the impact. |
| PERF-02 | `SlidingWindow.cs:47` | Optimization, low | Every datagram takes `lock (_syncObj)` on the receive path. Uncontended here (receive loop is single-threaded per listener), so the cost is small. | Performance only. |
| OBS-01 | `UdpListener.Receive.cs:40` and SDK reader | Observation | Tag comparison uses `!=` on `uint`, not a constant-time comparison. | Moot while the tag is unkeyed. The keyed-MAC fix must compare in constant time. |

No additional correctness defects in scope were found beyond the three audit findings, so no new repro tests were added.

## Not re-audited

The audit's "Checked and not reported" section (amplification, pinning, resume, expiry, lazy window init) was
accepted as stated. I did not re-derive those conclusions, and `InMemorySessionStore` lies outside the Network project.

## Test changes made in this review

- `tests/Nalix.Network.Tests/Audit/UdpSession/UdpDatagramTagAuditTests.cs`: doc comment for
  `FindXxHash32Collision` corrected to describe the second-preimage attacker model. No assertion changed.
- No tests were deleted; none is a false positive.
