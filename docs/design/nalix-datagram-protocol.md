# Nalix Datagram Protocol (NDP) v1 — design draft

> Status: **draft for review**. No code yet. This document proposes the protocol that replaces the
> current "UDP companion transport" of `Connection`. Nothing here is implemented.
>
> **Decisions after the first review** (details in [udp-removal-plan.md](udp-removal-plan.md)): the datagram session
> implements `IConnection`; `IConnection` becomes single-transport and today's UDP is removed first; no new project
> (engine in `Nalix.Environment`, crypto in `Nalix.Codec`); defaults as proposed; `KeepStream` is an option; Unity is out
> of scope. Where this document still lists those points as open, the decision above wins.

## 1. Goals and non-goals

**Goals**

- A UDP transport strong enough that TCP is **not needed after the key exchange**: reliable, ordered,
  sequenced and unreliable delivery on independent channels, in one session (RakNet / LiteNetLib class).
- Clients: desktop, mobile and Unity. The wire format is language-neutral and the client engine must be
  portable (see §12).
- Security on every datagram with an AEAD built from primitives that already exist in `Nalix.Codec.Security`.
  No new cryptographic constructions.
- `Connection` becomes TCP-only. UDP gets its own session type, listener and lifetime.

**Non-goals (v1)**

- Browser clients (browsers cannot open raw UDP; they keep using WebSocket).
- A handshake over UDP. v1 bootstraps keys over the existing TCP/WebSocket handshake (§4). A UDP handshake
  with a stateless cookie is reserved for a later version (§12).
- Header protection (hiding packet numbers). Reserved for a later version.

**Why not QUIC (`System.Net.Quic`)?** It needs the native msquic library (Windows 11/Server 2022+, Linux with
`libmsquic`, macOS/Android not officially supported, no Unity), requires TLS certificates, and .NET does not
expose unreliable QUIC datagrams (RFC 9221) yet (dotnet/runtime#123418). Games need unreliable delivery.
NDP borrows QUIC's *proven algorithms* (ACK ranges, loss detection, congestion control, key update,
path validation) and RakNet/LiteNetLib's *delivery model* (delivery methods per channel, message coalescing,
fragmentation), without depending on either.

## 2. Layers

```
+--------------------------------------------------------------+
| Messages      Nalix frames (existing packet format) as bytes |
+--------------------------------------------------------------+
| Reliability   channels, delivery methods, ordering, dedup,   |
|               fragmentation, ACK receipts, priorities        |
+--------------------------------------------------------------+
| Connection    packet numbers, ACK/loss/RTT, congestion,      |
|               MTU, keepalive, close, migration, key update   |
+--------------------------------------------------------------+
| Protection    AEAD seal/open per datagram (ChaCha20-Poly1305)|
+--------------------------------------------------------------+
| UDP                                                          |
+--------------------------------------------------------------+
```

Retransmission works on **frames, not datagrams** (as in QUIC): a lost datagram's reliable frames are re-sent
inside new datagrams with new packet numbers. Packet numbers are never reused, which keeps the AEAD nonce unique.

## 3. Encoding conventions

- Integers in the cleartext header are little-endian (matches `PacketHeader` and the rest of Nalix).
- Inside the encrypted payload, lengths and counters use the QUIC variable-length integer (RFC 9000 §16):
  the two high bits of the first byte select a 1/2/4/8-byte encoding, values up to 2^62-1.
- All sizes are UDP payload sizes (IP and UDP headers excluded).

## 4. Bootstrap over TCP/WebSocket (v1)

1. The client completes the existing Nalix handshake over TCP or WebSocket (X25519, session secret).
2. The client sends a new control packet `DATAGRAM_TICKET_REQUEST` over that stream.
3. The server answers `DATAGRAM_TICKET`:
   - `ConnectionId` — 8 bytes from the CSPRNG (**not** a Snowflake: it must not be predictable), unique in the
     server's datagram session table.
   - `TicketNonce` — 16 random bytes, input to key derivation so every ticket gets fresh keys.
   - `Endpoint` — UDP host/port to use (may differ from the TCP endpoint).
   - `ExpiresIn` — seconds within which the first valid datagram must arrive (default 10 s).
   - Negotiated limits: max idle timeout, max message size, max channels.
