// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Codec.DataFrames;
using Nalix.Environment.Configuration;
using Nalix.Framework.Injection;
using Nalix.Hosting;
using Nalix.Hosting.Protocols;
using Nalix.Integration.Tests.TestSupport;
using Nalix.Network.Options;
using Nalix.SDK.Options;
using Nalix.SDK.Transport;
using Nalix.SDK.Transport.Extensions;
using Nalix.Tests.Shared;
using Xunit;

namespace Nalix.Integration.Tests.EndToEnd;

/// <summary>
/// Full client/server scenarios: a real <see cref="NetworkApplication"/> running <see cref="DefaultProtocol"/> with
/// handlers mapped through Hosting, driven by the public <c>Nalix.SDK</c> sessions over loopback sockets.
/// </summary>
[Collection(TestCollections.NetworkConfig)]
public sealed class ClientServerEndToEndTests : IDisposable
{
    private const string Host = "127.0.0.1";
    private const string WebSocketPath = "/ws/";
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(15);
    private static readonly RequestOptions s_request = RequestOptions.Default.WithTimeout(5_000);

    private readonly NetworkConfigSnapshot _configSnapshot = new();
    private readonly string _certificatePath = Path.Combine(Path.GetTempPath(), $"nalix-e2e-{Guid.NewGuid():N}.private");

    public ClientServerEndToEndTests()
    {
        File.WriteAllText(_certificatePath, "0000000000000000000000000000000000000000000000000000000000000001");
        ConfigurationManager.Instance.Get<NetworkWebSocketOptions>().Host = Host;

        if (!PacketRegistry.IsBuilt)
        {
            PacketRegistry.Build();
        }
    }

    public void Dispose()
    {
        _configSnapshot.Dispose();
        File.Delete(_certificatePath);
        InstanceManager.Instance.Clear(dispose: false);
    }

    [Fact]
    public async Task RequestAsync_OverPlainTcp_ReturnsHandlerResponse()
    {
        ushort port = NextPort();
        await using NetworkApplication app = await this.StartServerAsync(port, secure: false);

        using TcpSession client = new(CreateOptions(port));
        await client.ConnectAsync().WaitAsync(s_timeout);

        EchoPacket response = await client.RequestAsync<EchoPacket>(
            new EchoPacket { Sequence = 41, Text = "hello nalix" }, s_request).AsTask().WaitAsync(s_timeout);

        Assert.Equal(42, response.Sequence);
        Assert.Equal("HELLO NALIX", response.Text);

        await client.DisconnectAsync().WaitAsync(s_timeout);
    }

    [Fact]
    public async Task RequestAsync_AfterHandshakeWithEncryption_ReturnsHandlerResponse()
    {
        ushort port = NextPort();
        await using NetworkApplication app = await this.StartServerAsync(port, secure: true);

        using TcpSession client = new(CreateOptions(port));
        await client.ConnectAsync().WaitAsync(s_timeout);
        await client.HandshakeAsync().AsTask().WaitAsync(s_timeout);

        EchoPacket response = await client.RequestAsync<EchoPacket>(
            new EchoPacket { Sequence = 7, Text = "secret" }, s_request.WithEncrypt()).AsTask().WaitAsync(s_timeout);

        Assert.True(client.State.EncryptionEnabled);
        Assert.Equal(8, response.Sequence);
        Assert.Equal("SECRET", response.Text);

        await client.DisconnectAsync().WaitAsync(s_timeout);
    }

    [Fact]
    public async Task RequestAsync_WithCompressedLargePayload_ReturnsPayloadIntact()
    {
        ushort port = NextPort();
        await using NetworkApplication app = await this.StartServerAsync(port, secure: true);

        TransportOptions options = CreateOptions(port);
        options.CompressionEnabled = true;
        options.CompressionThreshold = 64;
        using TcpSession client = new(options);
        await client.ConnectAsync().WaitAsync(s_timeout);
        await client.HandshakeAsync().AsTask().WaitAsync(s_timeout);
        string text = string.Concat(Enumerable.Repeat("nalix-compression-", 1_000));

        EchoPacket response = await client.RequestAsync<EchoPacket>(
            new EchoPacket { Sequence = 1, Text = text }, s_request.WithEncrypt()).AsTask().WaitAsync(s_timeout);

        Assert.Equal(2, response.Sequence);
        Assert.Equal(text.ToUpperInvariant(), response.Text);

        await client.DisconnectAsync().WaitAsync(s_timeout);
    }

