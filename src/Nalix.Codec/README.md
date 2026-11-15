# Nalix.Codec

Serialization, packet framing, compression, and cryptographic transform primitives for Nalix.

Nalix.Codec owns the data representation layer: source-generated serialization, packet/frame
models, LZ4 compression, AEAD envelope ciphers, and the transform pipeline used by the runtime and
SDK.

## Install

```bash
dotnet add package Nalix.Codec
```

## What It Provides

| Area | Purpose | Main types |
| :--- | :--- | :--- |
| Packet model | Packet base types and runtime packet metadata | `PacketBase<TPacket>`, `FrameBase`, `PacketRegistry`, `PacketSchema` |
| Protocol frames | Built-in control, directive, session handshake, proof-of-work, time sync, and resume frames | `Control`, `Directive`, `SessionInit`, `SessionChallenge`, `SessionResume`, `ProofOfWorkChallenge`, `TimeSync` |
| Serialization | Source-generated binary serialization | `LiteSerializer`, `FormatterProvider`, `IFormatter<T>`, `IFillableFormatter<T>` |
| Transform pipeline | Ordered compression and encryption stages | `FrameTransformer`, `FramePipeline`, `FrameCompression`, `FrameCipher` |
| Cryptography | X25519 handshakes, envelope ciphers, hashes, and MACs | `HandshakeX25519`, `EnvelopeCipher`, `Keccak256`, `HmacKeccak256`, `Poly1305` |
| Compression | Pool-backed LZ4 block and stream compression | `LZ4BlockEncoder`, `LZ4Codec`, `LZ4Encoder`, `LZ4Decoder` |
| Pooling | Scoped packet allocation and object lifecycle helpers | `PacketScope`, `PacketFactory` |

## Minimal Packet

```csharp
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Serialization;
using Nalix.Codec.DataFrames;

[Packet]
[GenerateFormatter]
[SerializePackable(SerializeLayout.Explicit)]
public sealed partial class ChatMessage : PacketBase<ChatMessage>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 201;

    [SerializeOrder(0)]
    public string Text { get; set; } = string.Empty;
}
```

## Serialization

```csharp
byte[] encoded = LiteSerializer.Serialize(new ChatMessage { Text = "hello" });
ChatMessage decoded = LiteSerializer.Deserialize<ChatMessage>(encoded, out int bytesRead);
```

## Design Notes

- Hot paths use spans and pooled buffers.
- Packet serialization is source generated instead of reflection based.
- Cryptographic primitives live in `Nalix.Codec.Security`; do not replace them with application code.

## Documentation

- Package guide: https://ppn.io.vn/packages/nalix-codec/
- API reference: https://ppn.io.vn/api/codec/