4. Both sides derive the datagram keys (§5). The ticket is **single use**.
5. The client sends its first datagram (a `PING`). The session becomes *established* on the server when that
   datagram authenticates.
6. The stream may now be closed. Keeping it open is allowed (`KeepStream` option) but nothing in NDP depends on it.

A new ticket (for example after a network change, or to resume) is obtained by reconnecting the stream and
running the handshake or the existing session-resume flow again.

## 5. Keys and nonces

Derivation uses the same HKDF as the handshake (HKDF-Keccak256, 32-byte output). Today that HKDF is a private
nested class of `HandshakeX25519`, so Codec needs a new **public** derivation API for this; `iv` below is the
first 12 bytes of a 32-byte expand.

`S` is the stream session secret **snapshotted when the ticket is issued**. The datagram session owns its own
keys from then on: a later stream rekey (which ratchets `connection.Secret`) or disposing the stream connection
(which wipes it) does not affect the datagram session.

```
dgram_secret   = HKDF-Expand(S, "nalix-dgram/v1" || ConnectionId || TicketNonce)
c2s_secret     = HKDF-Expand(dgram_secret, "nalix-dgram/c2s")
s2c_secret     = HKDF-Expand(dgram_secret, "nalix-dgram/s2c")
key(x)         = HKDF-Expand(x, "key")            -- 32 bytes, ChaCha20-Poly1305 key
iv(x)          = HKDF-Expand(x, "iv")[0..12]      -- 12 bytes
```

- Separate keys per direction; the stream session key is never used on UDP.
- **Nonce** = `iv XOR (0x00000000 || PN as 64-bit big-endian)` (RFC 9001 §5.3 construction), where PN is the
  full reconstructed packet number. A packet number is used at most once per key.
- **AAD** = the cleartext header (§6) exactly as sent.
- **Key update** (RFC 9001 §6 style): `next_secret = HKDF-Expand(current_secret, "nalix-dgram/ku")`, signalled by
  flipping the `KeyPhase` bit. Initiated after 2^23 packets or 1 hour, whichever first. The receiver keeps the
  previous key for 3 PTO to accept late packets, then wipes it (`Bytes32.Wipe`).
- **Limits** (RFC 9001 §6.6 for ChaCha20-Poly1305): after 2^36 failed decryptions on a session the session is
  closed. The confidentiality limit is far above any realistic packet count; key update covers it anyway.

## 6. Datagram format

```
Cleartext header (13 bytes, authenticated as AAD)
  0   u8    Flags     bits 7..4 Version (=1) | bit 3 KeyPhase | bits 2..0 reserved (must be 0)
  1   u64   ConnectionId (server-issued, same value in both directions)
  9   u32   PacketNumber (low 32 bits; full value reconstructed per RFC 9000 §A.3)
Encrypted payload (one or more frames, §7)
Tag (16 bytes, Poly1305)
```

Overhead: 29 bytes per datagram. A receiver drops — **without any response** — a datagram that is shorter than
`13 + 1 + 16`, has an unknown version, non-zero reserved bits, an unknown ConnectionId, a packet number that
is a duplicate or too old for the replay window, or that fails to decrypt.

Packet numbers start at 0 per direction and increase by 1 per datagram. The replay window covers at least 2048
packet numbers (bitmap, like the existing `SlidingWindow` but 64-bit aware).

## 7. Frames

| Type | Frame | Ack-eliciting | Notes |
| :--- | :--- | :---: | :--- |
| 0x00 | PADDING | no | Fills the datagram (MTU probes). |
| 0x01 | PING | yes | Keepalive / RTT sample / MTU probe. |
| 0x02 | ACK | no | QUIC ACK ranges (§8.1). |
| 0x03 | MESSAGE | depends on delivery method | One message or one fragment (§9). |
| 0x04 | PATH_CHALLENGE | yes | 8 random bytes (§10, item 3). |
| 0x05 | PATH_RESPONSE | yes | Echo of the challenge data. |
| 0x06 | CLOSE | no | Error code (varint), reason length (varint), UTF-8 reason. |
| 0x07 | NEW_TICKET_HINT | yes | Server asks the client to fetch a new ticket (reserved). |
| 0x08 | LIMITS | yes | Receiver's current buffer budgets (§9.5). |

