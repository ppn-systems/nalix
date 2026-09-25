// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

// Entry-point code: ConfigureAwait is not required in top-level application code.
#pragma warning disable CA2007
// Sample uses inline literal strings for clarity.
#pragma warning disable CA1303

using Microsoft.Extensions.Logging;
using Nalix.Hosting;
using Nalix.Hosting.Protocols;

namespace SecureMultiTransportHelloWorld.Server;

/// <summary>
/// Entry point for the SecureMultiTransportHelloWorld server.
/// Starts TCP, UDP, and WebSocket listeners with secure connections enabled.
/// </summary>
internal static class Program
{
    private const ushort TcpPort = 57210;
    // UDP must share the same port as TCP so that the server's endpoint-pinning
    // (SEC-30) can match the UDP datagram source against the TCP connection.
    private const ushort UdpPort = 57210;
    private const ushort WebSocketPort = 57212;

    public static async Task Main()
    {
        // Create a console logger for visibility.
        using ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
        {
            _ = builder.SetMinimumLevel(LogLevel.Trace)
                       .AddConsole();
        });

        ILogger logger = loggerFactory.CreateLogger("SecureMultiTransport");

        // Build the network application using the canonical Hosting API.
        // UseSecureConnections() enables X25519 handshake and key exchange,
        // which is required for UDP authentication (session token).
        await using NetworkApplication app = NetworkApplication.CreateBuilder()
            .UseLogger(logger)
            .UseSecureConnections()
            .UseSystemControl()
            .MapHandlers(typeof(HelloHandlers))
            .MapTcp<DefaultProtocol>().OnPort(TcpPort).Bind()
            .MapUdp<DefaultProtocol>().OnPort(UdpPort).Bind()
            .MapWebSocket<DefaultProtocol>().OnPort(WebSocketPort).WithPath("/ws").Bind()
            .Build();

        // Graceful shutdown on Ctrl+C.
        using CancellationTokenSource cts = new();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        Console.WriteLine("SecureMultiTransport server is running.");
        Console.WriteLine($"  TCP + UDP  : 127.0.0.1:{TcpPort}");
        Console.WriteLine($"  WebSocket  : ws://127.0.0.1:{WebSocketPort}/ws");
        Console.WriteLine("Press Ctrl+C to stop.");

        await app.RunAsync(cts.Token);
    }
}
