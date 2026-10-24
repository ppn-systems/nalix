// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using FluentAssertions;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Security;
using Nalix.Codec.ProtocolFrames;
using Nalix.Runtime.Dispatching;
using Nalix.Runtime.Handlers;
using Nalix.Runtime.Tests.TestSupport;
using Xunit;

namespace Nalix.Runtime.Tests.Handlers;

[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
public sealed class SystemControlHandlersTests
{
    [Fact]
    public async Task HandleAsync_WithNullContext_ThrowsArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await SystemControlHandlers.HandleAsync(null!).AsTask());
    }

    [Fact]
    public async Task HandleAsync_WithUnencryptedCipherUpdate_DisconnectsWithoutUpdatingAlgorithm()
    {
        using FakeConnection connection = new()
        {
            Algorithm = CipherSuiteType.Chacha20Poly1305,
            Level = PermissionLevel.ESTABLISHED
        };
        ConnectionRuntimeStateAccessor.SetHandshakeEstablished(connection, true);

        Control packet = new();
        packet.Initialize(ControlType.CIPHER_UPDATE, sequenceId: 1, reasonCode: (ProtocolReason)CipherSuiteType.Salsa20Poly1305);

        PacketContext<Control> context = PacketContextFactory.Create(packet, connection, reliable: true, encryptedOnWire: false);
        try
        {
            await SystemControlHandlers.HandleAsync(context);

            connection.Disconnected.Should().BeTrue("plaintext CIPHER_UPDATE must be rejected and disconnect the connection");
            connection.Algorithm.Should().Be(CipherSuiteType.Chacha20Poly1305, "algorithm must not be mutated by unencrypted CIPHER_UPDATE");
        }
        finally
        {
            context.Dispose();
        }
    }

    [Fact]
    public async Task HandleAsync_WithCipherUpdateToNoneSuite_DisconnectsWithoutUpdatingAlgorithm()
    {
        using FakeConnection connection = new()
        {
            Algorithm = CipherSuiteType.Chacha20Poly1305,
            Level = PermissionLevel.ESTABLISHED
        };
        ConnectionRuntimeStateAccessor.SetHandshakeEstablished(connection, true);

        Control packet = new();
        packet.Initialize(ControlType.CIPHER_UPDATE, sequenceId: 1, reasonCode: (ProtocolReason)CipherSuiteType.None);

        PacketContext<Control> context = PacketContextFactory.Create(packet, connection, reliable: true, encryptedOnWire: true);
        try
        {
            await SystemControlHandlers.HandleAsync(context);

            connection.Disconnected.Should().BeTrue("downgrade to CipherSuiteType.None must disconnect the connection");
            connection.Algorithm.Should().Be(CipherSuiteType.Chacha20Poly1305, "algorithm must not be downgraded to None");
        }
        finally
        {
            context.Dispose();
        }
    }

    [Fact]
    public async Task HandleAsync_WithUnencryptedSessionRekey_DisconnectsWithoutSendingKeyAgreement()
    {
        using FakeConnection connection = new()
        {
            Algorithm = CipherSuiteType.Chacha20Poly1305,
            Level = PermissionLevel.ESTABLISHED
        };
        ConnectionRuntimeStateAccessor.SetHandshakeEstablished(connection, true);

        Control packet = new();
        packet.Initialize(ControlType.SESSION_REKEY, sequenceId: 1);

        PacketContext<Control> context = PacketContextFactory.Create(packet, connection, reliable: true, encryptedOnWire: false);
        try
        {
            await SystemControlHandlers.HandleAsync(context);

            connection.Disconnected.Should().BeTrue("unencrypted SESSION_REKEY must disconnect the connection");
            connection.FakeTcp.SentMessages.Should().BeEmpty("unencrypted SESSION_REKEY must not trigger key agreement");
        }
        finally
        {
            context.Dispose();
        }
    }

    [Fact]
    public async Task HandleAsync_WithRapidDuplicatePowAndPublicKeyRequests_RateLimitsDuplicatesViaDirectiveGuard()
    {
        using FakeConnection connection = new();

        Control powReq = new();
        powReq.Initialize(ControlType.POW_REQUEST, sequenceId: 1);

        using (PacketContext<Control> ctx1 = PacketContextFactory.Create(powReq, connection, reliable: true, encryptedOnWire: false))
        using (PacketContext<Control> ctx2 = PacketContextFactory.Create(powReq, connection, reliable: true, encryptedOnWire: false))
        {
            await SystemControlHandlers.HandleAsync(ctx1);
            await SystemControlHandlers.HandleAsync(ctx2);
        }

        connection.FakeTcp.SentMessages.Should().HaveCount(1, "rapid duplicate POW_REQUEST on the same connection must be rate-limited");

        Control pubKeyReq = new();
        pubKeyReq.Initialize(ControlType.PUBLIC_KEY_REQUEST, sequenceId: 2);

        using (PacketContext<Control> ctx3 = PacketContextFactory.Create(pubKeyReq, connection, reliable: true, encryptedOnWire: false))
        using (PacketContext<Control> ctx4 = PacketContextFactory.Create(pubKeyReq, connection, reliable: true, encryptedOnWire: false))
        {
            await SystemControlHandlers.HandleAsync(ctx3);
            await SystemControlHandlers.HandleAsync(ctx4);
        }

        connection.FakeTcp.SentMessages.Should().HaveCount(2,
            "first PUBLIC_KEY_REQUEST must succeed after POW_REQUEST, while rapid duplicate PUBLIC_KEY_REQUEST must be rate-limited");
    }
}
