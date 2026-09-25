using System.Net;
using Nalix.Abstractions.Networking;
using Nalix.Environment.Configuration;
using Nalix.Framework.Injection;
using Nalix.Network.Options;
using Nalix.Network.RateLimiting;
using Nalix.SDK.Options;
using Nalix.SDK.Transport;
using Nalix.SDK.Transport.Extensions;

namespace Nalix.Hosting.Tests;

/// <summary>
/// Regression tests for hosting configuration pitfalls: option ordering around
/// UseSecureConnections, the implicit system-control dependency of the handshake,
/// and loopback clients tripping the per-IP connection guard.
/// </summary>
[Collection("RealServerTests")]
public sealed class HostingConfigPitfallTests : IDisposable
{
    private readonly int _savedMaxPerIp;
    private readonly int _savedMaxPerWindow;

    public HostingConfigPitfallTests()
    {
        TestAssemblySetup.EnsureHighLimits();
        ConnectionQuotaOptions quota = ConfigurationManager.Instance.Get<ConnectionQuotaOptions>();
        _savedMaxPerIp = quota.MaxConnectionsPerIpAddress;
        _savedMaxPerWindow = quota.MaxConnectionsPerWindow;

        InstanceManager.Instance.Clear(dispose: false);

        if (!Nalix.Codec.DataFrames.PacketRegistry.IsBuilt)
        {
            Nalix.Codec.DataFrames.PacketRegistry.Build();
        }
    }

    [Fact]
    public async Task ConfigureConnectionQuota_AfterUseSecureConnections_IsHonoured()
    {
        int port = TestUtils.GetFreePort();
        var builder = NetworkApplication.CreateBuilder();
        builder.UseSecureConnections(CertPath());
        builder.Configure<ConnectionQuotaOptions>(o => o.MaxConnectionsPerIpAddress = 1234);
        builder.MapTcp<TestUtils.IntegrationTestProtocol>().OnPort((ushort)port);

        await using NetworkApplication app = builder.Build();
        await app.ActivateAsync().WaitAsync(TestUtils.Timeout);

        IConnectionGuard? guard = InstanceManager.Instance.GetExistingInstance<IConnectionGuard>();
        ConnectionGuard concrete = Assert.IsType<ConnectionGuard>(guard);
        Assert.Contains("\"MaxPerEndpoint\":1234", ReportJson(concrete), StringComparison.Ordinal);

        // No orphan guard with stale options may be left behind under the concrete key.
        ConnectionGuard? byConcreteType = InstanceManager.Instance.GetExistingInstance<ConnectionGuard>();
        Assert.True(byConcreteType is null || ReferenceEquals(byConcreteType, concrete));

        await app.DeactivateAsync().WaitAsync(TestUtils.Timeout);
    }

    [Fact]
    public async Task UseSecureConnections_WithoutUseSystemControl_HandshakeSucceeds()
    {
        int port = TestUtils.GetFreePort();
        var builder = NetworkApplication.CreateBuilder();
        builder.UseSecureConnections(CertPath()); // deliberately no UseSystemControl()
        builder.MapTcp<TestUtils.IntegrationTestProtocol>().OnPort((ushort)port);

        await using NetworkApplication app = builder.Build();
        await app.ActivateAsync().WaitAsync(TestUtils.Timeout);

        using TcpSession client = new(new TransportOptions
        {
            Address = "127.0.0.1",
            Port = (ushort)port,
            // No pinned key: forces the TOFU PUBLIC_KEY_REQUEST -> SessionTofu round-trip,
            // which is served by SystemControlHandlers.
            ServerPublicKey = string.Empty,
            ConnectTimeoutMillis = 3000,
        });
        await client.ConnectAsync().WaitAsync(TestUtils.Timeout);
        await client.HandshakeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));

        Assert.True(client.IsConnected);
        await client.DisconnectAsync().WaitAsync(TestUtils.Timeout);
        await app.DeactivateAsync().WaitAsync(TestUtils.Timeout);
    }

    [Fact]
    public void UseSecureConnections_And_UseSystemControl_InAnyOrder_DoNotDuplicateHandlers()
    {
        var a = NetworkApplication.CreateBuilder();
        a.UseSystemControl();
        a.UseSecureConnections(CertPath());
        a.UseSystemControl();
        using (a.Build()) { }

        var b = NetworkApplication.CreateBuilder();
        b.UseSecureConnections(CertPath());
        b.UseSecureConnections(CertPath());
        b.UseSystemControl();
        using (b.Build()) { }
    }

    [Fact]
    public async Task DefaultQuota_ManyConcurrentLoopbackClients_AreAllAccepted()
    {
        // Restore shipped defaults (the test assembly normally raises them).
        ConnectionQuotaOptions quota = ConfigurationManager.Instance.Get<ConnectionQuotaOptions>();
        ConnectionQuotaOptions defaults = new();
        quota.MaxConnectionsPerIpAddress = defaults.MaxConnectionsPerIpAddress;
        quota.MaxConnectionsPerWindow = defaults.MaxConnectionsPerWindow;

        int port = TestUtils.GetFreePort();
        var builder = NetworkApplication.CreateBuilder();
        builder.MapTcp<TestUtils.IntegrationTestProtocol>().OnPort((ushort)port);

        await using NetworkApplication app = builder.Build();
        await app.ActivateAsync().WaitAsync(TestUtils.Timeout);

        const int clients = 32;
        List<TcpSession> sessions = [];
        try
        {
            for (int i = 0; i < clients; i++)
            {
                TcpSession s = new(new TransportOptions { Address = "127.0.0.1", Port = (ushort)port });
                sessions.Add(s);
                await s.ConnectAsync().WaitAsync(TestUtils.Timeout);
            }

            // Give the server a moment to reject (it would close the socket) if it were going to.
            await Task.Delay(300);
            Assert.All(sessions, s => Assert.True(s.IsConnected));

            IConnectionGuard guard = InstanceManager.Instance.GetExistingInstance<IConnectionGuard>()!;
            Assert.True(guard.TryAccept(new IPEndPoint(IPAddress.Loopback, 1)), "loopback must not be banned");
        }
        finally
        {
            foreach (TcpSession s in sessions)
            {
                s.Dispose();
            }

            await app.DeactivateAsync().WaitAsync(TestUtils.Timeout);
        }
    }

    private static string CertPath() => Path.Combine(Path.GetTempPath(), $"nalix-hosting-pitfall-{System.Environment.ProcessId}.cert");

    private static string ReportJson(ConnectionGuard guard)
    {
        using MemoryStream ms = new();
        using (System.Text.Json.Utf8JsonWriter writer = new(ms))
        {
            guard.WriteReportData(writer);
        }

        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    public void Dispose()
    {
        ConnectionQuotaOptions quota = ConfigurationManager.Instance.Get<ConnectionQuotaOptions>();
        quota.MaxConnectionsPerIpAddress = _savedMaxPerIp;
        quota.MaxConnectionsPerWindow = _savedMaxPerWindow;
        InstanceManager.Instance.Clear(dispose: false);
    }
}
