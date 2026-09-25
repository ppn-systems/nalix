// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Nalix.Comparison.Benchmarks.Grpc;
using Nalix.Comparison.Benchmarks.Harness;

namespace Nalix.Comparison.Benchmarks.Libraries;

public sealed class EchoService : Echo.EchoBase
{
    public override Task<EchoMessage> Unary(EchoMessage request, ServerCallContext context) => Task.FromResult(request);

    public override async Task Duplex(IAsyncStreamReader<EchoMessage> requestStream, IServerStreamWriter<EchoMessage> responseStream, ServerCallContext context)
    {
        await foreach (EchoMessage m in requestStream.ReadAllAsync())
        {
            await responseStream.WriteAsync(m);
        }
    }
}

/// <summary>gRPC for .NET (Grpc.AspNetCore + Grpc.Net.Client), unary or bidirectional streaming, h2c or TLS.</summary>
public sealed class GrpcLibrary(bool duplex, bool tls) : IBenchLibrary
{
    public string Name => $"grpc-{(duplex ? "duplex" : "unary")}{(tls ? "-tls" : string.Empty)}";

    public string Description => $"gRPC {(duplex ? "bidi stream (1 write + 1 read per op on a long-lived call)" : "unary call")} over {(tls ? "HTTP/2 + TLS (self-signed ECDSA P-256)" : "h2c plaintext")}, 1 GrpcChannel per client";

    public async Task<IAsyncDisposable> StartServerAsync(int port)
    {
        WebApplicationBuilder builder = AspNetHost.CreateBuilder(port, HttpProtocols.Http2, tls);
        builder.Services.AddGrpc();
        WebApplication app = builder.Build();
        app.MapGrpcService<EchoService>();
        await app.StartAsync();
        return new AspNetHost.AppHandle(app);
    }

    public IBenchClient CreateClient(int port) => new Client(port, duplex, tls);

    private sealed class Client(int port, bool duplex, bool tls) : IBenchClient
    {
        private GrpcChannel? _channel;
        private Echo.EchoClient? _client;
        private AsyncDuplexStreamingCall<EchoMessage, EchoMessage>? _call;

        public async Task ConnectAsync()
        {
            SocketsHttpHandler handler = new()
            {
                EnableMultipleHttp2Connections = true,
                PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
            };
            if (tls)
            {
                handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            }

            _channel = GrpcChannel.ForAddress($"{(tls ? "https" : "http")}://127.0.0.1:{port}", new GrpcChannelOptions { HttpHandler = handler });
            _client = new Echo.EchoClient(_channel);
            await _channel.ConnectAsync();
            if (duplex)
            {
                _call = _client.Duplex();
            }
        }

        public async ValueTask<int> EchoAsync(byte[] payload)
        {
            EchoMessage m = new() { Data = Google.Protobuf.UnsafeByteOperations.UnsafeWrap(payload) };
            if (_call is null)
            {
                return (await _client!.UnaryAsync(m)).Data.Length;
            }

            await _call.RequestStream.WriteAsync(m);
            if (!await _call.ResponseStream.MoveNext(default)) throw new InvalidOperationException("stream ended");
            return _call.ResponseStream.Current.Data.Length;
        }

        public async ValueTask DisposeAsync()
        {
            if (_call is not null)
            {
                try { await _call.RequestStream.CompleteAsync(); } catch { /* ignore */ }
                _call.Dispose();
            }

            _channel?.Dispose();
        }
    }
}
