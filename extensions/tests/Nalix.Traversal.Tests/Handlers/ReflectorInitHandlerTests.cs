// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Security;
using Nalix.Runtime.Internal.Compilation;
using Nalix.Traversal.Handlers;
using Nalix.Traversal.Packets;
using Nalix.Traversal.Tests.TestSupport;

namespace Nalix.Traversal.Tests.Handlers;

public sealed class ReflectorInitHandlerTests
{
    [Theory]
    [InlineData(PermissionLevel.NONE)]
    [InlineData(PermissionLevel.GUEST)]
    public void ReflectorInit_WhenLevelBelowUser_ReturnsUnauthorized(PermissionLevel level)
    {
        PacketHandler<IPacket> handler = Resolve();

        bool allowed = DispatchProbe.CanExecute(handler, new ReflectorInit(), new FakeConnection { Level = level }, out ProtocolReason reason);

        Assert.False(allowed);
        Assert.Equal(ProtocolReason.UNAUTHORIZED, reason);
    }

    [Fact]
    public void ReflectorInit_WhenLevelIsUser_IsAllowed()
    {
        PacketHandler<IPacket> handler = Resolve();

        bool allowed = DispatchProbe.CanExecute(handler, new ReflectorInit(), new FakeConnection { Level = PermissionLevel.USER }, out _);

        Assert.True(allowed);
    }

    private static PacketHandler<IPacket> Resolve() =>
        DispatchProbe.ResolveHandler(new ReflectorInitHandler(null!), (ushort)ProtocolOpCode.TRAVERSAL_REFLECTOR_INIT);
}
