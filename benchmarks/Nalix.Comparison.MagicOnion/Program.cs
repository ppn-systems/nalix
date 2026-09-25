// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Grpc.Net.Client;
using MagicOnion;
using MagicOnion.Client;
using MagicOnion.Server;
using MagicOnion.Server.Hubs;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Nalix.Comparison.Benchmarks.Harness;
using Nalix.Comparison.Benchmarks.Libraries;

IBenchLibrary[] all = [new MagicOnionLibrary(hub: false), new MagicOnionLibrary(hub: true)];
return await HarnessMain.RunAsync(args, all.ToDictionary(l => l.Name));

public interface IEchoService : IService<IEchoService>
{
    UnaryResult<byte[]> EchoAsync(byte[] data);
}

public interface IEchoHubReceiver
{
}

public interface IEchoHub : IStreamingHub<IEchoHub, IEchoHubReceiver>
{
    ValueTask<byte[]> EchoAsync(byte[] data);
}

public sealed class EchoService : ServiceBase<IEchoService>, IEchoService
{
    public UnaryResult<byte[]> EchoAsync(byte[] data) => UnaryResult.FromResult(data);
}

public sealed class EchoHub : StreamingHubBase<IEchoHub, IEchoHubReceiver>, IEchoHub
{
    public ValueTask<byte[]> EchoAsync(byte[] data) => ValueTask.FromResult(data);
}

public sealed class MagicOnionLibrary(bool hub) : IBenchLibrary
{
    public string Name => hub ? "magiconion-hub" : "magiconion-unary";

    public string Description => $"MagicOnion 7 {(hub ? "StreamingHub method call" : "unary service call")} over gRPC h2c, MessagePack v3, 1 GrpcChannel per client";

    public async Task<IAsyncDisposable> StartServerAsync(int port)
    {
        WebApplicationBuilder builder = AspNetHost.CreateBuilder(port, HttpProtocols.Http2);
        builder.Services.AddGrpc();
        builder.Services.AddMagicOnion();
        WebApplication app = builder.Build();
        app.MapMagicOnionService();
        await app.StartAsync();
        return new AspNetHost.AppHandle(app);
    }

    public IBenchClient CreateClient(int port) => new Client(port, hub);

    private sealed class Receiver : IEchoHubReceiver
    {
    }

    private sealed class Client(int port, bool hub) : IBenchClient
    {
        private GrpcChannel? _channel;
        private IEchoService? _svc;
        private IEchoHub? _hub;

        public async Task ConnectAsync()
        {
            SocketsHttpHandler handler = new() { EnableMultipleHttp2Connections = true, PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan };
            _channel = GrpcChannel.ForAddress($"http://127.0.0.1:{port}", new GrpcChannelOptions { HttpHandler = handler });
            await _channel.ConnectAsync();
            if (hub)
            {
                _hub = await StreamingHubClient.ConnectAsync<IEchoHub, IEchoHubReceiver>(_channel, new Receiver());
            }
            else
            {
                _svc = MagicOnionClient.Create<IEchoService>(_channel);
            }
        }

        public async ValueTask<int> EchoAsync(byte[] payload)
            => _hub is not null ? (await _hub.EchoAsync(payload)).Length : (await _svc!.EchoAsync(payload)).Length;

        public async ValueTask DisposeAsync()
        {
            if (_hub is not null) await _hub.DisposeAsync();
            _channel?.Dispose();
        }
    }
}