Unknown frame types close the session with `FRAME_ENCODING_ERROR`. Many frames may share a datagram;
this is how small messages are coalesced (RakNet packing / LiteNetLib "merged").

## 8. Connection layer

### 8.1 ACK

ACK frame: `Largest Acknowledged (varint)`, `ACK Delay (varint, microseconds >> 3)`, `ACK Range Count (varint)`,
`First ACK Range (varint)`, then `Range Count` × (`Gap`, `ACK Range Length`) — the RFC 9000 §19.3 layout
without ECN. An ACK is sent after every 2nd ack-eliciting datagram or when `max_ack_delay` (default 25 ms)
expires, and immediately when a gap is detected (fast loss signalling).

### 8.2 RTT, loss detection, retransmission

RFC 9002 algorithms: `smoothed_rtt`, `rttvar`, `min_rtt`; a datagram is lost when a later one is acknowledged
and it is 3 packet numbers older (packet threshold) or older than `9/8 × max(smoothed_rtt, latest_rtt)`
(time threshold). Probe timeout (PTO) with exponential backoff covers tail loss. Frames of lost datagrams go
back to their send queues; only reliable frames are re-sent.

### 8.3 Congestion control and pacing

NewReno as specified in RFC 9002 §7 (slow start, congestion avoidance, recovery, persistent congestion), with
pacing of `1.25 × cwnd / smoothed_rtt`. The controller sits behind an internal interface so CUBIC or BBR can be
added later. Unreliable messages are subject to congestion control as well; when the window is full they wait
in their queue and are **dropped when older than their expiry** (default 100 ms, per send call) instead of being
sent late.

### 8.4 MTU

Start at 1200 bytes (safe on IPv4 and IPv6 paths). Probe upward with padded `PING` datagrams using DPLPMTUD
(RFC 8899) over the candidates 1232, 1350, 1400, 1452, 1472. A probe loss does not count as congestion. The
largest acknowledged probe becomes the path MTU; repeated loss of full-size datagrams falls back to 1200.

### 8.5 Idle timeout, keepalive, close

- Idle timeout = min(client, server) value (default 15 s). A `PING` is sent when nothing ack-eliciting has been
  sent for `idle_timeout / 3`.
- `CLOSE` is authenticated like any frame. After sending or receiving it the session enters *draining* for
  3 × PTO and ignores further datagrams. Close codes: `NO_ERROR`, `INTERNAL_ERROR`, `PROTOCOL_VIOLATION`,
  `FRAME_ENCODING_ERROR`, `FLOW_CONTROL_ERROR`, `IDLE_TIMEOUT` (local only), `AEAD_LIMIT_REACHED`,
  `APPLICATION_ERROR` (+ application code).

## 9. Reliability layer

### 9.1 Delivery methods

| Method | Delivered | Order | Retransmitted |
| :--- | :--- | :--- | :--- |
| `Unreliable` | at most once | none | no |
| `UnreliableSequenced` | at most once; older than newest dropped | per channel | no |
| `ReliableUnordered` | exactly once | none | yes |
| `ReliableOrdered` | exactly once | per channel | yes |
| `ReliableSequenced` | newest guaranteed, older may be skipped | per channel | yes (newest only) |

Optional **ACK receipt** for reliable sends: the send call returns a task that completes when every fragment of
the message has been acknowledged (RakNet "with ack receipt").

### 9.2 Channels

Channel id 0–63. Each channel keeps **independent** counters for each ordered/sequenced method, so a loss on one
channel never blocks another (no head-of-line blocking across channels). The number of channels in use is
negotiated in the ticket (default 8, max 64).

### 9.3 MESSAGE frame

```
u8      Channel (0..63)
u8      Info      bits 2..0 DeliveryMethod | bit 3 Fragmented | bits 7..4 reserved (0)
varint  ReliableIndex       (reliable methods)   per-session, dedup on the receiver
varint  OrderIndex          (ordered/sequenced)  per channel and method
varint  FragmentGroupId     (if Fragmented)      per session
varint  FragmentIndex       (if Fragmented)
varint  FragmentCount       (if Fragmented)
varint  Length
bytes   Payload
```

