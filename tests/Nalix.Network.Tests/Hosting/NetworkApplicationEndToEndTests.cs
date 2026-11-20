// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Nalix.Abstractions;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Hosting;
using Nalix.Hosting.Protocols;
using Nalix.Network.Protocols;
using Nalix.Network.Tests.TestSupport;
using Nalix.Runtime.Dispatching;
using Nalix.SDK.Options;
using Nalix.SDK.Transport;
using Nalix.SDK.Transport.Extensions;
using Nalix.Tests.Shared;

namespace Nalix.Network.Tests.Hosting;

public sealed class NetworkApplicationEndToEndTests
{
    [Fact]
    public void Serialize_WithHeaderOpCode_WritesOpCodeAtOffsetZero()
    {
        // 1. Create packet
        using var pkt = new HostingScanAttributedPacket { Value = 42 };
        var h = pkt.Header;
        h.OpCode = 0x1234;
        pkt.Header = h;

        // 2. Serialize
        byte[] bytes = pkt.Serialize();

        // 3. Verify
        // Offset 0: OpCode (2 bytes)
        ushort readOpCode = BitConverter.ToUInt16(bytes, 0);

        Assert.Equal(pkt.Header.OpCode, readOpCode);
    }

    [Fact]
    public async Task Activate_WithTcpClientSendingPacket_ProcessesPacket()
    {
        IntegrationTestController.ReceivedCount = 0;
        IntegrationTestProtocol.ProcessedCount = 0;

        // 0. Resolve certificate path (using the sample key provided in shared)
        string certPath = Path.GetFullPath(@"..\..\..\shared\certificate.private");
        if (!File.Exists(certPath))
        {
            certPath = Path.GetFullPath(@"shared\certificate.private");
        }

        if (!File.Exists(certPath))
        {
            certPath = Path.GetFullPath("test_certificate.private");
            File.WriteAllText(certPath, new string('a', 64));
        }

        // Find a random available port
        int port = GetFreePort();

        // 1. Setup Server
        var logger = NullLogger.Instance;

        NetworkApplicationBuilder builder = NetworkApplication.CreateBuilder();
        builder.UseLogger(logger);
        builder.UseSecureConnections(certPath);

        // Listen on loopback with our test protocol
        builder.MapTcp<IntegrationTestProtocol>().OnPort((ushort)port);

        builder.MapHandlers<IntegrationTestController>();

        using NetworkApplication app = builder.Build();

        // Start Server
        await app.ActivateAsync();

        try
        {
            // 2. Setup Client
            var options = new TransportOptions
            {
                Address = "127.0.0.1",
                Port = (ushort)port,
                CompressionEnabled = false
            };
            using TcpSession client = new(options);

            await client.ConnectAsync();

            // 3. Send Packet
            using HostingScanAttributedPacket pkt = new() { Value = 42 };
            var h2 = pkt.Header;
            h2.OpCode = 0x1234;
            pkt.Header = h2;

            // Disambiguate SendAsync by specifying CancellationToken
            await client.SendAsync(pkt, ct: default);

            // 4. Verify (with polling for processing)
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                while (IntegrationTestProtocol.ProcessedCount < 1 && !cts.IsCancellationRequested)
                {
                    await Task.Delay(100, cts.Token);
                }
            }
            catch (TaskCanceledException) { }

            Assert.Equal(1, IntegrationTestProtocol.ProcessedCount);

            await client.DisconnectAsync();
        }
        finally
        {
            await app.DeactivateAsync();
        }
    }

    private static int GetFreePort() => TestPorts.Next();
}
