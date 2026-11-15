// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Security;
using Nalix.Codec.Internal;
using Nalix.Runtime.Dispatching;
using Nalix.Tunneling.Handlers;
using Nalix.Tunneling.Tests.TestSupport;

namespace Nalix.Tunneling.Tests.Handlers;

public sealed class TunnelRequestEncryptionTests
{
    private const byte EncryptedFlag = 1 << 2;

    [Fact]
    public async Task TunnelConnect_WhenTunnelRequestIsForwarded_SendsItEncryptedOnProviderControlConnection()
    {
        // The provider's control sender is the one captured by TUNNEL_PROVIDE; its metadata carries no
        // [PacketEncryption] flag, so the TUNNEL_REQUEST must be encrypted explicitly.
        FakeConnection provider = CreateSessionConnection();
        ProviderRegistry providers = new();
        Assert.True(providers.TryRegister(3, provider));

        _ = RunConnectAsync(providers);

        await Wait.UntilAsync(() => provider.FakeTcp.SentMessages.Count > 0, TimeSpan.FromSeconds(5));
        byte[] frame = provider.FakeTcp.SentMessages[0];
        Assert.Equal((ushort)ProtocolOpCode.TUNNEL_REQUEST, BitConverter.ToUInt16(frame, 0));
        Assert.True((frame[2] & EncryptedFlag) != 0, "TUNNEL_REQUEST must carry the ENCRYPTED flag on the control connection.");
    }

    [Fact]
    public async Task TunnelConnect_WhenProviderHasNoSessionCipher_DoesNotSendTunnelRequestInPlaintext()
    {
        // Without a negotiated cipher the request must be refused, never sent in clear.
        FakeConnection provider = new() { Algorithm = CipherSuiteType.None };
        ProviderRegistry providers = new();
        Assert.True(providers.TryRegister(3, provider));

        _ = RunConnectAsync(providers);
        await Task.Delay(200);

        Assert.Empty(provider.FakeTcp.SentMessages);
    }

    private static FakeConnection CreateSessionConnection()
    {
        Span<byte> secret = stackalloc byte[Bytes32.Size];
        Nalix.Environment.Random.Csprng.Fill(secret);
        return new FakeConnection { Algorithm = CipherSuiteType.Chacha20Poly1305, Secret = new Bytes32(secret) };
    }

    private static Task RunConnectAsync(ProviderRegistry providers)
    {
        ConsumerHandler handler = new(providers, new TunnelRegistry(), new TunnelSessionRegistry(), new TunnelOptions(), NullLogger.Instance);
        return handler.HandleConnectAsync(new FakePacketContext<TunnelConnect>(new TunnelConnect { ChannelId = 3 }, new FakeConnection(), new FakePacketSender())).AsTask();
    }
}
