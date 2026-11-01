# Audit: Nalix.Network — TCP side

Scope: `src/Nalix.Network` TCP path — listeners (`Listeners/TcpListener/*`), connection lifecycle
(`Connections/Connection.cs`, `Internal/Transport/SocketConnection*.cs`), `ConnectionGuard` ban tiers and
slot accounting (`RateLimiting/Connection.Guard*.cs`), PROXY-protocol handshake, frame length validation.

Method: read the code paths, then keep a finding only when a test reproduces it on the current code.
Repro tests live in `tests/Nalix.Network.Tests/Audit/TcpConnectionGuardAuditTests.cs` and carry
`[Trait("Category", "Audit")]`. No production code under `src/` was changed.

Run: `dotnet test tests/Nalix.Tests.sln --configuration Release --filter Category=Audit`

Result on `audit/network-tcp` (current code): **2 run, 2 failed, 0 passed** — both failures are the intended
reproductions (see each finding below).

---

## Summary

| ID | Title | Severity | Location | Repro test | Status |
| :--- | :--- | :--- | :--- | :--- | :--- |
| NETTCP-01 | Ban-count decay re-applied on every connection attempt | Medium | `RateLimiting/Connection.Guard.Slot.cs:156-163`, `:522-529` | `NETTCP01_BanCountDecay_IsAppliedOncePerElapsedWindow_NotOnEveryAttempt` | Reproduced (fails) |
| NETTCP-02 | Cleanup evicts idle entries that still hold an escalated `BanCount` | Medium | `RateLimiting/Connection.Guard.Cleanup.cs:306-316` (cleanup pass `:99-170`) | `NETTCP02_Cleanup_DoesNotEvictIdleEntry_ThatCarriesEscalatedBanCount` | Reproduced (fails) |

---

## NETTCP-01 — Ban-count decay re-applied on every connection attempt

- **Severity:** Medium
- **Location:** `src/Nalix.Network/RateLimiting/Connection.Guard.Slot.cs:156-163` (overload
  `TRY_ACQUIRE_CONNECTION_SLOT(SocketEndpoint, DateTime)`, used by `TryAccept(SocketEndpoint)`) and
  `src/Nalix.Network/RateLimiting/Connection.Guard.Slot.cs:522-529` (overload
  `TRY_ACQUIRE_CONNECTION_SLOT(SocketEndpoint, DateTime, IPAddress)`, used by `TryAccept(IPEndPoint)`, which is
  the TCP accept path via `ProcessAcceptedSocket` → `_limiter.TryAccept(ip)`).

**Description.** The decay step computes how many whole decay windows have elapsed since the last ban:

```csharp
long elapsed = nowTicks - entry.LastBanTimeTicks;
if (elapsed > decayWindowTicks)
{
    int decayTiers = (int)(elapsed / decayWindowTicks);
    entry.BanCount = Math.Max(0, entry.BanCount - decayTiers);
    if (entry.BanCount == 0) entry.LastBanTimeTicks = 0;
}
```

`LastBanTimeTicks` is only reset when `BanCount` reaches zero. When the count is still positive, it is left
at the old timestamp, so the same `elapsed` is computed again on the next connection attempt and the same
number of tiers is subtracted again. Decay is therefore applied once per **connection attempt**, not once per
elapsed window.

**Impact.** Progressive banning is supposed to escalate 1m → 5m → 15m → 1h → 6h → 24h and decay slowly
(`BanCountDecayWindow`, default 7 days). Because the decay repeats on every accept/attempt, a single
long-gap IP whose count is, say, 10 is driven to 0 within a handful of attempts. The next offense then
restarts at the 1-minute tier, so escalation is effectively reset by ordinary traffic. The effect is
gated on `elapsed > BanCountDecayWindow`, so it needs a quiet gap of at least one full window first.

**Repro test.** `NETTCP01_BanCountDecay_IsAppliedOncePerElapsedWindow_NotOnEveryAttempt`
(`tests/Nalix.Network.Tests/Audit/TcpConnectionGuardAuditTests.cs`).
Observed on current code: first attempt 10 → 7 (correct), second attempt 7 → 4 (expected 7).

