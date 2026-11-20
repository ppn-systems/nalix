# Nalix.Network

## Triggers
- Modifying connection lifecycle or per-IP limits
- Touching rate limiting, ban logic, or flood protection
- Working with session persistence or zero-RTT resume
- Adding or changing transport (TCP/WebSocket/datagram) behavior

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
- Datagram (NDP over UDP): pick a `DeliveryMethod` per send/handler — unreliable or sequenced for position updates and telemetry, reliable ordered/unordered for state; channels are independent
- Datagram anti-replay: every datagram is AEAD-sealed and checked against a 2048-entry packet-number window; forged, duplicate or too-old datagrams are **silently dropped** and never answered

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
4. On success: restore the stream sequence numbers from session attributes
5. On failure: disconnect; do not allow retry with the same token (it is already consumed)

### Add a new listener
1. Implement `IListener`
2. Register via `builder.MapTcp<TProtocol>()` or `builder.MapDatagram<TProtocol>()`
3. Wire to a dispatcher — each listener can bind to a specific dispatch channel

---

## Gotchas

- **Session token consumed even on failed resume**: `ConsumeAsync()` is remove-on-read. A resume attempt that fails due to invalid state or expired token still removes the session. The client must fully re-authenticate — there is no retry with the same token.

- **Creating sessions on disconnect is wrong**: Sessions must be created at `CLIENT_FINISH`. Creating them at disconnect loses the session data needed for zero-RTT resume before any disconnect can occur.

- **Datagram drops are silent**: unauthenticated and replayed datagrams are discarded without logging. Monitor `DatagramListener.Metrics` (malformed / unknown-CID / unauthenticated drops, guard rejections) and `DatagramConnection.EngineReport`.
- **Datagram timers run on `DatagramTimingWheel` (1 ms tick, one per listener), not `TimingWheel`**: the TCP wheel ticks once a second and only disconnects idle connections; ACK delay, PTO and pacing need millisecond deadlines and a callback into the engine. `Internal/Datagram/DatagramSocketConnection` sends outside the engine lock, so never call `DatagramListener.SendTo` while holding it.
- **Datagram sessions go through `IConnectionGuard`**: a session takes a per-IP slot when its first datagram authenticates (not at ticket issue) and releases it on close.

- **`SpinLock` protection**: `ConnectionLimitEntry` uses a standard `Queue<long>` (not a thread-safe `ConcurrentQueue`). Always acquire the `SpinLock` before reading/modifying any tracking info or queue timestamps to prevent race conditions.

- **X-Forwarded-For without trusted proxy config = IP spoofing**: Enabling proxy header parsing without configuring trusted IPs allows any client to set their apparent IP to any value via the header.

- **Progressive ban tiers compound**: A client that triggers bans repeatedly accumulates `BanCount`. The ban duration for the Nth violation is longer than the first. Ban count decays via `LastBanTimeTicks` but only after a configurable idle period — a client that persistently violates will stay in high ban tiers.
