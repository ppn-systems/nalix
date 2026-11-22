// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

// Entry-point code: ConfigureAwait is not required in top-level application code.
#pragma warning disable CA2007
// Packet created with new() is short-lived; GC handles cleanup.
#pragma warning disable CA2000
// Sample uses inline literal strings for clarity.
#pragma warning disable CA1303

using ChatRoom.Contracts;
using Nalix.SDK.Options;
using Nalix.SDK.Transport;
using Nalix.SDK.Transport.Extensions;

namespace ChatRoom.Client;

/// <summary>
/// Entry point for the ChatRoom client.
/// Connects to the server, subscribes to incoming chat messages,
/// and sends user-typed messages to the chat room.
/// </summary>
internal static class Program
{
    private const string Host = "127.0.0.1";
    private const ushort Port = 57207;

    public static async Task Main()
    {
        // Prompt for a username.
        Console.Write("Enter username: ");
        string? username = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(username))
        {
            username = "anon";
        }

        username = username.Trim();

        // Configure transport options for the local server.
        TransportOptions options = new()
        {
            Address = Host,
            Port = Port,
        };

        // Create a TCP session.
        TcpSession session = new(options);

        // Subscribe to incoming ChatMessagePacket messages before connecting
        // so that no push from the server is ever missed.
        IDisposable subscription = session.On<ChatMessagePacket>(packet =>
        {
            Console.WriteLine($"{packet.Username}: {packet.Message}");
        });

        await session.ConnectAsync(Host, Port);

        Console.WriteLine($"Connected to {Host}:{Port}.");
        Console.WriteLine("Type a message and press Enter. Type /exit to quit.");

        // Main loop: read messages from the console and send them.
        while (true)
        {
            string? line = Console.ReadLine();

            if (line is null)
            {
                // stdin was closed (e.g., piped input ended).
                break;
            }

            if (string.Equals(line, "/exit", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            // Build and send the chat message.
            ChatMessagePacket packet = new()
            {
                Username = username,
                Message = line.Trim(),
            };

            await session.SendAsync(packet, CancellationToken.None).ConfigureAwait(false);
        }

        // Clean up: unsubscribe, disconnect, and dispose.
        subscription.Dispose();
        await session.DisconnectAsync().ConfigureAwait(false);
        await session.DisposeAsync().ConfigureAwait(false);

        Console.WriteLine("Disconnected.");
    }
}