**Suggested fix (text only).** After applying `decayTiers`, advance the reference point by the consumed
windows so the same tiers cannot be counted twice, e.g.
`entry.LastBanTimeTicks += decayTiers * decayWindowTicks;` (or set it to `nowTicks - (elapsed % decayWindowTicks)`),
and keep the `BanCount == 0` reset. Apply the same change to both overloads so they stay in sync.

---

## NETTCP-02 — Cleanup evicts idle entries that still hold an escalated `BanCount`

- **Severity:** Medium
- **Location:** `src/Nalix.Network/RateLimiting/Connection.Guard.Cleanup.cs:306-316`
  (`SHOULD_REMOVE_ENTRY`, invoked from `RUN_CLEANUP_ONCE` at `:99-170`). Contrast with the protected case in
  `src/Nalix.Network/RateLimiting/Connection.Guard.Slot.cs:386`, where the eviction sampler explicitly skips
  entries with `BanCount > 0`.

**Description.** `SHOULD_REMOVE_ENTRY` removes an entry when its ban has expired, it has no active connections,
and `LastConnectionTime` is older than `InactivityThreshold`:

```csharp
long bannedUntil = Interlocked.Read(ref entry.BannedUntilTicks);
if (bannedUntil > cutoff.Ticks) return false;
ConnectionLimitInfo info = entry.Info;
return info.CurrentConnections <= 0 && info.LastConnectionTime < cutoff;
```

It never looks at `entry.BanCount`. The sampler (`TRY_RANDOM_SAMPLE_EVICT`) does protect `BanCount > 0`
entries, so the two eviction paths disagree about what state is worth keeping. Escalation is stored only in
the entry, so removing the entry discards it.

**Impact.** `InactivityThreshold` defaults to 5 minutes; the first ban lasts 1 minute and the decay window is
7 days. An attacker that is banned, goes quiet for a few minutes after the ban expires, and returns is
re-evaluated from `BanCount = 0`, so each cycle restarts at the 1-minute tier instead of escalating. This
defeats progressive banning for any source that paces its attempts.

**Repro test.** `NETTCP02_Cleanup_DoesNotEvictIdleEntry_ThatCarriesEscalatedBanCount`
(`tests/Nalix.Network.Tests/Audit/TcpConnectionGuardAuditTests.cs`). Sets `BanCount = 3`, an expired ban,
and an idle `LastConnectionTime`, runs `RUN_CLEANUP_ONCE`, and expects the entry to survive.
Observed on current code: the entry is removed.

**Suggested fix (text only).** Treat `BanCount > 0` as "keep" in `SHOULD_REMOVE_ENTRY`, mirroring the
sampler's guard at `Connection.Guard.Slot.cs:386`. Optionally keep a separate, longer retention for entries
with a non-zero `BanCount` (bounded by `BanCountDecayWindow`) so that memory stays bounded.

---

## Investigated and not reported

These were read but not confirmed by a failing test, so they are not findings:

- **Accept/close accounting parity.** Every `TryAccept` on the accept, PROXY, and channel-full paths has a
  matching `Release` or `ConnectionClosed` path (`TcpListener.Handle.cs`, `TcpListener.ProxyProtocol.cs`,
  `TcpListener.ProcessChannel.cs`). `Disconnect()` routes through `Dispose()`, so the channel-full drop does
  fire `ConnectionClosed`. `SocketEndpoint` equality and hashing are IP-only, so accept-side and close-side
  keys match.
- **PROXY sweep race (possible, not reproduced).** `OnProxyReadCompleted` reads `socket.RemoteEndPoint`
  (`TcpListener.ProxyProtocol.cs:261`, and `TcpListener.Handle.cs:258` when no real endpoint was parsed)
  outside any `try`. If the timeout sweep closes the socket during that window, the exception could escape the
  completion callback, or the guard slot could be left counted. Triggering it deterministically needs control
  over socket disposal timing that the test harness does not expose. Needs a follow-up with a fault-injection
  seam before it can be reported.
- **Frame length validation (UInt16 path).** `SocketConnection.cs:438-455` rejects size prefixes below the
  header size before any negative payload length can form, and `:549` bounds pending frames by
  `s_maxReceiveBufferSize`. The VarInt path also validates `payloadLen` (`SocketConnection.Receive.VarInt.cs:48`).
- **Global counter drift correction** (`Connection.Guard.Cleanup.cs:258`) is consistent with per-entry
  accounting for TCP connections.
