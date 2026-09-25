// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nalix.Comparison.Benchmarks.Harness;
using Nalix.Hosting;
using Nalix.Hosting.Protocols;
using Nalix.Network.Options;
using Nalix.SDK.Options;
using Nalix.SDK.Transport;
using Nalix.SDK.Transport.Extensions;

namespace Nalix.Comparison.Benchmarks.Libraries;

public enum NalixMode
{
    Tcp,
    TcpAead,
    WebSocket,

    /// <summary>Diagnostic variant: TCP with LZ4 frame compression disabled on both ends.</summary>
    TcpNoCompression,

    /// <summary>Diagnostic variant: TCP with the InlinePacketDispatcher instead of the default channel dispatcher.</summary>
    TcpInline,
}

public sealed class NalixLibrary(NalixMode mode) : IBenchLibrary
{
    public string Name => mode switch
    {
        NalixMode.Tcp => "nalix-tcp",
        NalixMode.TcpAead => "nalix-tcp-aead",
        NalixMode.TcpNoCompression => "nalix-tcp-nocomp",
        NalixMode.TcpInline => "nalix-tcp-inline",
        _ => "nalix-ws",
    };

    public string Description => mode switch
    {
        NalixMode.Tcp => "Nalix TCP, plaintext, no handshake, TcpSession.RequestAsync",
        NalixMode.TcpAead => "Nalix TCP, X25519 handshake + AEAD encryption both directions",
        NalixMode.TcpNoCompression => "[diagnostic] Nalix TCP plaintext with LZ4 compression disabled (server + client)",
        NalixMode.TcpInline => "[diagnostic] Nalix TCP plaintext with InlinePacketDispatcher",
        _ => "Nalix WebSocket transport, plaintext, WebSocketSession.RequestAsync",
    };

    public async Task<IAsyncDisposable> StartServerAsync(int port)
    {
        ILogger logger = System.Environment.GetEnvironmentVariable("BENCH_LOG") == "1"
            ? LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Debug).AddConsole(c => c.LogToStandardErrorThreshold = LogLevel.Trace)).CreateLogger("nalix")
            : NullLogger.Instance;

        // The default ConnectionGuard quota (10 connections per IP / 5 s window, 50 ms min interval)
        // is an anti-abuse policy that would reject the 16/64 loopback clients used here, so it is
        // relaxed. No other Nalix defaults are changed. Note: this is applied directly on the
        // ConfigurationManager instance instead of builder.Configure<ConnectionQuotaOptions>(),
        // because UseSecureConnections() constructs the ConnectionGuard eagerly, before deferred
        // Configure<> callbacks run at Build(), so Configure<> would be silently ignored there.
        ConnectionQuotaOptions q = Nalix.Environment.Configuration.ConfigurationManager.Instance.Get<ConnectionQuotaOptions>();
        q.MaxConnectionsPerIpAddress = 10_000;
        q.MaxConnectionsPerWindow = 10_000;
        q.MaxConnectionsPerSubnet = 10_000;
        q.MaxSubnetConnectionsPerWindow = 10_000;
        q.MinConnectionIntervalMs = 0;
        q.BurstThreshold = 100;

        if (mode == NalixMode.TcpNoCompression)
        {
            Nalix.Environment.Configuration.ConfigurationManager.Instance.Get<Nalix.Codec.Options.CompressionOptions>().Enabled = false;
        }

        INetworkApplicationBuilder b = NetworkApplication.CreateBuilder().UseLogger(logger);
        if (mode == NalixMode.TcpInline)
        {
            b = b.ConfigureDispatch(opts => new Nalix.Runtime.Dispatching.InlinePacketDispatcher(opts));
        }
        if (mode == NalixMode.TcpAead)
        {
            b = b.UseSecureConnections().UseSystemControl().MapHandlers(typeof(EchoEncryptedHandlers));
        }
        else
        {
            b = b.MapHandlers(typeof(EchoHandlers));
        }

        b = mode == NalixMode.WebSocket
            ? b.ListenWebSocket<DefaultProtocol>().OnPort((ushort)port).WithPath("/ws").Bind()
            : b.ListenTcp<DefaultProtocol>().OnPort((ushort)port).Bind();

        NetworkApplication app = b.Build();
        await app.ActivateAsync();
        return app;
    }

    public IBenchClient CreateClient(int port) => new Client(mode, port);

    private sealed class Client(NalixMode mode, int port) : IBenchClient
    {
        private TransportSession? _session;
        private RequestOptions _opts = RequestOptions.Default.WithTimeout(10_000);
        private readonly EchoRequestPacket _request = new();

        public async Task ConnectAsync()
        {
            TransportOptions o = new()
            {
                Address = "127.0.0.1",
                Port = (ushort)port,
                CompressionEnabled = mode != NalixMode.TcpNoCompression,
            };
            if (mode == NalixMode.WebSocket)
            {
                WebSocketSession ws = new(o, new WebSocketTransportOptions { Path = "/ws" });
                await ws.ConnectAsync("127.0.0.1", (ushort)port);
                _session = ws;
            }
            else
            {
                TcpSession tcp = new(o);
                await tcp.ConnectAsync("127.0.0.1", (ushort)port);
                if (mode == NalixMode.TcpAead)
                {
                    await tcp.HandshakeAsync();
                    _opts = _opts.WithEncrypt();
                }

                _session = tcp;
            }
        }

        public async ValueTask<int> EchoAsync(byte[] payload)
        {
            _request.Data = payload;
            EchoResponsePacket r = await _session!.RequestAsync<EchoResponsePacket>(_request, _opts);
            return r.Data.Length;
        }

        public async ValueTask DisposeAsync()
        {
            if (_session is null) return;
            await _session.DisconnectAsync();
            _session.Dispose();
        }
    }
}
