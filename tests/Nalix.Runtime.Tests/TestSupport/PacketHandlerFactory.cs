// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Threading.Tasks;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Runtime.Dispatching;
using Nalix.Runtime.Internal.Compilation;

namespace Nalix.Runtime.Tests.TestSupport;

internal static class PacketHandlerFactory
{
    public static PacketHandler<TestPacket> CreateDescriptor(
        Func<object?, PacketContext<TestPacket>, ValueTask<object?>> invoker,
        int timeoutMs = 0,
        bool requireEncryption = false)
        => CreateDescriptor<TestPacket>(invoker, timeoutMs, requireEncryption);

    public static PacketHandler<TPacket> CreateDescriptor<TPacket>(
        Func<object?, PacketContext<TPacket>, ValueTask<object?>> invoker,
        int timeoutMs = 0,
        bool requireEncryption = false)
        where TPacket : IPacket
    {
        PacketTimeoutAttribute? timeout = timeoutMs > 0 ? new PacketTimeoutAttribute(timeoutMs) : null;
        PacketEncryptionAttribute? encryption = requireEncryption ? new PacketEncryptionAttribute(true) : null;
        PacketMetadata metadata = new(
            opCode: new PacketOpcodeAttribute((ushort)1),
            timeout: timeout,
            permission: null,
            encryption: encryption,
            rateLimit: null,
            transport: null);

        return new PacketHandler<TPacket>(
            opCode: 1,
            metadata: metadata,
            controllerInstance: null,
            methodName: "FakeHandler",
            returnType: typeof(object),
            compiledInvoker: invoker,
            expectedPacketType: typeof(TPacket));
    }
}