    [Fact]
    public async Task RequestAsync_WithPayloadLargerThanChunkSize_IsFragmentedBothWaysAndReturnsIntact()
    {
        ushort port = NextPort();
        await using NetworkApplication app = await this.StartServerAsync(port, secure: true);

        using TcpSession client = new(CreateOptions(port));
        await client.ConnectAsync().WaitAsync(s_timeout);
        await client.HandshakeAsync().AsTask().WaitAsync(s_timeout);

        // Incompressible and several times FragmentOptions.MaxChunkSize, so both the SDK sender and the
        // server sender must split it into FRAGMENTED chunks and the other side must reassemble them.
        Random random = new(1234);
        string text = new([.. Enumerable.Range(0, 100_000).Select(_ => (char)random.Next('a', 'z' + 1))]);

        EchoPacket response = await client.RequestAsync<EchoPacket>(
            new EchoPacket { Sequence = 5, Text = text }, s_request.WithEncrypt()).AsTask().WaitAsync(s_timeout);

        Assert.Equal(6, response.Sequence);
        Assert.Equal(text.ToUpperInvariant(), response.Text);

        await client.DisconnectAsync().WaitAsync(s_timeout);
    }

    [Theory]
    // EncryptRequestsByDefault, RequestOptions.Encrypt, expected encrypted on the wire
    [InlineData(false, null, false)]  // default: TLS-wrapped deployments keep requests unencrypted
    [InlineData(true, null, true)]    // opted in: follow the session (handshake done -> encrypted)
    [InlineData(true, false, false)]  // explicit false wins over the session default
    [InlineData(false, true, true)]   // explicit true wins over the session default
    public async Task RequestAsync_AfterHandshake_EncryptsPerOptionAndSessionDefault(bool encryptByDefault, bool? encrypt, bool expectedEncrypted)
    {
        ushort port = NextPort();
        await using NetworkApplication app = await this.StartServerAsync(port, secure: true);

        TransportOptions options = CreateOptions(port);
        options.EncryptRequestsByDefault = encryptByDefault;
        using TcpSession client = new(options);
        await client.ConnectAsync().WaitAsync(s_timeout);
        await client.HandshakeAsync().AsTask().WaitAsync(s_timeout);

        EchoPacket response = await client.RequestAsync<EchoPacket>(
            new EchoPacket { Sequence = 1, Text = "enc" }, RequestOptions.Default.WithTimeout(5_000).WithEncrypt(encrypt)).AsTask().WaitAsync(s_timeout);

        Assert.Equal("ENC", response.Text);
        Assert.Equal(expectedEncrypted, EchoHandlers.LastEchoEncryptedOnWire);

        await client.DisconnectAsync().WaitAsync(s_timeout);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposeAsync_OnConnectedSession_ClosesBeforeReturningAndDoesNotReconnect(bool webSocket)
    {
        ushort port = NextPort();
        ushort webSocketPort = NextPort();
        await using NetworkApplication app = await this.StartServerAsync(port, secure: true, webSocketPort: webSocketPort);

        TransportOptions options = CreateOptions(webSocket ? webSocketPort : port);
        options.AutoReconnectEnabled = true;
        TransportSession client = webSocket
            ? new WebSocketSession(options, new WebSocketTransportOptions { Path = WebSocketPath })
            : new TcpSession(options);

        int disconnected = 0;
        int connected = 0;
        client.OnDisconnected += (_, _) => Interlocked.Increment(ref disconnected);
        await client.ConnectAsync().WaitAsync(s_timeout);
        await client.HandshakeAsync().AsTask().WaitAsync(s_timeout);
        client.OnConnected += (_, _) => Interlocked.Increment(ref connected);

        await client.DisposeAsync().AsTask().WaitAsync(s_timeout);

        // The session is closed and the disconnect was reported once. (That the receive loop has stopped too is
        // covered by TcpSessionDisposeAsyncTests, which can observe the loop.)
        Assert.False(client.IsConnected);
        Assert.Equal(1, Volatile.Read(ref disconnected));

        // Disposal is application-initiated: auto-reconnect must not bring the session back.
        await Task.Delay(500);
        Assert.Equal(0, Volatile.Read(ref connected));

        // Idempotent, also mixed with Dispose().
        await client.DisposeAsync();
        client.Dispose();
        Assert.Equal(1, Volatile.Read(ref disconnected));
    }

    [Fact]
    public async Task DisposeAsync_OnSessionThatNeverConnected_DoesNotThrow()
    {
        await using TcpSession client = new(CreateOptions(NextPort()));
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task RequestAsync_WhenItSucceeds_EmitsOkRequestActivity()
    {
        ushort port = NextPort();
        await using NetworkApplication app = await this.StartServerAsync(port, secure: false);
        using TcpSession client = new(CreateOptions(port));
        await client.ConnectAsync().WaitAsync(s_timeout);

        using ActivitySource testSource = new("Nalix.Integration.Tests.Telemetry");
        System.Collections.Concurrent.ConcurrentQueue<Activity> stopped = new();
        using ActivityListener listener = new()
        {
            ShouldListenTo = s => s.Name is Nalix.SDK.SdkTelemetry.ActivitySourceName or "Nalix.Integration.Tests.Telemetry",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);

        using (Activity root = testSource.StartActivity("test-root")!)
        {
            EchoPacket response = await client.RequestAsync<EchoPacket>(new EchoPacket { Sequence = 1, Text = "otel" }, s_request).AsTask().WaitAsync(s_timeout);
            Assert.Equal("OTEL", response.Text);

            Activity request = Assert.Single(stopped, a => a.OperationName == "nalix.request" && a.ParentSpanId == root.SpanId);
            Assert.Equal("ok", request.GetTagItem("nalix.request.outcome"));
            Assert.Equal(1, request.GetTagItem("nalix.request.attempts"));
            Assert.Equal((int)EchoPacket.OpCode, request.GetTagItem("nalix.opcode"));
            Assert.Equal(ActivityStatusCode.Unset, request.Status);
        }

        await client.DisconnectAsync().WaitAsync(s_timeout);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RekeyAsync_OnTcpAndWebSocket_RotatesTheKeyAndKeepsTheSessionUsable(bool webSocket)
    {
        ushort port = NextPort();
        ushort webSocketPort = NextPort();
        await using NetworkApplication app = await this.StartServerAsync(port, secure: true, webSocketPort: webSocketPort);

        using TransportSession client = webSocket
            ? new WebSocketSession(CreateOptions(webSocketPort), new WebSocketTransportOptions { Path = WebSocketPath })
            : new TcpSession(CreateOptions(port));
        await client.ConnectAsync().WaitAsync(s_timeout);
        await client.HandshakeAsync().AsTask().WaitAsync(s_timeout);
        Nalix.Abstractions.Primitives.Bytes32 before = client.State.Secret;

        await client.RekeyAsync().AsTask().WaitAsync(s_timeout);

        Assert.NotEqual(before, client.State.Secret);
        EchoPacket response = await client.RequestAsync<EchoPacket>(
            new EchoPacket { Sequence = 1, Text = "after rekey" }, s_request.WithEncrypt()).AsTask().WaitAsync(s_timeout);
        Assert.Equal("AFTER REKEY", response.Text);
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task HandshakeAsync_WithWrongPinnedServerKey_FailsWithoutEncrypting()
    {
        ushort port = NextPort();
        await using NetworkApplication app = await this.StartServerAsync(port, secure: true);

        TransportOptions options = CreateOptions(port);
        options.ServerPublicKey = new string('a', 64);
        using TcpSession client = new(options);
        await client.ConnectAsync().WaitAsync(s_timeout);

        _ = await Assert.ThrowsAnyAsync<Exception>(async () => await client.HandshakeAsync().AsTask().WaitAsync(s_timeout));
        Assert.False(client.State.EncryptionEnabled);
    }

    [Fact]
    public async Task TryRequestAsync_WhenCanceled_Throws()
    {
        ushort port = NextPort();
        await using NetworkApplication app = await this.StartServerAsync(port, secure: false);
        using TcpSession client = new(CreateOptions(port));
        await client.ConnectAsync().WaitAsync(s_timeout);

        using CancellationTokenSource canceled = new();
        await canceled.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await client.TryRequestAsync<EchoPacket>(new EchoPacket { Sequence = 1, Text = "x" }, s_request, ct: canceled.Token));
    }

    [Fact]
    public async Task RequestAsync_FromManyConcurrentClients_EachReceivesOwnResponses()
    {
        const int clientCount = 8;
        const int requestsPerClient = 10;
        ushort port = NextPort();
        await using NetworkApplication app = await this.StartServerAsync(port, secure: true);

        int[] completed = await Task.WhenAll(Enumerable.Range(0, clientCount).Select(async id =>
        {
            using TcpSession client = new(CreateOptions(port));
            await client.ConnectAsync().WaitAsync(s_timeout);
            await client.HandshakeAsync().AsTask().WaitAsync(s_timeout);

            int ok = 0;
            for (int i = 0; i < requestsPerClient; i++)
            {
                int sequence = (id * 1_000) + i;
                EchoPacket response = await client.RequestAsync<EchoPacket>(
                    new EchoPacket { Sequence = sequence, Text = $"client-{id}-{i}" },
                    s_request.WithEncrypt(),
                    predicate: r => r.Sequence == sequence + 1).AsTask().WaitAsync(s_timeout);

                if (response.Text == $"CLIENT-{id}-{i}")
                {
                    ok++;
                }
            }

            await client.DisconnectAsync().WaitAsync(s_timeout);
            return ok;
        })).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.All(completed, count => Assert.Equal(requestsPerClient, count));
    }

    [Fact]
    public async Task PingAsync_AfterHandshake_ReturnsNonNegativeRoundTrip()
    {
        ushort port = NextPort();
        await using NetworkApplication app = await this.StartServerAsync(port, secure: true);

        using TcpSession client = new(CreateOptions(port));
        await client.ConnectAsync().WaitAsync(s_timeout);
        await client.HandshakeAsync().AsTask().WaitAsync(s_timeout);

        double rttMs = await client.PingAsync(timeoutMs: 5_000).AsTask().WaitAsync(s_timeout);

        Assert.True(rttMs >= 0, $"Round trip must be non-negative, was {rttMs} ms.");

        await client.DisconnectAsync().WaitAsync(s_timeout);
    }

    [Fact]
    public async Task RequestAsync_OverUdpSharingTcpSession_ReturnsHandlerResponse()
    {
        ushort port = NextPort();
        await using NetworkApplication app = await this.StartServerAsync(port, secure: true, udp: true);

        SessionState sharedState = new();
        using TcpSession tcp = new(CreateOptions(port), sharedState);
        await tcp.ConnectAsync().WaitAsync(s_timeout);
        await tcp.HandshakeAsync().AsTask().WaitAsync(s_timeout);
        using UdpSession udp = new(CreateOptions(port), sharedState);
        await udp.ConnectAsync().WaitAsync(s_timeout);

        EchoPacket response = await udp.RequestAsync<EchoPacket>(
            new EchoPacket { Sequence = 99, Text = "udp" }, s_request.WithRetry(2)).AsTask().WaitAsync(s_timeout);

        Assert.Equal(100, response.Sequence);
        Assert.Equal("UDP", response.Text);
        Assert.True(tcp.IsConnected);

        await udp.DisconnectAsync().WaitAsync(s_timeout);
        await tcp.DisconnectAsync().WaitAsync(s_timeout);
    }

    [Fact]
    public async Task RequestAsync_OverWebSocketAfterHandshake_ReturnsHandlerResponse()
    {
        ushort port = NextPort();
        ushort webSocketPort = NextPort();
        await using NetworkApplication app = await this.StartServerAsync(port, secure: true, webSocketPort: webSocketPort);

        using WebSocketSession client = new(CreateOptions(webSocketPort), new WebSocketTransportOptions { Path = WebSocketPath });
        await client.ConnectAsync().WaitAsync(s_timeout);
        await client.HandshakeAsync().AsTask().WaitAsync(s_timeout);

        EchoPacket response = await client.RequestAsync<EchoPacket>(
            new EchoPacket { Sequence = 3, Text = "websocket" }, s_request.WithEncrypt()).AsTask().WaitAsync(s_timeout);

        Assert.Equal(4, response.Sequence);
        Assert.Equal("WEBSOCKET", response.Text);

        await client.DisconnectAsync().WaitAsync(s_timeout);
    }

    [Fact]
    public async Task RequestAsync_OverWebSocketWithPayloadLargerThanOneFrame_ReturnsIntact()
    {
        ushort port = NextPort();
        ushort webSocketPort = NextPort();
        await using NetworkApplication app = await this.StartServerAsync(port, secure: true, webSocketPort: webSocketPort);

        using WebSocketSession client = new(CreateOptions(webSocketPort), new WebSocketTransportOptions { Path = WebSocketPath });
        await client.ConnectAsync().WaitAsync(s_timeout);
        await client.HandshakeAsync().AsTask().WaitAsync(s_timeout);

        // ~500 KB of incompressible text: far larger than a single WebSocket receive buffer, so the message
        // spans many native WebSocket frames both ways. WebSocket framing (not Nalix fragment chunks) must
        // carry it, and both the server and the SDK reader must reassemble the whole message intact.
        Random random = new(4321);
        string text = new([.. Enumerable.Range(0, 500_000).Select(_ => (char)random.Next('a', 'z' + 1))]);

        EchoPacket response = await client.RequestAsync<EchoPacket>(
            new EchoPacket { Sequence = 9, Text = text }, s_request.WithEncrypt()).AsTask().WaitAsync(s_timeout);

        Assert.Equal(10, response.Sequence);
        Assert.Equal(text.ToUpperInvariant(), response.Text);

        await client.DisconnectAsync().WaitAsync(s_timeout);
    }

    [Theory]
    [InlineData(false, 2)]
    // 300 padded replies exceed 64 KB, so the coalesced batch spans several socket writes.
    [InlineData(false, 300)]
    [InlineData(true, 2)]
    [InlineData(true, 300)]
    public async Task ReplyAsync_WithEncryptedBatch_DeliversEveryPacketInOrder(bool webSocket, int count)
    {
        ushort port = NextPort();
        ushort webSocketPort = NextPort();
        await using NetworkApplication app = await this.StartServerAsync(port, secure: true, webSocketPort: webSocketPort);

        using TransportSession client = webSocket
            ? new WebSocketSession(CreateOptions(webSocketPort), new WebSocketTransportOptions { Path = WebSocketPath })
            : new TcpSession(CreateOptions(port));
        await client.ConnectAsync().WaitAsync(s_timeout);
        await client.HandshakeAsync().AsTask().WaitAsync(s_timeout);

        System.Collections.Generic.List<(int Sequence, string Text)> received = [];
        TaskCompletionSource all = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable subscription = client.On<EchoPacket>(packet =>
        {
            lock (received)
            {
                received.Add((packet.Sequence, packet.Text));
                if (received.Count == count)
                {
                    _ = all.TrySetResult();
                }
            }
        });

        await client.SendAsync(new EchoBatchRequest { Count = count }, encrypt: true).WaitAsync(s_timeout);
        await all.Task.WaitAsync(s_timeout);

        // Every frame decrypted on the client, and the payloads show nothing was dropped or reordered.
        Assert.Equal(Enumerable.Range(0, count).Select(static i => (i, EchoBatchRequest.TextFor(i))), received);

        // The connection is still usable for ordinary request/response afterwards.
        EchoPacket response = await client.RequestAsync<EchoPacket>(
            new EchoPacket { Sequence = 1, Text = "after" }, s_request.WithEncrypt()).AsTask().WaitAsync(s_timeout);
        Assert.Equal("AFTER", response.Text);

        await client.DisconnectAsync().WaitAsync(s_timeout);
    }

    [Fact]
    public async Task RequestAsync_WithTwoWebSocketPortsInOneApplication_ServesBoth()
    {
        ushort first = NextPort();
        ushort second = NextPort();
        NetworkApplicationBuilder builder = NetworkApplication.CreateBuilder();
        _ = builder.MapHandlers(typeof(EchoHandlers));
        _ = builder.MapWebSocket<DefaultProtocol>().OnPort(first).WithPath(WebSocketPath).Bind();
        _ = builder.MapWebSocket<DefaultProtocol>().OnPort(second).WithPath(WebSocketPath).Bind();
        await using NetworkApplication app = builder.Build();
        await app.ActivateAsync().WaitAsync(s_timeout);

        foreach (ushort port in new[] { first, second })
        {
            using WebSocketSession client = new(CreateOptions(port), new WebSocketTransportOptions { Path = WebSocketPath });
            await client.ConnectAsync().WaitAsync(s_timeout);

            EchoPacket response = await client.RequestAsync<EchoPacket>(
                new EchoPacket { Sequence = port, Text = "ws" }, s_request).AsTask().WaitAsync(s_timeout);

            Assert.Equal(port + 1, response.Sequence);
            await client.DisconnectAsync().WaitAsync(s_timeout);
        }
    }

    [Fact]
    public async Task Deactivate_WithConnectedClient_RaisesClientDisconnected()
    {
        ushort port = NextPort();
        NetworkApplication app = await this.StartServerAsync(port, secure: false);

        try
        {
            using TcpSession client = new(CreateOptions(port));
            TaskCompletionSource disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
            client.OnDisconnected += (_, _) => disconnected.TrySetResult();
            await client.ConnectAsync().WaitAsync(s_timeout);
            _ = await client.RequestAsync<EchoPacket>(
                new EchoPacket { Sequence = 0, Text = "ready" }, s_request).AsTask().WaitAsync(s_timeout);

            await app.DeactivateAsync().WaitAsync(s_timeout);

            await disconnected.Task.WaitAsync(s_timeout);
            Assert.False(client.IsConnected);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    private static ushort NextPort() => (ushort)TestPorts.Next();

    private static TransportOptions CreateOptions(ushort port) => new()
    {
        Address = Host,
        Port = port,
        CompressionEnabled = false,
        ConnectTimeoutMillis = 5_000,
    };

    private async Task<NetworkApplication> StartServerAsync(ushort port, bool secure, bool udp = false, ushort? webSocketPort = null)
    {
        NetworkApplicationBuilder builder = NetworkApplication.CreateBuilder();
        if (secure)
        {
            _ = builder.UseSecureConnections(_certificatePath).UseTimeSync();
        }

        _ = builder.MapHandlers(typeof(EchoHandlers));
        _ = builder.MapTcp<DefaultProtocol>().OnPort(port).Bind();
        if (udp)
        {
            _ = builder.MapUdp<DefaultProtocol>().OnPort(port).Bind();
        }

        if (webSocketPort is ushort wsPort)
        {
            _ = builder.MapWebSocket<DefaultProtocol>().OnPort(wsPort).WithPath(WebSocketPath).Bind();
        }

        NetworkApplication app = builder.Build();
        await app.ActivateAsync().WaitAsync(s_timeout);
        return app;
    }
}