The receiver deduplicates reliable messages with a window over `ReliableIndex` and buffers out-of-order
messages of an ordered channel until the gap is filled.

### 9.4 Fragmentation

A message larger than the space left in one datagram is split into fragments of one group. Fragments carry the
message's delivery method. Limits (negotiated, defaults): message ≤ 1 MiB, fragments per message ≤ 1024,
concurrent incomplete groups ≤ 64 per session, incomplete group lifetime 10 s. **Unreliable** fragmented
messages are dropped if any fragment is lost; they are limited to 16 fragments by default.

### 9.5 Flow control and memory limits

Each side **advertises** its receive budgets, first in the ticket and later with `LIMITS` frames: total bytes it
will buffer for out-of-order and reassembly data (default 4 MiB) and per-channel bytes. A sender must keep its
unacknowledged reliable bytes per channel and in total within the peer's advertised budgets (in addition to the
congestion window). A receiver closes with `FLOW_CONTROL_ERROR` only when a peer exceeds what was advertised, so
an honest sender is never closed because of loss. Per channel send queues are bounded; a reliable send on a full queue
fails fast (or waits, configurable) instead of growing memory.

### 9.6 Priorities

Send scheduling uses the existing `PacketPriority`: higher priority queues are drained first within the
congestion window; ACK and PING frames always go first.

## 10. Security considerations

1. **No response to unauthenticated datagrams.** The server only sends to an address after it received a valid
   AEAD datagram for that session from it. Invalid datagrams cost one table lookup plus one AEAD open.
2. **Amplification.** On a new, unvalidated path the server sends at most 3× the bytes received on it until
   `PATH_RESPONSE` arrives (§10, item 3).
3. **Migration / NAT rebinding.** A valid datagram with the highest packet number so far from a new address makes
   the server send `PATH_CHALLENGE` there. The new path is adopted after a correct `PATH_RESPONSE`; the old path
   stays usable meanwhile. This replaces the current endpoint pinning to the TCP peer address.
4. **Replay.** AEAD plus the packet-number window. Packet numbers never repeat under one key.
5. **Unpredictable identifiers.** ConnectionId and TicketNonce come from the CSPRNG.
6. **Per-source rate limiting.** Decryption failures are rate limited per source IP. Whether `ConnectionGuard` can be
   reused as is or needs a datagram-specific counter is to be checked during implementation.
7. **Linkability.** Without header protection an on-path observer sees ConnectionId and packet numbers. Accepted
   for v1; ConnectionId rotation and header protection are listed in §12.

## 11. Integration with Nalix sessions (open)

Everything above the transport (dispatch, `PacketContext`, permissions, `ConnectionHub`, `RequestAsync`) is keyed on
`IConnection`. With `Connection` TCP-only, the datagram session has to fit in somewhere. Options, **not decided**:

- **A. The datagram session implements `IConnection`** (transport = NDP). Least change above the transport; handlers
  work unchanged. Identity and permission level are copied from the stream connection when the ticket is issued.
- **B. A logical session object** that outlives its transports and owns identity/permissions; stream and datagram
  sessions attach to it. Cleaner long term, larger change.
- **C. A new datagram-only abstraction** with its own dispatch. Most isolated, duplicates the dispatch path.

Questions any option must answer:

- Identity and permission level set during the stream handshake must survive the stream closing (snapshot at
  ticket time in A, owned by the logical session in B).
- `RequestAsync` and the 16-bit `SequenceId`: requests/responses map to reliable messages; correlation can keep
  `SequenceId` inside the Nalix frame payload, unchanged.
- Resume: which object a resumed client re-attaches to.

## 12. Where the code goes (dependency rules)

The current `Nalix.Network` must not reference `Nalix.Codec`, which is why today's UDP tag is `XxHash32`
(not a cryptographic MAC). Proposal:

- **`Nalix.Abstractions`**: `DeliveryMethod`, `IDatagramProtection` (seal/open a datagram given header, packet
  number and payload; key update), datagram session contracts.
