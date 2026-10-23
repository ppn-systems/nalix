// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Codec.DataFrames;
using Nalix.Runtime.Routing;
using Nalix.Abstractions.Primitives;

namespace Nalix.Runtime.Tests.TestSupport;

/// <summary>
/// Test-only packet handled by <see cref="EchoController"/>, real generator output flows through it.
/// </summary>
public sealed partial class EchoPacket : PacketBase<EchoPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 9998;

    public static new EchoPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<EchoPacket>.Deserialize(buffer);
}
