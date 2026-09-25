// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

// Entry-point code: ConfigureAwait is not required in top-level application code.
#pragma warning disable CA2007
// Sample uses inline literal strings for clarity.
#pragma warning disable CA1303

using Microsoft.Extensions.Logging;
using Nalix.Hosting;
using Nalix.Hosting.Protocols;

namespace HelloWorld.Server;

/// <summary>
/// Entry point for the HelloWorld server.
/// Starts a TCP listener on port 57206 and waits for client requests.
/// </summary>
internal static class Program
{
    private const ushort Port = 57206;

    public static async Task Main()
    {
        // Create a console logger for visibility.
        using ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
        {
            _ = builder.SetMinimumLevel(LogLevel.Information)
                       .AddConsole();
        });

        ILogger logger = loggerFactory.CreateLogger("HelloWorld");

        // Build the network application using the canonical Hosting API.
        // MapHandlers(Type) is used because HelloHandlers is a static class.
        await using NetworkApplication app = NetworkApplication.CreateBuilder()
            .UseLogger(logger)
            .MapHandlers(typeof(HelloHandlers))
            .MapTcp<DefaultProtocol>().OnPort(Port).Bind()
            .Build();

        // Graceful shutdown on Ctrl+C.
        using CancellationTokenSource cts = new();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        Console.WriteLine($"HelloWorld server is running on 127.0.0.1:{Port}.");
        Console.WriteLine("Press Ctrl+C to stop.");

        await app.RunAsync(cts.Token);
    }
}
