using System;
using System.Threading.Tasks;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Security;
using Nalix.Codec.DataFrames;
using Nalix.Hosting;
using Nalix.SDK.Options;
using Nalix.SDK.Transport;
using Nalix.SDK.Transport.Extensions;
using Nalix.Framework.Injection;
using Nalix.Codec.ProtocolFrames;
using Nalix.Codec.Pooling;
using Xunit;

namespace Nalix.SDK.Tests;

[Collection(TestCollections.RealServer)]
public sealed class CipherExtensionsTests : IDisposable
{
    public CipherExtensionsTests()
    {
        TestAssemblySetup.EnsureHighLimits();
        if (!PacketRegistry.IsBuilt)
        PacketRegistry.Build();
        TestUtils.SetupCertificate();
    }

    [Fact]
    public async Task UpdateCipherAsync_WhenSuccessful_SwitchesAlgorithm()
    {
        int port = TestUtils.GetFreePort();
        var builder = NetworkApplication.CreateBuilder();
        builder.ListenTcp<IntegrationTestProtocol>().OnPort((ushort)port);
        builder.UseSecureConnections();
        builder.UseSystemControl();
        builder.UseTimeSync();

        using NetworkApplication app = builder.Build();
        await app.ActivateAsync();

        try
        {
            var options = new TransportOptions
            {
                Address = "127.0.0.1",
                Port = (ushort)port,
                Algorithm = CipherSuiteType.Chacha20Poly1305,
                ServerPublicKey = TestUtils.GetServerPublicKey()
            };

            using var session = new TcpSession(options);
            await session.ConnectAsync();
#pragma warning disable CS0612
            await session.HandshakeAsync();
            await session.UpdateCipherAsync(CipherSuiteType.Salsa20Poly1305, timeoutMs: 20_000);
#pragma warning restore CS0612

            Assert.Equal(CipherSuiteType.Salsa20Poly1305, session.Options.Algorithm);

            // Send a ping to verify Salsa20 works for subsequent packets
            double rtt = await session.PingAsync(timeoutMs: 5000);
            Assert.True(rtt >= 0);
        }
        finally
        {
            await app.DeactivateAsync();
        }
    }

    public void Dispose() => InstanceManager.Instance.Clear(dispose: false);
}

