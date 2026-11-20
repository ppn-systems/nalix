# Removing the current UDP path — research and plan

> Status: **research only, no code**. Companion to [nalix-datagram-protocol.md](nalix-datagram-protocol.md).
> Goal: `IConnection` has **one** transport; the current UDP companion transport, its listener and the SDK
> `UdpSession` are removed so NDP can be built cleanly on top of `IConnection`.

## 1. Decisions taken (from review of the NDP draft)

| Topic | Decision |
| :--- | :--- |
| Session integration | The NDP datagram session **implements `IConnection`** (option A). |
| Order of work | First make `IConnection` single-transport and remove today's UDP, then build NDP. |
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

## 3. Target shape of `IConnection` (single transport)

- `ITransport TCP` → `ITransport Transport` (the only transport; for NDP it is the datagram transport).
- Removed: `ITransport? UDP`, `bool IsUdpCreated`.
- `NetworkTransport` becomes a description of the connection kind (`TCP`, `WEBSOCKET`, later `DATAGRAM`), not a
  per-send choice. The `NetworkTransport transport` parameters on broadcast/send APIs are removed: a message always
  goes over the connection's own transport.
- `IsReliable` on `IPacketContext`/`IBufferLease` is always true after the removal; it is removed now and replaced by
  the NDP delivery method later (send options carry `DeliveryMethod` + channel; on stream transports every method is
  delivered reliably and in order).
- `PacketTransportAttribute` is removed now; NDP adds a delivery attribute (`[PacketDelivery(DeliveryMethod, Channel)]`).
- `ConnectionSequenceState` keeps one send/receive pair.

Renaming `TCP` → `Transport` touches about 52 call sites in src and extensions plus the tests. An `[Obsolete]` `TCP`
alias is not kept (major version, and the name would be wrong for datagram connections).

## 4. Traversal / Reflector (decision needed)

The Reflector relays raw, opaque datagrams between peers that are hole punching; it is not a Nalix session. Options:

- **A. Move the relay into `Nalix.Traversal` itself** with its own `Socket` (a small receive loop + token table, the same
  logic `ReflectorProtocol` has today). Nalix core then has no raw-UDP concept at all. **Recommended.**
- **B. Keep a minimal raw-datagram listener in Nalix.Network** for passthrough only. Keeps a second UDP code path in
  core that is not NDP.
- **C. Rebuild the relay on NDP connections.** Not a fit: hole-punch probes must be raw datagrams, not NDP sessions.

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

1. **Single-transport `IConnection`** (breaking): `TCP` → `Transport`; remove `UDP`, `IsUdpCreated`, `IsReliable`,
   `PacketTransportAttribute`, `NetworkTransport` send parameters; update Runtime/Hosting/SDK, tests and samples.
   The current UDP listener is removed in the same PR, because it cannot work without `IConnection.UDP`.
2. **Traversal relay** moved into `Nalix.Traversal` (option A of §4) — must land together with or before PR 1, so the
   extension keeps working.
3. **Remove the rest of the UDP code**: `PassthroughConnection`, `OperatingMode`, `DatagramGuard`, `SequenceOptions.UdpWindow`,
   `TaskNaming.Tags.Udp`, SDK `UdpSession` and its tests, `SecureMultiTransportHelloWorld` UDP parts.
4. Then the NDP PRs from the NDP draft (§15 there): contracts + Codec protection, Environment engine with the network
   emulator, Network listener + `DatagramConnection`, SDK `DatagramSession`, Hosting wiring, samples.

Between PR 1–3 and the end of the NDP work, Nalix has **no UDP at all**. That is acceptable only if no release is cut in
between, or if the release notes say so; otherwise keep the work on a long-lived branch.

## 7. Open questions

1. Traversal relay: option A (recommended), B or C?
2. Is it acceptable that there is no UDP between the removal PRs and the NDP PRs (release gap)?
3. `IsReliable`: remove now (proposed) or keep until NDP adds `DeliveryMethod`?
4. Reuse Environment `Fragments` for NDP reassembly, or NDP-specific fragment fields?
