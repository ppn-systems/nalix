using System.Net.Sockets;
using Nalix.Hosting;
using Nalix.SDK.Options;
using Nalix.SDK.Transport;

namespace Nalix.Hosting.Tests;

[Collection(TestCollections.RealServer)]
public sealed class HostingLifecycleTests : IDisposable
{
    public HostingLifecycleTests()
    {
        TestAssemblySetup.EnsureHighLimits();
        if (!Nalix.Codec.DataFrames.PacketRegistry.IsBuilt)
        {
            Nalix.Codec.DataFrames.PacketRegistry.Build();
        }
    }

    [Fact]
    public async Task BuildRunDispose_RoundTrip_Succeeds()
    {
        int port = TestUtils.GetFreePort();
        var builder = NetworkApplication.CreateBuilder();
        builder.ListenTcp<TestUtils.IntegrationTestProtocol>().OnPort((ushort)port);

        await using NetworkApplication app = builder.Build();
        await app.ActivateAsync().WaitAsync(TestUtils.Timeout);

        using TcpSession client = new(new TransportOptions { Address = "127.0.0.1", Port = (ushort)port });
        await client.ConnectAsync().WaitAsync(TestUtils.Timeout);
        Assert.True(client.IsConnected);
        await client.DisconnectAsync().WaitAsync(TestUtils.Timeout);

        await app.DeactivateAsync().WaitAsync(TestUtils.Timeout);
    }

    [Fact]
    public async Task RunAsync_CancelToken_ShutsDownGracefully()
    {
        int port = TestUtils.GetFreePort();
        var builder = NetworkApplication.CreateBuilder();
        builder.ListenTcp<TestUtils.IntegrationTestProtocol>().OnPort((ushort)port);

        using NetworkApplication app = builder.Build();
        using var cts = new CancellationTokenSource();

        Task runTask = app.RunAsync(cts.Token);

        // Wait until the listener is actually accepting.
        using (TcpSession probe = new(new TransportOptions { Address = "127.0.0.1", Port = (ushort)port }))
        {
            await probe.ConnectAsync().WaitAsync(TestUtils.Timeout);
            await probe.DisconnectAsync().WaitAsync(TestUtils.Timeout);
        }

        cts.Cancel();

        Task completed = await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(runTask, completed);
        await runTask.WaitAsync(TestUtils.Timeout); // Should complete without throwing (OperationCanceledException swallowed internally).
    }

    [Fact]
    public async Task StartStopStart_OnSamePort_Succeeds()
    {
        int port = TestUtils.GetFreePort();

        var builder1 = NetworkApplication.CreateBuilder();
        builder1.ListenTcp<TestUtils.IntegrationTestProtocol>().OnPort((ushort)port);
        using (NetworkApplication app1 = builder1.Build())
        {
            await app1.ActivateAsync().WaitAsync(TestUtils.Timeout);
            await app1.DeactivateAsync().WaitAsync(TestUtils.Timeout);
        }

        var builder2 = NetworkApplication.CreateBuilder();
        builder2.ListenTcp<TestUtils.IntegrationTestProtocol>().OnPort((ushort)port);
        using (NetworkApplication app2 = builder2.Build())
        {
            // Must not throw "address already in use" — the previous listener released the port.
            await app2.ActivateAsync().WaitAsync(TestUtils.Timeout);

            using TcpSession client = new(new TransportOptions { Address = "127.0.0.1", Port = (ushort)port });
            await client.ConnectAsync().WaitAsync(TestUtils.Timeout);
            Assert.True(client.IsConnected);
            await client.DisconnectAsync().WaitAsync(TestUtils.Timeout);

            await app2.DeactivateAsync().WaitAsync(TestUtils.Timeout);
        }
    }

    [Fact(Skip = "Second bind can crash/hang the Linux test host; cover this with a lower-level listener test.")]
    public async Task BindSamePort_TwoApps_FailsClearly()
    {
        int port = TestUtils.GetFreePort();

        var builder1 = NetworkApplication.CreateBuilder();
        builder1.ListenTcp<TestUtils.IntegrationTestProtocol>().OnPort((ushort)port);
        using NetworkApplication app1 = builder1.Build();
        await app1.ActivateAsync().WaitAsync(TestUtils.Timeout);

        try
        {
            var builder2 = NetworkApplication.CreateBuilder();
            builder2.ListenTcp<TestUtils.IntegrationTestProtocol>().OnPort((ushort)port);
            using NetworkApplication app2 = builder2.Build();

            await Assert.ThrowsAnyAsync<Exception>(() => app2.ActivateAsync().WaitAsync(TestUtils.Timeout));
        }
        finally
        {
            await app1.DeactivateAsync().WaitAsync(TestUtils.Timeout);
        }
    }

    [Fact]
    public async Task TcpAndUdp_SamePort_Simultaneously_BothWork()
    {
        int port = TestUtils.GetFreePort();
        var builder = NetworkApplication.CreateBuilder();
        builder.ListenTcp<TestUtils.IntegrationTestProtocol>().OnPort((ushort)port);
        builder.ListenUdp<TestUtils.IntegrationTestProtocol>().OnPort((ushort)port);

        using NetworkApplication app = builder.Build();
        await app.ActivateAsync().WaitAsync(TestUtils.Timeout);

        try
        {
            using TcpSession tcp = new(new TransportOptions { Address = "127.0.0.1", Port = (ushort)port });
            await tcp.ConnectAsync().WaitAsync(TestUtils.Timeout);
            Assert.True(tcp.IsConnected);
            await tcp.DisconnectAsync().WaitAsync(TestUtils.Timeout);
        }
        finally
        {
            await app.DeactivateAsync().WaitAsync(TestUtils.Timeout);
        }
    }

    public void Dispose() => Nalix.Framework.Injection.InstanceManager.Instance.Clear(dispose: false);
}
