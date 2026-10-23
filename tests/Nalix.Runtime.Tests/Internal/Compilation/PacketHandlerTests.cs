// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Reflection;
using System.Threading.Tasks;
using FluentAssertions;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Security;
using Nalix.Runtime.Dispatching;
using Nalix.Runtime.Internal.Compilation;
using Nalix.Runtime.Tests.TestSupport;
using Xunit;
using Nalix.Abstractions.Primitives;

namespace Nalix.Runtime.Tests.Internal.Compilation;

public sealed class PacketHandlerTests
{
    [Fact]
    public void CanExecute_WithStrictMatchPermissionAndHigherLevel_DeniesWithUnauthorized()
    {
        PacketMetadata metadata = new(
            opCode: new PacketOpcodeAttribute(1),
            timeout: null,
            permission: new PacketPermissionAttribute(PermissionLevel.GUEST, PermissionEvaluation.StrictMatch),
            encryption: null,
            rateLimit: null,
            transport: null);

        object descriptor = CreatePacketHandlerDescriptor(metadata);
        MethodInfo canExecuteMethod = descriptor.GetType().GetMethod("CanExecute", BindingFlags.Instance | BindingFlags.Public)!;

        using FakeConnection connection = new()
        {
            Level = PermissionLevel.USER // Higher than GUEST, which violates StrictMatch
        };

        using PacketContext<DummyPacket> context = PacketContextFactory.Create(new DummyPacket(), connection, metadata: metadata);

        object?[] args = [context, ProtocolReason.NONE];
        bool canExecute = (bool)canExecuteMethod.Invoke(descriptor, args)!;
        ProtocolReason denyReason = (ProtocolReason)args[1]!;

        canExecute.Should().BeFalse("PermissionEvaluation.StrictMatch must reject connections whose level does not exactly match");
        denyReason.Should().Be(ProtocolReason.UNAUTHORIZED, "permission denial in CanExecute must report ProtocolReason.UNAUTHORIZED consistent with PermissionMiddleware");
    }

    /// <summary>
    /// A handler marked [PacketEncryption(true)] must be denied with ProtocolReason.FORBIDDEN
    /// when the packet context reports the frame did not arrive encrypted on the wire —
    /// EncryptedOnWire is the dedicated audit signal CanExecute reads (the packet header's
    /// transient ENCRYPTED flag is cleared by the inbound cipher transforms and cannot be
    /// used for this check).
    /// </summary>
    [Fact]
    public void CanExecute_WithEncryptionRequiredAndFrameNotEncryptedOnWire_DeniesWithForbidden()
    {
        PacketHandler<TestPacket> descriptor = PacketHandlerFactory.CreateDescriptor(
            (_, _) => new ValueTask<object?>((object?)null), requireEncryption: true);

        PacketContext<TestPacket> context = new();
        FakeConnection connection = new();
        try
        {
            context.Initialize(
                new TestPacket(), connection, descriptor.Metadata,
                reliable: true, encryptedOnWire: false);

            bool canExecute = descriptor.CanExecute(context, out ProtocolReason denyReason);

            canExecute.Should().BeFalse();
            denyReason.Should().Be(ProtocolReason.FORBIDDEN);
        }
        finally
        {
            context.Return();
            connection.Dispose();
        }
    }

    /// <summary>
    /// A handler marked [PacketEncryption(true)] must be allowed to execute when the packet
    /// context reports the frame did arrive encrypted on the wire.
    /// </summary>
    [Fact]
    public void CanExecute_WithEncryptionRequiredAndFrameEncryptedOnWire_Allows()
    {
        PacketHandler<TestPacket> descriptor = PacketHandlerFactory.CreateDescriptor(
            (_, _) => new ValueTask<object?>((object?)null), requireEncryption: true);

        PacketContext<TestPacket> context = new();
        FakeConnection connection = new();
        try
        {
            context.Initialize(
                new TestPacket(), connection, descriptor.Metadata,
                reliable: true, encryptedOnWire: true);

            bool canExecute = descriptor.CanExecute(context, out ProtocolReason denyReason);

            canExecute.Should().BeTrue();
            denyReason.Should().Be(ProtocolReason.NONE);
        }
        finally
        {
            context.Return();
            connection.Dispose();
        }
    }

    private static object CreatePacketHandlerDescriptor(PacketMetadata metadata)
    {
        Type handlerType = typeof(PacketDispatchChannel).Assembly
            .GetType("Nalix.Runtime.Internal.Compilation.PacketHandler`1")!
            .MakeGenericType(typeof(DummyPacket));

        Func<object?, PacketContext<DummyPacket>, ValueTask<object?>> invoker = static (_, _) => new ValueTask<object?>((object?)null);

        ConstructorInfo ctor = handlerType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)[0];
        return ctor.Invoke([metadata.PacketOpcode.OpCode, metadata, null, "FakeHandler", typeof(object), invoker, typeof(DummyPacket)]);
    }

    private sealed class DummyPacket : IPacket
    {
        public int Length => PacketHeader.Size;

        public PacketHeader Header { get; set; }

        public byte[] Serialize() => new byte[PacketHeader.Size];

        public int Serialize(Span<byte> buffer) => PacketHeader.Size;
    }
}
