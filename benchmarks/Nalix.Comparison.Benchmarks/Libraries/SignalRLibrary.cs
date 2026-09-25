// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Nalix.Comparison.Benchmarks.Harness;

namespace Nalix.Comparison.Benchmarks.Libraries;

public sealed class EchoHub : Hub
{
    public byte[] Echo(byte[] data) => data;
}

/// <summary>ASP.NET Core SignalR, WebSocket transport (negotiation skipped), MessagePack hub protocol.</summary>
public sealed class SignalRLibrary : IBenchLibrary
{
    public string Name => "signalr-ws-msgpack";

    public string Description => "ASP.NET Core SignalR, WebSockets transport (SkipNegotiation), MessagePack protocol, InvokeAsync";

    public async Task<IAsyncDisposable> StartServerAsync(int port)
    {
        WebApplicationBuilder builder = AspNetHost.CreateBuilder(port, HttpProtocols.Http1);
        builder.Services.AddSignalR().AddMessagePackProtocol();
        WebApplication app = builder.Build();
        app.MapHub<EchoHub>("/hub", o => o.Transports = HttpTransportType.WebSockets);
        await app.StartAsync();
        return new AspNetHost.AppHandle(app);
    }

    public IBenchClient CreateClient(int port) => new Client(port);

    private sealed class Client(int port) : IBenchClient
    {
        private readonly HubConnection _c = new HubConnectionBuilder()
            .WithUrl($"http://127.0.0.1:{port}/hub", o =>
            {
                o.Transports = HttpTransportType.WebSockets;
                o.SkipNegotiation = true;
            })
            .AddMessagePackProtocol()
            .Build();

        public Task ConnectAsync() => _c.StartAsync();

        public async ValueTask<int> EchoAsync(byte[] payload)
            => (await _c.InvokeAsync<byte[]>("Echo", payload)).Length;

        public ValueTask DisposeAsync() => _c.DisposeAsync();
    }
}
