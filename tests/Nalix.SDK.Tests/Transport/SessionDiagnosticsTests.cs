// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Linq;
using System.Threading.Tasks;
using Nalix.Codec.DataFrames;
using Nalix.Hosting;
using Nalix.SDK.Options;
using Nalix.SDK.Tests.TestSupport;
using Nalix.SDK.Transport;
using Xunit;

namespace Nalix.SDK.Tests.Transport;

/// <summary>
/// The session lifecycle is published on the SDK's <see cref="System.Diagnostics.DiagnosticListener"/> as
/// <see cref="DiagnosticsEvents.Session"/> events with a <c>DiagnosticLog</c> payload.
/// </summary>
[Collection(TestCollections.RealServer)]
public sealed class SessionDiagnosticsTests : IDisposable
{
    public SessionDiagnosticsTests()
    {
        TestAssemblySetup.EnsureHighLimits();
        if (!PacketRegistry.IsBuilt)
        {
            PacketRegistry.Build();
        }

        TestEnvironment.SetupCertificate();
    }

    public void Dispose() => Nalix.Framework.Injection.InstanceManager.Instance.Clear(dispose: false);

    [Fact]
    public async Task TcpSession_ConnectAndDispose_EmitsSessionEventsInOrder()
    {
        int port = TestEnvironment.GetFreePort();
        NetworkApplicationBuilder builder = NetworkApplication.CreateBuilder();
        builder.ListenTcp<IntegrationTestProtocol>().OnPort((ushort)port);
        using NetworkApplication app = builder.Build();
        await app.ActivateAsync();

        try
        {
            using DiagnosticCapture capture = new("Session.");

            TcpSession session = new(new TransportOptions { Address = "127.0.0.1", Port = (ushort)port });
            await session.ConnectAsync();
            await session.DisposeAsync();

            // Other tests run in parallel and share the listener: keep only this session's endpoint.
            string endpoint = $"port={port}";
            (string Name, Nalix.Abstractions.Diagnostics.DiagnosticLog Log)[] mine =
                [.. capture.Events.Where(e => e.Log.Message.Contains(endpoint, StringComparison.Ordinal) || e.Name == DiagnosticsEvents.Session.Disconnected)];

            Assert.Equal(DiagnosticsEvents.Session.Connecting, mine[0].Name);
            Assert.Equal(DiagnosticsEvents.Session.Connected, mine[1].Name);
            Assert.Equal("SDK.TcpSession:Connect", mine[1].Log.Tag);
            Assert.Equal($"connected host=127.0.0.1 port={port}", mine[1].Log.Message);
            Assert.Contains(mine, e => e.Name == DiagnosticsEvents.Session.Disconnected && e.Log.Tag == "SDK.TcpSession:Disconnect");
        }
        finally
        {
            await app.DeactivateAsync();
        }
    }
}
