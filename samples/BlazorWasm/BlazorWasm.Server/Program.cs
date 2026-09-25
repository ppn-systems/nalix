// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

// Entry-point code: ConfigureAwait is not required in top-level application code.
#pragma warning disable CA2007
// Sample uses inline literal strings for clarity.
#pragma warning disable CA1303

using Microsoft.Extensions.Logging;
using Nalix.Environment.Configuration;
using Nalix.Hosting;
using Nalix.Hosting.Protocols;
using Nalix.Network.Options;
using Nalix.Runtime.Handlers;

namespace BlazorWasm.Server;

/// <summary>
/// Nalix server for the Blazor WebAssembly sample. Exposes a single WebSocket endpoint
/// (<c>ws://localhost:57230/ws/</c>) protected by the X25519 handshake.
/// </summary>
internal static class Program
{
    private const ushort WebSocketPort = 57230;
    private const string WebSocketPath = "/ws/";

    public static async Task Main()
    {
        using ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
        {
            _ = builder.SetMinimumLevel(LogLevel.Information)
                       .AddConsole();
        });

        ILogger logger = loggerFactory.CreateLogger("BlazorWasm");

        // Local development: every browser tab, page reload and reconnect comes from 127.0.0.1.
        // The default per-IP guard (10 attempts / 5 s, halved on bursts) bans loopback after a
        // few quick reloads. Relax it here - set real limits (or server.ini) in production.
        // Set on the live options object *before* building so the guard picks it up regardless
        // of builder call order.
        ConnectionQuotaOptions quota = ConfigurationManager.Instance.Get<ConnectionQuotaOptions>();
        quota.MaxConnectionsPerIpAddress = 1_000;
        quota.MaxConnectionsPerWindow = 10_000;

        await using NetworkApplication app = NetworkApplication.CreateBuilder()
            .UseLogger(logger)
            // X25519 handshake + AEAD encryption. Works from the browser: Nalix's crypto is
            // fully managed and does not use System.Security.Cryptography.
            .UseSecureConnections()
            // Serves PUBLIC_KEY_REQUEST (TOFU), ping, disconnect and other control frames.
            // Required by the handshake when the client does not pin the server key.
            .UseSystemControl()
            // Session store: lets a reconnecting browser tab resume instead of re-handshaking.
            .UseSessions()
            .MapHandlers(typeof(DemoHandlers))
            .MapWebSocket<DefaultProtocol>().OnPort(WebSocketPort).WithPath(WebSocketPath).Bind()
            .Build();

        using CancellationTokenSource cts = new();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        Task run = app.RunAsync(cts.Token);

        Console.WriteLine($"BlazorWasm server: ws://localhost:{WebSocketPort}{WebSocketPath}");
        // Pin this in the client's wwwroot/appsettings.json (Nalix:ServerPublicKey) for production.
        Console.WriteLine($"Server public key: {HandshakeHandlers.ServerPublicKey}");
        Console.WriteLine("Press Ctrl+C to stop.");

        await run;
    }
}
