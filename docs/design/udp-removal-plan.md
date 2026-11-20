# Replacing the current UDP path — research and plan

> Status: **research only, no code**. Companion to [nalix-datagram-protocol.md](nalix-datagram-protocol.md).
> Strategy: **build NDP alongside the current TCP + UDP**, then remove the old UDP in a major version.
> There is no period without UDP.

## 1. Decisions taken (from review of the NDP draft)

| Topic | Decision |
| :--- | :--- |
| Session integration | The NDP datagram session **implements `IConnection`** (option A). |
| Order of work | Keep TCP + today's UDP working; add `IConnection.Transport`, build NDP alongside, remove the old UDP last (major version). |
| Traversal | Only the raw passthrough part (`UdpPassthroughListener` + `PassthroughConnection`) moves into `Nalix.Traversal`. The session-bound UDP companion is not moved; it is deleted when NDP replaces it. |
| New project | **None.** Shared, crypto-free engine code goes into `Nalix.Environment` (next to `Fragments/`); crypto goes into `Nalix.Codec`. |
| Defaults | As proposed in the NDP draft (channels, limits, 100 ms unreliable expiry). |
| Keep the TCP stream after the ticket | An option (`KeepStream`), not a fixed behaviour. |
| Unity / `netstandard2.1` | Out of scope. |

## 2. What exists today

### 2.1 Public API surface that is UDP-specific

| Assembly | Symbols |
| :--- | :--- |
| Abstractions | `IConnection.UDP`, `IConnection.IsUdpCreated`, `NetworkTransport.UDP`, `OperatingMode` (`Passthrough` is UDP-only), `ConnectionSequenceState.UdpSendSequence/UdpReceiveSequence`, `IPacketContext.IsReliable`, `IBufferLease.IsReliable`, `PacketTransportAttribute` |
| Abstractions / Network / Runtime | `NetworkTransport transport` parameters on broadcast/multicast/send APIs (20 PublicAPI entries: `IConnectionBroadcaster`, `ConnectionHub`, `ConnectionExtensions`, `ConnectionUserExtensions`) |
| Environment | `SequenceOptions.UdpWindow` |
| Framework | `TaskNaming.Tags.Udp` |
| Network | `Connection.UDP/IsUdpCreated`, `PassthroughConnection` (whole type), `UdpListenerBase` and `UdpPassthroughListener` (namespace `Nalix.Network.Listeners.Udp`), `DatagramGuardOptions` |
| Hosting | `MapUdp<T>()`, `ListenUdp<T>()` (obsolete), `IProtocolBindingBuilder.WithMode` (operating mode) |
| SDK | `UdpSession`, UDP fields of `TransportOptions`, UDP diagnostics events |

### 2.2 Source files (src)

- **Delete:** `Network/Listeners/UdpListener/*` (6 files), `Network/Internal/Transport/SocketUdpTransport.cs`,
  `Network/Internal/Pooling/PooledUdpReceiveEventArgs.cs`, `Network/Connections/PassthroughConnection.cs`,
  `Network/RateLimiting/Datagram.Guard.cs` + `Options/DatagramGuardOptions.cs`, `Hosting/Internal/UdpServerListener.cs`,
  `SDK/Transport/UdpSession.cs`, `SDK/Transport/Internal/Udp/*` (2 files).
- **Modify:** `Connection.cs`, `Connection.Transport.cs`, `WebSocketConnection.cs`, `ConnectionBacking.cs` (UDP
  transport + replay window), `ConnectionHub.Broadcast.cs`, `Connection.Guard.cs`, `TimingWheelOptions.cs`,
  `NetworkSocketOptions.cs`, `Throw.cs`; Runtime `PacketSender.cs`, `PacketDispatchOptions.Execution.cs`,
  `ConnectionExtensions*.cs`, `SessionHandlers.cs`, `SystemControlHandlers.cs`, `PacketPipeline.cs`,
  `SessionFactory.cs`, `SessionPersistenceObserver.cs`; Hosting `NetworkApplicationBuilder.cs`,
  `INetworkApplicationBuilder.cs`, `IProtocolBindingBuilder.cs`, `HostingBuilderContext.cs`, `DefaultFrameProcessor.cs`,
  `Logging.cs`; SDK `TransportSession.cs`, `SessionState.cs`, `RekeyExtensions.cs`, `TransportOptions.cs`,
  `DiagnosticsEvents.cs`.

### 2.3 Tests, samples, extensions

- **Tests to delete** (UDP-only): `UdpTransportLifecycleTests`, `UdpTransportResumeTests`, `UdpTransportStaleHolderTests`,
  `UdpListenerSocketConfigTests`, `UdpPassthroughListenerTests`, `PassthroughConnectionTests`,
  `PassthroughConnectionBindTests`, `UdpSessionTests`, `UdpSessionIntegrityTests`.
- **Tests to adjust:** about 25 more files touch `UDP`/`IsUdpCreated` through fakes (`FakeConnection` in Runtime, Traversal,
  Tunneling tests), sender/resume/report tests and end-to-end tests.
