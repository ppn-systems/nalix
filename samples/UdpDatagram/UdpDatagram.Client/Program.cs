// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

// Entry-point code: ConfigureAwait is not required in top-level application code.
#pragma warning disable CA2007
// Packets created with new() are short-lived; GC handles cleanup.
#pragma warning disable CA2000
// Sample uses inline literal strings for clarity.
#pragma warning disable CA1303

using Nalix.SDK.Options;
using Nalix.SDK.Transport;
using Nalix.SDK.Transport.Extensions;
using UdpDatagram.Contracts;

namespace UdpDatagram.Client;

/// <summary>
/// Entry point for the UdpDatagram client.
/// <list type="number">
///   <item>Connects over TCP and completes the X25519 handshake.</item>
///   <item>Opens a <see cref="UdpSession"/> that shares the TCP session state.</item>
///   <item>Sends a request over UDP and awaits the typed response.</item>
///   <item>Streams fire-and-forget telemetry over UDP.</item>
/// </list>
/// </summary>
internal static class Program
{
    private const string Host = "127.0.0.1";
    // UDP must share the TCP port so the server can match the datagram source to the TCP connection.
    private const ushort Port = 57220;

    public static async Task Main()
    {
        // TCP fills this state during the handshake (session token, secret, encryption flag);
        // the UDP session reads it to authenticate every datagram it sends.
        SessionState sharedState = new();

        TransportOptions options = new()
        {
            Address = Host,
            Port = Port,
        };

        // Step 1: TCP connect + X25519 handshake. UDP cannot be used without it,
        // because the session token and shared secret come from this exchange.
        using TcpSession tcp = new(options, sharedState);
        await tcp.ConnectAsync(Host, Port);
        await tcp.HandshakeAsync();
        Console.WriteLine("TCP connected and handshake completed.");
        Console.WriteLine($"  SessionToken : {sharedState.SessionToken}");

        // Step 2: open the UDP session on the same state.
        using UdpSession udp = new(options, sharedState);
        await udp.ConnectAsync(Host, Port);
        Console.WriteLine("UDP session opened.");

        // Step 3: request/response over UDP.
        EchoResponsePacket echo = await udp.RequestAsync<EchoResponsePacket>(
            new EchoRequestPacket { Sequence = 41 },
            RequestOptions.Default.WithTimeout(5_000));
        Console.WriteLine($"UDP echo reply: Sequence={echo.Sequence}");

        // Step 4: fire-and-forget telemetry. The server never replies, and UDP does not
        // retransmit, so a lost sample is simply gone. That is the point of using UDP for it.
        for (int i = 1; i <= 5; i++)
        {
            await udp.SendAsync(new TelemetryPacket { Sequence = i }, encrypt: null);
        }

        Console.WriteLine("Sent 5 telemetry samples.");
        await Task.Delay(500);

        await udp.DisconnectAsync();
        await tcp.DisconnectAsync();
        Console.WriteLine("Done.");
    }
}
