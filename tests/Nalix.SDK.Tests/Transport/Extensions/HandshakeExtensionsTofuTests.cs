// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Net.Sockets;
using System.Net;
using System.Reflection;
using Nalix.Abstractions;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Security;
using Nalix.Codec.DataFrames;
using Nalix.Codec.ProtocolFrames;
using Nalix.Codec.Security;
using Nalix.Codec.Transforms;
using Nalix.Environment.Hashing;
using Nalix.Environment.Memory;
using Nalix.Environment.Sequencing;
using Nalix.Framework.Injection;
using Nalix.Hosting;
using Nalix.SDK.Options;
using Nalix.SDK.Transport;
using Nalix.SDK.Transport.Extensions;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Nalix.SDK.Tests.TestSupport;

namespace Nalix.SDK.Tests.Transport.Extensions;

[Collection(TestCollections.RealServer)]
public sealed class HandshakeExtensionsTofuTests : IDisposable
{
    private readonly Bytes32 _serverPublicKey;

    public HandshakeExtensionsTofuTests()
    {
        TestAssemblySetup.EnsureHighLimits();
        if (!PacketRegistry.IsBuilt)
            PacketRegistry.Build();
        TestEnvironment.SetupCertificate();
        _serverPublicKey = Bytes32.Parse(TestEnvironment.GetServerPublicKey());
    }

    [Fact]
    public async Task HandshakeAsync_WhenAllowTofuFalseAndNoServerPublicKey_ThrowsNetworkException()
    {
        int port = TestEnvironment.GetFreePort();
        var builder = NetworkApplication.CreateBuilder();
        builder.ListenTcp<IntegrationTestProtocol>().OnPort((ushort)port);
        builder.UseSecureConnections();
        builder.UseSystemControl();

        using NetworkApplication app = builder.Build();
        await app.ActivateAsync();

        try
        {
            TransportOptions options = new()
            {
                Address = "127.0.0.1",
                Port = (ushort)port,
                ServerPublicKey = string.Empty
            };
            PropertyInfo? allowTofuProp = typeof(TransportOptions).GetProperty("AllowTofu");
            Assert.NotNull(allowTofuProp);
            allowTofuProp!.SetValue(options, false);

            using TcpSession session = new(options);
            await session.ConnectAsync();

            await Assert.ThrowsAsync<Nalix.Abstractions.Exceptions.NetworkException>(() => session.HandshakeAsync().AsTask());
        }
        finally
        {
            await app.DeactivateAsync();
        }
    }

    public void Dispose() => InstanceManager.Instance.Clear(dispose: false);
}
