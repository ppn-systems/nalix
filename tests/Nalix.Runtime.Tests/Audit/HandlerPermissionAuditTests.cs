// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using FluentAssertions;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Security;
using Nalix.Codec.ProtocolFrames;
using Nalix.Runtime.Internal.Compilation;
using Nalix.Runtime.Tests.TestSupport;
using Xunit;

namespace Nalix.Runtime.Tests.Audit;

/// <summary>
/// Reproduction for handler permission fail-open audit finding (RT-04).
/// </summary>
[Trait("Category", "Audit")]
public sealed class HandlerPermissionAuditTests
{
    /// <summary>
    /// RT-04: <c>PacketHandler.CanExecute</c> enforces permission only when <c>Metadata.Permission</c> is non-null.
    /// The generator emits <c>permission: null</c> when a handler method omits <c>[PacketPermission]</c>, so such a
    /// handler is executed by the dispatcher gate for a connection at <see cref="PermissionLevel.NONE"/>. The gate
    /// comment (SEC-77) describes a fail-closed baseline, and <c>PermissionMiddleware</c> (SEC-37) denies the same
    /// case, so the dispatcher gate is inconsistent with both. Expected: the handler is denied.
    /// </summary>
    [Fact]
    public void RT04_HandlerWithoutPermissionAttribute_IsDeniedByDispatcherGate()
    {
        PacketMetadata metadata = new(
            new PacketOpcodeAttribute(0x0001),
            timeout: null,
            permission: null,
            encryption: null,
            rateLimit: null,
            transport: null);

        PacketHandler<SessionResume> handler = new(
            0x0001, metadata, null, "NoPermissionHandler", typeof(void),
            (_, _) => default, null);

        FakeConnection connection = new() { Level = PermissionLevel.NONE };
        var context = PacketContextFactory.Create(new SessionResume(), connection);

        bool allowed = handler.CanExecute(context, out _);

        allowed.Should().BeFalse(
            "a handler without an explicit permission policy must fail closed at the dispatcher gate");
    }
}
