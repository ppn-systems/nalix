// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;

namespace Nalix.Network.Tests.TestSupport;

[PacketHandler("IntegrationTest")]
[Nalix.Abstractions.Injection.Injectable]
public sealed class IntegrationTestController
{
    public static int ReceivedCount = 0;

    [PacketOpcode(0x1234)]
    public void Handle(IPacketContext<HostingScanAttributedPacket> context)
    {
        Interlocked.Increment(ref ReceivedCount);
    }
}
