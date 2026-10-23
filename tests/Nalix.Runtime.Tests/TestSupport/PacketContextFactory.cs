// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Reflection;
using System.Threading;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Runtime.Dispatching;

namespace Nalix.Runtime.Tests.TestSupport;

internal static class PacketContextFactory
{
    public static PacketContext<TPacket> Create<TPacket>(
        TPacket packet,
        IConnection connection,
        bool reliable = true,
        bool encryptedOnWire = false,
        PacketMetadata? metadata = null)
        where TPacket : IPacket
    {
        PacketContext<TPacket> context = new();
        PacketMetadata desc = metadata ?? new PacketMetadata(
            new PacketOpcodeAttribute(packet.Header.OpCode),
            timeout: null,
            permission: null,
            encryption: null,
            rateLimit: null,
            transport: null);

        MethodInfo initMethod = typeof(PacketContext<TPacket>).GetMethod(
            "Initialize",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        initMethod.Invoke(context, [packet, connection, desc, reliable, encryptedOnWire, false, CancellationToken.None, null]);
        return context;
    }
}
