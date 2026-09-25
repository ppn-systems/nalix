// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

// Entry-point code: ConfigureAwait is not required in top-level application code.
#pragma warning disable CA2007
// Packet created with new() is short-lived; GC handles cleanup.
#pragma warning disable CA2000

using HelloWorld.Contracts;
using Nalix.SDK.Options;
using Nalix.SDK.Transport;
using Nalix.SDK.Transport.Extensions;

namespace HelloWorld.Client;

/// <summary>
/// Entry point for the HelloWorld client.
/// Connects to the server, sends a request, and prints the response.
/// </summary>
internal static class Program
{
    private const string Host = "127.0.0.1";
    private const ushort Port = 57206;

    public static async Task Main()
    {
        // Configure transport options for the local server.
        TransportOptions options = new()
        {
            Address = Host,
            Port = Port,
        };

        // Create and connect a TCP session.
        using TcpSession session = new(options);
        await session.ConnectAsync();

        Console.WriteLine($"Connected to {Host}:{Port}.");

        // Build the request packet.
        HelloRequestPacket request = new();

        // Send the request and wait for the typed response.
        // RequestAsync subscribes before sending, so no response is ever missed.
        HelloResponsePacket response = await session.RequestAsync<HelloResponsePacket>(
            request,
            RequestOptions.Default.WithTimeout(5_000));

        // Print the result.
        string message = response.Message switch
        {
            1 => "Hello from Nalix!",
            _ => $"Unknown (Message={response.Message})",
        };

        Console.WriteLine($"Server replied: {message}");

        // Cleanly disconnect.
        await session.DisconnectAsync();
    }
}