- **Sample:** `SecureMultiTransportHelloWorld` (server and client use UDP).
- **Extensions — the important dependency:**
  - `Nalix.Traversal` binds a **raw UDP passthrough** listener (`MapUdp<ReflectorProtocol>().WithMode(Passthrough)`) and
    relays datagrams with `targetConnection?.UDP?.Send(buffer)`.
  - `Nalix.SDK.Traversal` uses `System.Net.Sockets.UdpClient` directly for hole punching. It does not depend on
    Nalix UDP code and is unaffected.

## 3. `IConnection` during and after the transition

**Now (non-breaking preparation):**

- Add `ITransport Transport` — the connection's primary transport. `TCP` stays as an `[Obsolete]` alias that returns
  `Transport`.
- `UDP` / `IsUdpCreated` stay unchanged for the old path. A `DatagramConnection` (NDP) returns its datagram transport
  from `Transport`, `null` from `UDP` and `false` from `IsUdpCreated`.
- NDP send options carry `DeliveryMethod` + channel; on stream transports every method is delivered reliably and in order.

**At the major version (old UDP removed):**

- Remove `TCP` (obsolete alias), `UDP`, `IsUdpCreated`, `IsReliable` on `IPacketContext`/`IBufferLease`,
  `PacketTransportAttribute`, the `NetworkTransport transport` parameters on broadcast/send APIs, and the UDP fields
  of `ConnectionSequenceState`. `NetworkTransport` then only describes the connection kind (`TCP`, `WEBSOCKET`, `DATAGRAM`).
- `TCP` → `Transport` touches about 52 call sites in src and extensions plus tests; they can migrate gradually while the
  alias exists.

## 4. Traversal / Reflector (decided)

The Reflector relays raw, opaque datagrams between peers that are hole punching; it is not a Nalix session.
Decision: **move the raw passthrough part into `Nalix.Traversal`** — `UdpPassthroughListener`, `PassthroughConnection`
and what they need (socket configuration, receive loop, token table, `OperatingMode.Passthrough` handling) — as
internal types of the extension. `ReflectorProtocol` stops using `IConnection.UDP` and sends through the moved socket.
Hosting keeps a way to start it (an extension method in `Nalix.Traversal` instead of `MapUdp<T>().WithMode(Passthrough)`).

The session-bound UDP companion (`UdpListener`, `SocketUdpTransport`, the `XxHash32` tag, UDP sequences/resume) is **not**
moved: Traversal does not use it, and it is deleted when NDP replaces it. `Nalix.SDK.Traversal` uses `UdpClient`
directly and is unaffected.

This move can land at any time; it does not depend on NDP.

## 5. Where NDP code goes (no new project)

| Piece | Assembly | Why |
| :--- | :--- | :--- |
| `DeliveryMethod`, `IDatagramProtection` (seal/open/key update), send options | Abstractions | Contracts only. |
| Protocol engine: varint, frames, ACK ranges, loss detection/RTT, NewReno + pacing, channels, ordering/sequencing, reassembly, MTU probing, simulated clock hooks | **Environment** (e.g. `Nalix.Environment.Datagrams`, next to `Fragments/`) | Crypto-free; reachable from Network (via Framework) and SDK (via Codec). |
| `ChaCha20Poly1305DatagramProtection`, public HKDF derivation API, key schedule | **Codec** (`Security/`) | Crypto stays with the existing primitives. |
| UDP socket listener, session table, `DatagramConnection : IConnection`, path validation | Network | Server sockets; receives `IDatagramProtection` by injection. |
| Ticket control packets + handlers, wiring the Codec protection into the listener | Runtime / Hosting | Hosting references both Network and Codec. |
| `DatagramSession` client | SDK | References Codec directly. |

Environment's existing `Fragments` (`FragmentHeader`, `FragmentAssembler`) can be reused for NDP reassembly, or NDP
gets its own varint-based fragment fields as in the draft; to be decided when the engine is written.

The same `IDatagramProtection` injection is how Network gets real AEAD without referencing Codec.

## 6. Proposed PR sequence

1. **`IConnection.Transport`** added; `TCP` becomes an `[Obsolete]` alias. Non-breaking.
2. **Traversal passthrough** moved into `Nalix.Traversal` (§4). Independent of the rest.
3. **NDP contracts + crypto:** `DeliveryMethod`, `IDatagramProtection` (Abstractions); `ChaCha20Poly1305DatagramProtection`,
   public HKDF derivation API, key schedule (Codec).
4. **NDP engine** in Environment with the network emulator, property tests and known-answer vectors (no sockets).
5. **`DatagramConnection : IConnection`** + UDP listener + session table (Network); ticket packets and handlers
   (Runtime); wiring (Hosting); `DatagramSession` (SDK); samples and benchmarks against the old UDP path.
6. **Major version:** remove the old UDP path (§2) and the obsolete `TCP` alias (§3), migrate the remaining tests
   and the `SecureMultiTransportHelloWorld` sample.

## 7. Open questions

1. Reuse Environment `Fragments` for NDP reassembly, or NDP-specific fragment fields as in the draft?
2. Should the old UDP path get the `IDatagramProtection` (real AEAD instead of `XxHash32`) during the transition, or is
   it left as is until it is removed?
