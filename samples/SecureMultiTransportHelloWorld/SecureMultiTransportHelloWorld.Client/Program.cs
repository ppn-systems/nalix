// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

// Entry-point code: ConfigureAwait is not required in top-level application code.
#pragma warning disable CA2007
// Packet created with new() is short-lived; GC handles cleanup.
#pragma warning disable CA2000
// Sample uses inline literal strings for clarity.
#pragma warning disable CA1303

using Nalix.SDK.Options;
using Nalix.SDK.Transport;
using Nalix.SDK.Transport.Extensions;
using SecureMultiTransportHelloWorld.Contracts;

namespace SecureMultiTransportHelloWorld.Client;

/// <summary>
/// Entry point for the SecureMultiTransportHelloWorld client.
/// <para>
/// Demonstrates multi-transport communication with a Nalix server:
/// <list type="number">
///   <item>Connects over TCP and performs X25519 handshake.</item>
///   <item>Sends a request over TCP and receives the response.</item>
///   <item>Opens a datagram (UDP) session: it gets a ticket over the secure TCP session.</item>
///   <item>Sends a request over UDP and receives the response.</item>
///   <item>Optionally tests WebSocket transport.</item>
/// </list>
/// </para>
/// <para>
/// TCP must be connected and handshake completed before UDP can be used,
/// because UDP packets require the session token negotiated during the TCP handshake.
/// </para>
/// </summary>
internal static class Program
{
    private const string Host = "127.0.0.1";
    private const ushort TcpPort = 57210;
    private const ushort WebSocketPort = 57212;

    public static async Task Main()
    {
        TransportOptions tcpOptions = new()
        {
            Address = Host,
            Port = TcpPort,
        };

        // ── Step 1: Connect over TCP ──────────────────────────────────────
        using TcpSession tcpSession = new(tcpOptions);
        await tcpSession.ConnectAsync(Host, TcpPort);
        Console.WriteLine("Connected over TCP.");

        // ── Step 2: Perform X25519 handshake ─────────────────────────────
        // This negotiates a shared secret, enables AEAD encryption,
        // and assigns a session token that UDP will use for authentication.
        await tcpSession.HandshakeAsync();
        Console.WriteLine("TCP handshake completed. Secure state is ready.");
        Console.WriteLine($"  SessionToken : {tcpSession.State.SessionToken}");
        Console.WriteLine($"  Encryption   : {tcpSession.State.EncryptionEnabled}");

        // ── Step 3: Send request over TCP ─────────────────────────────────
        HelloRequestPacket tcpRequest = new();
        HelloResponsePacket tcpResponse = await tcpSession.RequestAsync<HelloResponsePacket>(
            tcpRequest,
            RequestOptions.Default.WithTimeout(5_000));

        string tcpMessage = tcpResponse.Message switch
        {
            1 => "Hello from Nalix!",
            _ => $"Unknown (Message={tcpResponse.Message})",
        };
        Console.WriteLine($"TCP replied: {tcpMessage}");

        // ── Step 4: Send request over UDP ─────────────────────────────────
        // UDP is only used AFTER TCP secure setup is complete.
        // DatagramSession asks the server for a ticket over the TCP session, derives its own keys
        // from the TCP session secret and then talks UDP only. Every datagram is sealed with
        // ChaCha20-Poly1305. The host and UDP port come from the ticket.
        Console.WriteLine();
        Console.WriteLine("Sending UDP hello (request/response)...");
        using DatagramSession udpSession = new(tcpSession);
        await udpSession.ConnectAsync();

        HelloRequestPacket udpRequest = new();
        HelloResponsePacket udpResponse = await udpSession.RequestAsync<HelloResponsePacket>(
            udpRequest,
            RequestOptions.Default.WithTimeout(5_000));
        string udpMessage = udpResponse.Message switch
        {
            1 => "Hello from Nalix!",
            _ => $"Unknown (Message={udpResponse.Message})",
        };
        Console.WriteLine($"UDP replied: {udpMessage}");

        Console.WriteLine("TCP connection is still alive: " + tcpSession.IsConnected);

        // ── Step 5: Test WebSocket (optional) ────────────────────────────
        // WebSocket operates independently from the TCP session.
        // It establishes its own connection and does not share the TCP session token.
        Console.WriteLine();
        Console.WriteLine("Connecting WebSocket...");
        await TestWebSocketAsync();

        // ── Step 6: Cleanup ──────────────────────────────────────────────
        Console.WriteLine();
        await udpSession.DisconnectAsync();
        await tcpSession.DisconnectAsync();
        Console.WriteLine("Done.");
    }

    /// <summary>
    /// Tests WebSocket transport independently.
    /// WebSocket uses its own framing layer and does not depend on the TCP session state.
    /// </summary>
    private static async Task TestWebSocketAsync()
    {
        TransportOptions wsOptions = new()
        {
            Address = Host,
            Port = WebSocketPort,
        };

        WebSocketTransportOptions wsTransportOptions = new()
        {
            Path = "/ws",
        };

        using WebSocketSession wsSession = new(wsOptions, wsTransportOptions);

        try
        {
            await wsSession.ConnectAsync(Host, WebSocketPort);

            HelloRequestPacket wsRequest = new();
            HelloResponsePacket wsResponse = await wsSession.RequestAsync<HelloResponsePacket>(
                wsRequest,
                RequestOptions.Default.WithTimeout(5_000));

            string wsMessage = wsResponse.Message switch
            {
                1 => "Hello from Nalix!",
                _ => $"Unknown (Message={wsResponse.Message})",
            };
            Console.WriteLine($"WebSocket replied: {wsMessage}");

            await wsSession.DisconnectAsync();
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or IOException or Nalix.Abstractions.Exceptions.NetworkException)
        {
            Console.WriteLine($"WebSocket test skipped: {ex.Message}");
        }
    }
}
