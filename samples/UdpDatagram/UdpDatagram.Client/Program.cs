// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

// Entry-point code: ConfigureAwait is not required in top-level application code.
#pragma warning disable CA2007
// Packets created with new() are short-lived; GC handles cleanup.
#pragma warning disable CA2000
// Sample uses inline literal strings for clarity.
#pragma warning disable CA1303

using Nalix.Abstractions.Networking.Datagrams;
using Nalix.SDK.Options;
using Nalix.SDK.Transport;
using Nalix.SDK.Transport.Extensions;
using UdpDatagram.Contracts;

namespace UdpDatagram.Client;

/// <summary>
/// Entry point for the UdpDatagram client.
/// <list type="number">
///   <item>Connects over TCP and completes the X25519 handshake.</item>
///   <item>Opens a <see cref="DatagramSession"/>: it obtains a ticket over TCP, then talks UDP only.</item>
///   <item>Sends a reliable request/response over UDP.</item>
///   <item>Streams unreliable telemetry over UDP.</item>
///   <item>Closes TCP and shows that the UDP session keeps working.</item>
/// </list>
/// </summary>
internal static class Program
{
    private const string Host = "127.0.0.1";
    private const ushort TcpPort = 57220;

    public static async Task Main()
    {
        TransportOptions options = new()
        {
            Address = Host,
            Port = TcpPort,
        };

        // Step 1: TCP + handshake. This is the only job TCP has here.
        await using TcpSession tcp = new(options);
        await tcp.ConnectAsync();
        await tcp.HandshakeAsync();
        Console.WriteLine("TCP connected and handshake completed.");

        // Step 2: ask the server for a UDP ticket and open the UDP socket.
        // The host and port come from the ticket, so no UDP address is configured here.
        using DatagramSession udp = new(tcp);
        await udp.ConnectAsync();
        Console.WriteLine($"UDP session established (server UDP port {udp.Ticket!.Value.Port}).");

        // Step 3: request/response over UDP. The default delivery is ReliableOrdered,
        // so lost datagrams are retransmitted transparently.
        EchoResponsePacket echo = await udp.RequestAsync<EchoResponsePacket>(
            new EchoRequestPacket { Sequence = 41 },
            RequestOptions.Default.WithTimeout(5_000));
        Console.WriteLine($"UDP echo reply: Sequence={echo.Sequence}");

        // Step 4: fire-and-forget telemetry. UnreliableSequenced never retransmits and drops
        // anything older than the newest sample already delivered, which suits position/state feeds.
        for (int i = 1; i <= 5; i++)
        {
            await udp.SendAsync(new TelemetryPacket { Sequence = i }, DeliveryMethod.UnreliableSequenced);
        }

        Console.WriteLine("Sent 5 telemetry samples (UnreliableSequenced).");
        await Task.Delay(500);

        // Step 5: the UDP session lives on its own once the ticket was issued.
        await tcp.DisconnectAsync();
        Console.WriteLine("TCP closed.");

        echo = await udp.RequestAsync<EchoResponsePacket>(
            new EchoRequestPacket { Sequence = 99 },
            RequestOptions.Default.WithTimeout(5_000));
        Console.WriteLine($"UDP still works without TCP: Sequence={echo.Sequence}");

        await udp.DisconnectAsync();
        Console.WriteLine("Done.");
    }
}
