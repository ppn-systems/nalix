// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net.WebSockets;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Nalix.Comparison.Benchmarks.Harness;

namespace Nalix.Comparison.Benchmarks.Libraries;

/// <summary>Baseline: raw ASP.NET Core WebSocket echo (no protocol on top).</summary>
public sealed class KestrelWebSocketLibrary : IBenchLibrary
{
    public string Name => "kestrel-ws";

    public string Description => "Baseline: raw Kestrel WebSocket binary echo (UseWebSockets, ClientWebSocket)";

    public async Task<IAsyncDisposable> StartServerAsync(int port)
    {
        WebApplication app = AspNetHost.CreateBuilder(port, HttpProtocols.Http1).Build();
        app.UseWebSockets();
        app.Map("/ws", async ctx =>
        {
            using WebSocket ws = await ctx.WebSockets.AcceptWebSocketAsync();
            byte[] buf = new byte[64 * 1024];
            while (ws.State == WebSocketState.Open)
            {
                int total = 0;
                ValueWebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(buf.AsMemory(total), default);
                    if (r.MessageType == WebSocketMessageType.Close)
                    {
                        await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, default);
                        return;
                    }

                    total += r.Count;
                }
                while (!r.EndOfMessage);
                await ws.SendAsync(buf.AsMemory(0, total), WebSocketMessageType.Binary, true, default);
            }
        });
        await app.StartAsync();
        return new AspNetHost.AppHandle(app);
    }

    public IBenchClient CreateClient(int port) => new Client(port);

    private sealed class Client(int port) : IBenchClient
    {
        private readonly ClientWebSocket _ws = new();
        private readonly byte[] _buf = new byte[64 * 1024];

        public Task ConnectAsync() => _ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"), default);

        public async ValueTask<int> EchoAsync(byte[] payload)
        {
            await _ws.SendAsync(payload.AsMemory(), WebSocketMessageType.Binary, true, default);
            int total = 0;
            ValueWebSocketReceiveResult r;
            do
            {
                r = await _ws.ReceiveAsync(_buf.AsMemory(total), default);
                total += r.Count;
            }
            while (!r.EndOfMessage);
            return total;
        }

        public async ValueTask DisposeAsync()
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, default); } catch { /* ignore */ }
            _ws.Dispose();
        }
    }
}
