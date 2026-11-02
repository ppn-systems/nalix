// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Hosting.Tests.TestSupport;
using Nalix.SDK.Options;
using Nalix.SDK.Transport;

namespace Nalix.Hosting.Tests.Audit;

/// <summary>
/// Sends one plaintext, unauthenticated probe packet to a freshly built host and reports how many times
/// the registered handler ran. Used by audit reproducers to observe the dispatch-level decision.
/// </summary>
internal static class AuditDispatch
{
    internal static async Task<int> SendProbeAsync<THandler>(ushort opcode, Func<int> readCount)
        where THandler : class
    {
        int port = TestUtils.GetFreePort();

        var builder = NetworkApplication.CreateBuilder();
        builder.ListenTcp<TestUtils.IntegrationTestProtocol>().OnPort((ushort)port);
        builder.MapHandlers<THandler>();

        using NetworkApplication app = builder.Build();
        await app.ActivateAsync().WaitAsync(TestUtils.Timeout);

        try
        {
            using TcpSession client = new(new TransportOptions { Address = "127.0.0.1", Port = (ushort)port });
            await client.ConnectAsync().WaitAsync(TestUtils.Timeout);

            using AuditProbePacket packet = new() { Value = 1 };
            var header = packet.Header;
            header.OpCode = opcode;
            packet.Header = header;

            await client.SendAsync(packet, ct: default);

            // Dispatch is asynchronous; give the handler a generous window to run if it is going to.
            await Task.Delay(1500);
            return readCount();
        }
        finally
        {
            await app.DeactivateAsync().WaitAsync(TestUtils.Timeout);
        }
    }
}
