// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Reflection;
using System.Threading;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Runtime.Dispatching;
using Nalix.Runtime.Internal.Compilation;
using Nalix.Runtime.Routing;

namespace Nalix.Tunneling.Tests.TestSupport;

/// <summary>
/// Resolves handlers the way the dispatcher does: the descriptor comes from the source-generated registry
/// and the permission gate is <see cref="PacketHandler{TPacket}.CanExecute"/>.
/// </summary>
internal static class DispatchProbe
{
    public static PacketHandler<IPacket> ResolveHandler<TController>(TController controller, ushort opCode)
        where TController : class
    {
        PacketDispatchOptions<IPacket> options = new PacketDispatchOptions<IPacket>().WithHandler(controller);

        Assert.True(options.TryResolveHandler(opCode, out PacketHandler<IPacket> handler), $"No handler registered for opcode {opCode}.");
        return handler;
    }

    public static bool CanExecute(PacketHandler<IPacket> handler, IPacket packet, IConnection connection, out ProtocolReason reason)
    {
        PacketContext<IPacket> context = CreateContext(packet, connection, handler.Metadata);
        return handler.CanExecute(context, out reason);
    }

    private static PacketContext<IPacket> CreateContext(IPacket packet, IConnection connection, PacketMetadata metadata)
    {
        PacketContext<IPacket> context = new();

        MethodInfo initialize = typeof(PacketContext<IPacket>).GetMethod("Initialize", BindingFlags.Instance | BindingFlags.NonPublic)!;
        initialize.Invoke(context, [packet, connection, metadata, false, false, CancellationToken.None, null]);

        return context;
    }
}
