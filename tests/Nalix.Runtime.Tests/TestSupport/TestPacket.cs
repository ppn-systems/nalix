// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Primitives;

namespace Nalix.Runtime.Tests.TestSupport;

internal sealed class TestPacket : IPacket
{
    public int Length => 0;

    public PacketHeader Header { get; set; }

    public byte[] Serialize() => [];

    public int Serialize(Span<byte> buffer) => 0;
}