- **`Nalix.Codec`**: `ChaCha20Poly1305DatagramProtection : IDatagramProtection` + key derivation (§5).
- **New `Nalix.Datagram`** (proposal, needs approval because it changes the dependency graph in CLAUDE.md;
  Level 2: Abstractions, Environment): the protocol engine — frames, ACK, loss
  detection, congestion control, channels, fragmentation. **No sockets and no crypto implementation**, so the
  same engine serves the server (`Nalix.Network`) and the client (`Nalix.SDK`) and can be tested
  deterministically with a simulated clock and network.
- **`Nalix.Network`**: UDP socket listener + session table, using the engine and an injected
  `IDatagramProtection`.
- **`Nalix.Hosting`**: wires the Codec implementation into the listener.
- **`Nalix.SDK`**: `DatagramSession` (client) on top of the engine.

The same `IDatagramProtection` injection is a possible answer to the constraint that keeps `XxHash32` in today's
UDP path. That fix is deferred until this approach is approved.

**Unity / mobile.** Unity cannot consume the `net10.0` packages (static abstract interface members). The engine
in `Nalix.Datagram` should therefore avoid static abstract members and net10-only APIs so it can later be
multi-targeted to `netstandard2.1`. A Unity client additionally needs a portable ChaCha20-Poly1305
(`System.Security.Cryptography.ChaCha20Poly1305` is not available there); `Nalix.Codec.Security`'s managed
implementation is a candidate if it can be built for `netstandard2.1`. To be confirmed before implementation.

## 13. Future versions (reserved, not in v1)

- UDP handshake with a stateless retry cookie (no TCP at all).
- Header protection and ConnectionId rotation.
- CUBIC/BBR congestion controllers.
- 0-RTT resumption.

## 14. Test strategy

- **Network emulator** in tests: loss, reordering, duplication, delay with jitter, bandwidth cap, MTU black hole,
  NAT rebinding. Property tests: every reliable message is delivered exactly once; ordered channels deliver in
  order; sequenced channels never deliver older after newer; memory stays within limits.
- **Known-answer vectors** for key derivation and datagram sealing, so other client implementations can be
  checked byte for byte.
- **Fuzzing** of the header and frame parsers.
- **Benchmarks**: throughput, latency percentiles and CPU per datagram under loss, compared with the current
  UDP path.

## 15. Migration and breaking changes

Major version. Removed: `IConnection.UDP`, `IConnection.IsUdpCreated`, UDP sequence fields in session
snapshots/resume, the UDP branches of `PacketSender`/`ConnectionHub`, the current `UdpListener`.
`[PacketTransport(NetworkTransport.UDP)]` is replaced by an attribute that names a delivery method and channel.
Suggested order of PRs:

1. Decide §11 (session integration) and §12 (new project / dependency graph).
2. `IDatagramProtection` in Abstractions + Codec implementation (and, if approved, use it to replace the `XxHash32`
   tag in the current UDP path).
3. `Nalix.Datagram` engine with the network emulator and property tests (no sockets).
4. Server listener/session table + ticket control packets; SDK `DatagramSession`.
5. Hosting integration and the delivery-method attribute; samples.
6. Remove UDP from `Connection` and the old listener (major version).

## 16. Open questions

1. Session integration: option A, B or C in §11.
2. New `Nalix.Datagram` project and the resulting dependency graph change (§12).
3. Default channel count and limits (§9.2, §9.4, §9.5) — fine as proposed?
4. Should `KeepStream` default to closing the TCP/WebSocket stream (as proposed) or keeping it?
5. Is the 100 ms default expiry for queued unreliable messages right for your games?
6. Unity client: build `Nalix.Datagram` + a managed AEAD for `netstandard2.1`, or a separate client later?

## References

RFC 9000 (QUIC transport), RFC 9001 (QUIC TLS: key update, nonce construction, AEAD limits), RFC 9002 (loss
detection and congestion control), RFC 8899 (DPLPMTUD), RFC 9221 (unreliable datagrams), RFC 8439
(ChaCha20-Poly1305), RFC 5869 (HKDF). RakNet and LiteNetLib for the delivery-method and channel model.
