// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Framework.Injection;
using Nalix.Abstractions.Networking;
using Nalix.Runtime.Groups;
using Nalix.Runtime.Sessions;
using Nalix.Hosting.Tests.TestSupport;
using Xunit;

namespace Nalix.Hosting.Tests.Audit;

/// <summary>
/// HOST-02 reproducers. <c>UseSessions()</c> and <c>UseGroups()</c> run at builder time and only create
/// their disconnect observers when an <see cref="IConnectionHub"/> is already registered. The default hub
/// is registered later, inside <c>PrepareCallbacks</c> during <c>ActivateAsync()</c>, and nothing revisits the
/// observer wiring afterwards. With the default hub the observers are never created.
/// </summary>
[Collection(TestCollections.RealServer)]
[Trait("Category", "Audit")]
public sealed class HOST02_DefaultHubObserverAuditTests : IDisposable
{
    public HOST02_DefaultHubObserverAuditTests()
    {
        TestAssemblySetup.EnsureHighLimits();
        if (!Nalix.Codec.DataFrames.PacketRegistry.IsBuilt)
        {
            Nalix.Codec.DataFrames.PacketRegistry.Build();
        }

        // Start from a clean singleton state so the builder sees no pre-registered hub.
        InstanceManager.Instance.Clear(dispose: false);
    }

    public void Dispose() => InstanceManager.Instance.Clear(dispose: false);

    // Control: with a hub supplied up front, the observer is wired at builder time and is present.
    [Fact]
    public async Task Control_UseGroups_WithCustomHub_WiresMembershipObserver()
    {
        int port = TestUtils.GetFreePort();

        var builder = NetworkApplication.CreateBuilder();
        builder.ListenTcp<TestUtils.IntegrationTestProtocol>().OnPort((ushort)port);
        builder.UseConnectionHub(new Nalix.Network.Connections.ConnectionHub());
        builder.UseGroups();

        using NetworkApplication app = builder.Build();
        await app.ActivateAsync().WaitAsync(TestUtils.Timeout);

        try
        {
            Assert.NotNull(InstanceManager.Instance.GetExistingInstance<GroupMembershipObserver>());
        }
        finally
        {
            await app.DeactivateAsync().WaitAsync(TestUtils.Timeout);
        }
    }

    // HOST-02a: with the default hub, UseGroups() must still wire the membership observer once the host is
    // active. Currently the observer is never created, so disconnected connections keep their group memberships.
    [Fact]
    public async Task HOST02a_UseGroups_WithDefaultHub_WiresMembershipObserverOnActivation()
    {
        int port = TestUtils.GetFreePort();

        var builder = NetworkApplication.CreateBuilder();
        builder.ListenTcp<TestUtils.IntegrationTestProtocol>().OnPort((ushort)port);
        builder.UseGroups();

        using NetworkApplication app = builder.Build();
        await app.ActivateAsync().WaitAsync(TestUtils.Timeout);

        try
        {
            Assert.NotNull(InstanceManager.Instance.GetExistingInstance<GroupMembershipObserver>());
        }
        finally
        {
            await app.DeactivateAsync().WaitAsync(TestUtils.Timeout);
        }
    }

    // HOST-02b: same defect for session persistence. With the default hub, sessions are never saved on
    // disconnect, so a later resume cannot find them.
    [Fact]
    public async Task HOST02b_UseSessions_WithDefaultHub_WiresPersistenceObserverOnActivation()
    {
        int port = TestUtils.GetFreePort();

        var builder = NetworkApplication.CreateBuilder();
        builder.ListenTcp<TestUtils.IntegrationTestProtocol>().OnPort((ushort)port);
        builder.UseSessions();

        using NetworkApplication app = builder.Build();
        await app.ActivateAsync().WaitAsync(TestUtils.Timeout);

        try
        {
            Assert.NotNull(InstanceManager.Instance.GetExistingInstance<SessionPersistenceObserver>());
        }
        finally
        {
            await app.DeactivateAsync().WaitAsync(TestUtils.Timeout);
        }
    }
}
