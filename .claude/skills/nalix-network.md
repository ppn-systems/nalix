# Nalix.Network

## Triggers
- Modifying connection lifecycle or per-IP limits
- Touching rate limiting, ban logic, or flood protection
- Working with session persistence or zero-RTT resume
- Adding or changing transport (TCP/UDP/WebSocket) behavior

---

## Rules

### Session Lifecycle

- Session snapshot is created **immediately at `CLIENT_FINISH`** (handshake completion) — not on disconnect, not lazily on first resume
- `SessionHandlers.ConsumeAsync()` is atomic retrieve-and-remove (SEC-33) — retrieves the session and removes it in one operation to prevent TOCTOU with parallel resume requests using the same token
- Session tokens are Snowflake IDs (`ulong`). Proof of possession uses HMAC-Keccak256 with a sliding window: t-1, t, t+1 where t = current 30-second window.
- Sessions are pooled objects — call `session.Return()` after use; do not hold references beyond the handler

### ConnectionHub

- Sharded for concurrency — never bypass sharding with direct shard access when broadcasting; always use `BroadcastAsync()`
- Shard count is fixed at construction — size it for peak expected connections
- `ConnectionHub` is the single source of truth for active connections — do not maintain a separate tracking list elsewhere

### ConnectionGuard (Rate Limiting)

- `ConnectionLimitEntry` uses a `SpinLock` to protect both the `ConnectionLimitInfo` and a standard `Queue<long>` for sliding-window timestamps. It is NOT thread-safe on its own and all operations must be performed under the lock.
- Ban tiers are progressive: `BanCount` increments on each ban, `LastBanTimeTicks` enables decay — ban duration grows with each repeated violation
- X-Forwarded-For is only trusted if the source IP is in the configured trusted proxy list — **not auto-trusted**
- DDoS log suppression: only one warning is logged per IP per throttle window; `SuppressedDDoSCount` tracks suppressed entries

### Transport Selection
- TCP: reliable ordered delivery — use for game state, commands, authentication handshake
- UDP: best-effort unordered — use for position updates, telemetry, time-sensitive events
- UDP anti-replay: sequence IDs older than the sliding window are **silently dropped** — not counted as errors, not logged by default

### Connection State
- `connection.Secret.IsZero` = connection not yet authenticated — use as pre-auth guard
- Each `Connection` has local per-connection object pools (reduces global pool contention)
- Malformed packet counter increments per bad packet — connection is disconnected when the threshold is reached
- Fragmented frames (TCP `SocketConnection`) are accepted only when `connection.Level >= NetworkCallbackOptions.MinFragmentPermissionLevel` (default `ESTABLISHED`, granted by `CLIENT_FINISH` or session resume). Lower-level fragments are dropped before any `FragmentAssembler` is created. Handshake frames are never fragmented.

---

## Checklists

### Configure per-IP connection limits
1. Set `ConnectionGuardOptions`: max concurrent connections, daily limit, ban tier durations
2. If behind a load balancer: add trusted proxy IPs to enable X-Forwarded-For trust
3. Tune ban escalation: set `BanDurationSeconds` array (each index = one ban tier)
4. Verify `DDoS log suppression` window is appropriate for your traffic pattern

### Implement session resume (server side)
1. After `CLIENT_FINISH`: session is already saved by `HandshakeHandlers.SaveSessionAsync()`
2. Client sends `SessionResume` frame with token
3. `ConsumeAsync()` retrieves and removes the session atomically — even a failed resume removes the session
4. On success: restore TCP/UDP sequence numbers from session attributes
5. On failure: disconnect; do not allow retry with the same token (it is already consumed)

### Add a new listener
1. Implement `IListener`
2. Register via `builder.ListenTcp<TProtocol>()` or `builder.ListenUdp<TProtocol>()`
3. Wire to a dispatcher — each listener can bind to a specific dispatch channel

---

## Gotchas

- **Session token consumed even on failed resume**: `ConsumeAsync()` is remove-on-read. A resume attempt that fails due to invalid state or expired token still removes the session. The client must fully re-authenticate — there is no retry with the same token.

- **Creating sessions on disconnect is wrong**: Sessions must be created at `CLIENT_FINISH`. Creating them at disconnect loses the session data needed for zero-RTT resume before any disconnect can occur.

- **UDP drops are silent**: The anti-replay window silently discards old sequence IDs. Elevated drop rates will not appear in error logs — monitor via the metrics/report endpoint of `ConnectionGuard`.

- **`SpinLock` protection**: `ConnectionLimitEntry` uses a standard `Queue<long>` (not a thread-safe `ConcurrentQueue`). Always acquire the `SpinLock` before reading/modifying any tracking info or queue timestamps to prevent race conditions.

- **X-Forwarded-For without trusted proxy config = IP spoofing**: Enabling proxy header parsing without configuring trusted IPs allows any client to set their apparent IP to any value via the header.

- **Progressive ban tiers compound**: A client that triggers bans repeatedly accumulates `BanCount`. The ban duration for the Nth violation is longer than the first. Ban count decays via `LastBanTimeTicks` but only after a configurable idle period — a client that persistently violates will stay in high ban tiers.
