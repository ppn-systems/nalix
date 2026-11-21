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
/// Hosts TCP (used for the secure handshake) and UDP listeners on the same port.
/// </summary>
internal static class Program
{
    private const ushort TcpPort = 57220;
    // UDP must share the TCP port: the server pins each UDP datagram's source
    // endpoint to the endpoint of the TCP connection that owns the session token.
    private const ushort UdpPort = 57220;

    public static async Task Main()
    {
        using ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
        {
            _ = builder.SetMinimumLevel(LogLevel.Information).AddConsole();
        });

        ILogger logger = loggerFactory.CreateLogger("UdpDatagram");

        // UseSecureConnections() registers the X25519 handshake. It produces the session token
        // and shared secret that authenticate every UDP datagram.
        await using NetworkApplication app = NetworkApplication.CreateBuilder()
            .UseLogger(logger)
            .UseSecureConnections()
            .MapHandlers(typeof(EchoHandlers))
            .MapTcp<DefaultProtocol>().OnPort(TcpPort).Bind()
            .MapUdp<DefaultProtocol>().OnPort(UdpPort).Bind()
            .Build();

        using CancellationTokenSource cts = new();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        Console.WriteLine("UdpDatagram server is running.");
        Console.WriteLine($"  TCP + UDP : 127.0.0.1:{TcpPort}");
        Console.WriteLine("Press Ctrl+C to stop.");

        await app.RunAsync(cts.Token);
    }
}
