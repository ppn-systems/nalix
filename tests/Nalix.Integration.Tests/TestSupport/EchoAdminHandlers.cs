// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Threading.Tasks;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Security;

namespace Nalix.Integration.Tests.TestSupport;

/// <summary>
/// Server-side handler that only an administrator may invoke. Ordinary clients are denied and must never reach it.
/// </summary>
[PacketHandler("Integration.EndToEnd.Admin")]
public static class EchoAdminHandlers
{
    [PacketPermission(PermissionLevel.SYSTEM_ADMINISTRATOR)]
    [PacketOpcode(EchoAdminPacket.OpCode)]
    public static ValueTask HandleAdminAsync(IPacketContext<EchoAdminPacket> context)
    {
        ArgumentNullException.ThrowIfNull(context);
        throw new InvalidOperationException("Denied handler must not execute.");
    }
}
