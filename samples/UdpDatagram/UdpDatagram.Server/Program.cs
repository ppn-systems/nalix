// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

// Entry-point code: ConfigureAwait is not required in top-level application code.
#pragma warning disable CA2007
// Sample uses inline literal strings for clarity.
#pragma warning disable CA1303

using Microsoft.Extensions.Logging;
using Nalix.Hosting;
using Nalix.Hosting.Protocols;

namespace UdpDatagram.Server;

/// <summary>
/// Entry point for the UdpDatagram server.
/// Hosts a TCP listener (used only to obtain a UDP ticket) and a datagram (NDP over UDP) listener.
/// </summary>
internal static class Program
{
    private const ushort TcpPort = 57220;
    private const ushort UdpPort = 57221;

    public static async Task Main()
    {
        using ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
        {
            _ = builder.SetMinimumLevel(LogLevel.Information).AddConsole();
        });

        ILogger logger = loggerFactory.CreateLogger("UdpDatagram");

        // UseSecureConnections() registers the X25519 handshake. The client needs it to prove
        // who it is before the server issues a UDP ticket.
        // MapDatagram() maps the ticket handlers automatically; the TCP and UDP ports may differ.
        await using NetworkApplication app = NetworkApplication.CreateBuilder()
            .UseLogger(logger)
            .UseSecureConnections()
            .MapHandlers(typeof(EchoHandlers))
            .MapTcp<DefaultProtocol>().OnPort(TcpPort).Bind()
            .MapDatagram<DefaultProtocol>().OnPort(UdpPort).Bind()
            .Build();

        using CancellationTokenSource cts = new();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        Console.WriteLine("UdpDatagram server is running.");
        Console.WriteLine($"  TCP (ticket) : 127.0.0.1:{TcpPort}");
        Console.WriteLine($"  UDP          : 127.0.0.1:{UdpPort}");
        Console.WriteLine("Press Ctrl+C to stop.");

        await app.RunAsync(cts.Token);
    }
}
