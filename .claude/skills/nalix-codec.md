# Nalix.Codec

## Triggers
- Defining a new packet or frame type
- Modifying the crypto or transform pipeline
- Changing serialization field layout or attributes
- Debugging wire format or deserialization mismatches

---

## Rules

### Packet Type Definition
- Use CRTP: `sealed class MyPacket : PacketBase<MyPacket>` — the self-type parameter is mandatory for source generator trigger
- `[Packet]` on the class triggers `PacketRegistryGenerator` to emit the static opcode → type mapping
- `[SerializeOrder(n)]` on **every** serializable field, starting from 0, no gaps — generator emits fields in this exact order into the wire format
- Opcode must be **globally unique** across all registered packet types — `PacketRegistry` is a static dict; collision is silent at compile time and produces wrong deserialization at runtime

### Transform Pipeline Order (Immutable)
```
Outbound:  serialize → compress (LZ4) → encrypt (AEAD)
Inbound:   decrypt   → decompress     → deserialize
```
Never swap compress and encrypt. Encrypting before compressing produces high-entropy ciphertext that compresses poorly (near-zero gain). Compressing before encrypting is the correct security model. `FramePipeline` enforces this order — do not reorder stages.

### Cryptography Invariants
- **Never generate nonces outside `EnvelopeCipher`** — nonce management (counter, uniqueness) is internal to the cipher engine
- `HandshakeX25519` derives master secret via HKDF-Extract (no salt) combining two shared secrets: EE (ephemeral-ephemeral = forward secrecy) + SE (static-ephemeral = authentication)
- Both EE and SE must produce non-zero output — zero in either = abort handshake with `DECRYPTION_FAILED`
- Proof labels are static readonly spans: `"nalix-handshake/server-proof"` — changing a label breaks interop with all existing clients immediately
- All intermediate secrets (EE, SE, master) must be `ZeroMemory`'d after use — they are GC-visible until zeroed

### Serialization Entry Points
- Application code: `LiteSerializer.Serialize<T>()` / `LiteSerializer.Deserialize<T>()` only
- `IFormatter<T>` is for generated code only — do not implement it manually
- `IFillableFormatter<T>` is the pool-friendly variant — fills an existing (rented) object instead of allocating

### Generator Activation
`[GenerateFormatter]` requires the containing project to reference `Nalix.Analyzers.Generators` as:
```xml
<ProjectReference ... OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
```
Without this, no code is generated and the serializer fails at runtime with a missing-formatter exception.

---

## Checklists

### Define a new application packet
1. `public sealed class MyPacket : PacketBase<MyPacket>`
2. Add `[SerializeHeader]` on the class
3. Add `[SerializeOrder(0)]`, `[SerializeOrder(1)]`... on each field in wire order
4. Exclude derived/computed fields: `[SerializeIgnore]`
5. Run `dotnet build` — inspect `obj/.../generated/` for the emitted `IFormatter<MyPacket>`
6. Confirm opcode in your enum is unique globally across all `PacketBase<T>` subclasses

### Define a new protocol frame
1. Create under `Nalix.Codec/ProtocolFrames/`
2. Inherit `FrameBase` (or `PacketBase<T>` if pooling is needed)
3. Same `[SerializeOrder]` rules as above
4. If pooled: add to `PacketRegistry` initialization

### Add a new crypto primitive
1. Only add to `Security/Aead/`, `Security/Symmetric/`, or `Security/Hashing/` — never invent a custom scheme
2. Use `EnvelopeCipher` as the integration point, not raw cipher classes
3. Nonce management must go through the cipher engine — no external nonce generation

---

## Gotchas

- **Opcode collision protection**: Duplicate `StaticOpCode` declarations on `[Packet]` classes or `RegisterPacket<T>()` chains are rejected at compile time (`NALIX068` in `PacketRegistryGenerator` and `NALIX106` in `NalixUsageAnalyzer`), and `PacketRegistry` throws `InternalErrorException` at runtime if two distinct packet types register the same opcode.

- **`[SerializeOrder]` gaps produce wire format mismatches**: The generator assigns wire slots by `[SerializeOrder]` value. A sequence like `(0, 1, 3)` leaves an empty slot at position 2. Clients expecting a 4-field packet receive 3 fields — deserialization produces truncated or corrupt data.

- **Generator not attached = silent runtime failure**: If the generator reference is wrong (missing `OutputItemType="Analyzer"`), no formatter is generated. The error only appears at runtime when `FormatterProvider` throws a missing-formatter exception.

- **HKDF label changes break all existing clients**: The handshake proof label is compiled into both client and server. Changing it on the server makes every existing client fail the `SESSION_PROOF` check — they will all get `DECRYPTION_FAILED`.

- **Compressing ciphertext wastes CPU**: If you accidentally reverse the pipeline (encrypt then compress), LZ4 produces near-zero compression on high-entropy ciphertext — you pay compression CPU cost for no benefit.

- **`IFillableFormatter<T>` vs `IFormatter<T>`**: For pooled packet types, always use `IFillableFormatter<T>` to fill a rented object. Using `IFormatter<T>` allocates a new instance, defeating pooling.
