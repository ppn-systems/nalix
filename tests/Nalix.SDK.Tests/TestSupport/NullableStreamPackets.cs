// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using Nalix.Abstractions;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Serialization;
using Nalix.Codec.DataFrames;
using Nalix.Environment.Memory;
using Nalix.SDK.Options;
using Nalix.SDK.Transport;
using Nalix.SDK.Transport.Extensions;

namespace Nalix.SDK.Tests.TestSupport;

[Packet]
[GenerateFormatter]
[SerializePackable(SerializeLayout.Explicit)]
public sealed partial class NullableStreamRequest : PacketBase<NullableStreamRequest>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 0x7A89;
}

[Packet]
[GenerateFormatter]
[SerializePackable(SerializeLayout.Explicit)]
public sealed partial class NullableStreamItem : PacketBase<NullableStreamItem>, IPacketStaticOpcode, IPacketStreamable, IPacketStreamTerminator
{
    public static ushort StaticOpCode => 0x7A8A;
    public static int DisposeCount { get; set; }

    [SerializeOrder(0)]
    public bool IsEndOfStream { get; set; }

    [SerializeOrder(1)]
    public int? Capacity { get; set; }

    [SerializeOrder(2)]
    public TimeSpan? Duration { get; set; }

    [SerializeOrder(3)]
    public bool IsTerminator { get; set; }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeCount++;
        }

        base.Dispose(disposing);
    }
}
