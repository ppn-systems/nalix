// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

// Entry-point code: ConfigureAwait is not required in top-level application code.
#pragma warning disable CA2007
// Sample uses inline literal strings for clarity.
#pragma warning disable CA1303

using Microsoft.Extensions.Logging;
using Nalix.Hosting;
using Nalix.Hosting.Protocols;

namespace ChatRoom.Server;

/// <summary>
/// Entry point for the ChatRoom server.
/// Starts a TCP listener on port 57207 and broadcasts messages between clients.
/// </summary>
internal static class Program
{
    private const ushort Port = 57207;

    public static async Task Main()
    {
        // Create a console logger for visibility.
        using ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
        {
            _ = builder.SetMinimumLevel(LogLevel.Information)
                       .AddConsole();
        });

        ILogger logger = loggerFactory.CreateLogger("ChatRoom");

        // Build the network application using the canonical Hosting API.
        // MapHandlers(Type) is used because ChatHandlers is a static class.
        await using NetworkApplication app = NetworkApplication.CreateBuilder()
            .UseLogger(logger)
            .MapHandlers(typeof(ChatHandlers))
            .MapTcp<DefaultProtocol>().OnPort(Port).Bind()
            .Build();

        // Graceful shutdown on Ctrl+C.
        using CancellationTokenSource cts = new();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        Console.WriteLine($"ChatRoom server is running on 127.0.0.1:{Port}.");
        Console.WriteLine("Press Ctrl+C to stop.");

        await app.RunAsync(cts.Token);
    }
}
