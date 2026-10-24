// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;

namespace Nalix.Network.Tests.TestSupport;

[PacketHandler("IntegrationTest2")]
[Nalix.Abstractions.Injection.Injectable]
public sealed class IntegrationTestController2
{
    [PacketOpcode(9999)]
    public HostingScanAttributedPacket Handle(IPacketContext<HostingScanAttributedPacket> context)
    {
        return new HostingScanAttributedPacket { Value = (ushort)(context.Packet.Value + 1) };
    }
}
