#if DEBUG
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Nalix.Abstractions.Exceptions;
using Nalix.Codec.DataFrames;
using Nalix.Hosting;
using Nalix.SDK.Options;
using Nalix.SDK.Transport;
using Nalix.SDK.Transport.Extensions;
using Xunit;

namespace Nalix.SDK.Tests;

/// <summary>
/// Covers connect-time failure modes for TCP/UDP/WebSocket transports: no listener,
/// listener closed mid-handshake, and mismatched keys — all must fail cleanly and
/// quickly, never hang or leak the underlying socket.
/// </summary>
[Collection("RealServerTests")]
public sealed class ConnectFailureResilienceTests : IDisposable
{
    public ConnectFailureResilienceTests()
    {
        TestAssemblySetup.EnsureHighLimits();
        if (!PacketRegistry.IsBuilt)
        {
            PacketRegistry.Build();
        }
        TestUtils.SetupCertificate();
    }

    [Fact]
    public async Task ConnectAsync_Tcp_NoListener_FailsCleanlyWithinTimeout()
    {
        int port = TestUtils.GetFreePort(); // Guaranteed nobody listens right after this call.

        using TcpSession session = new(new TransportOptions
        {
            Address = "127.0.0.1",
            Port = (ushort)port,
            ConnectTimeoutMillis = 2000
        });

        DateTime start = DateTime.UtcNow;
        _ = await Assert.ThrowsAsync<NetworkException>(() => session.ConnectAsync());
        TimeSpan elapsed = DateTime.UtcNow - start;

        Assert.True(elapsed < TimeSpan.FromSeconds(5), $"ConnectAsync took too long: {elapsed}");
        Assert.False(session.IsConnected);
    }

    [Fact]
    public async Task ConnectAsync_Tcp_ListenerClosedBeforeHandshake_FailsCleanly()
    {
        int port = TestUtils.GetFreePort();
        TcpListener listener = new(IPAddress.Loopback, port);
        listener.Start();

        try
        {
            using TcpSession session = new(new TransportOptions
            {
                Address = "127.0.0.1",
                Port = (ushort)port,
                ConnectTimeoutMillis = 2000
            });

            await session.ConnectAsync();
            Assert.True(session.IsConnected);

            // Close the listener/underlying accepted socket before handshake completes.
            listener.Stop();

            await Assert.ThrowsAnyAsync<Exception>(() => session.HandshakeAsync().AsTask());
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task HandshakeAsync_MismatchedServerPublicKey_FailsCleanlyNotHang()
    {
        int port = TestUtils.GetFreePort();
        var builder = NetworkApplication.CreateBuilder();
        builder.ListenTcp<IntegrationTestProtocol>().OnPort((ushort)port);
        builder.UseSecureConnections();
        builder.UseSystemControl();

        using NetworkApplication app = builder.Build();
        await app.ActivateAsync();

        try
        {
            // Deliberately wrong 32-byte public key -> AEAD/derivation must fail, not hang.
            string wrongKey = new('7', 64);

            using TcpSession session = new(new TransportOptions
            {
                Address = "127.0.0.1",
                Port = (ushort)port,
                ServerPublicKey = wrongKey,
                ConnectTimeoutMillis = 5000
            });

            await session.ConnectAsync();

            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
            await Assert.ThrowsAnyAsync<Exception>(() => session.HandshakeAsync(cts.Token).AsTask());
        }
        finally
        {
            await app.DeactivateAsync();
        }
    }

    /// <summary>
    /// #393: a WebSocket connect attempt that never reaches <c>OnConnected</c> (no listener, refused
    /// immediately) must not raise <c>OnDisconnected</c> either — an app has no matching "connected"
    /// event to pair it with, so treating a failed connect as a disconnect makes "one OnDisconnected
    /// per real transport close" not a guarantee callers can build on (every failed retry during an
    /// outage would raise one).
    /// </summary>
    [Fact]
    public async Task ConnectAsync_WebSocket_NoListener_DoesNotRaiseOnDisconnected()
    {
        int port = TestUtils.GetFreePort();

        using WebSocketSession session = new(new TransportOptions
        {
            Address = "127.0.0.1",
            Port = (ushort)port,
            ConnectTimeoutMillis = 2000
        });

        int disconnectedCount = 0;
        int connectedCount = 0;
        session.OnDisconnected += (_, _) => disconnectedCount++;
        session.OnConnected += (_, _) => connectedCount++;

        _ = await Assert.ThrowsAsync<NetworkException>(() => session.ConnectAsync());

        Assert.Equal(0, connectedCount);
        Assert.Equal(0, disconnectedCount);
        Assert.False(session.IsConnected);
    }

    public void Dispose() => Nalix.Framework.Injection.InstanceManager.Instance.Clear(dispose: false);
}
#endif
