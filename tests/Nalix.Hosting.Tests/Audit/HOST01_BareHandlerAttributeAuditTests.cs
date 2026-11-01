// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Threading;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Security;
using Nalix.Abstractions.Injection;
using Nalix.Hosting.Tests.TestSupport;
using Xunit;

namespace Nalix.Hosting.Tests.Audit;

/// <summary>
/// HOST-01 reproducer. The packet-handler generator (PacketHandlerGenerator.cs) only forwards
/// [PacketPermission] when the attribute has constructor arguments (<c>ConstructorArguments.Length &gt; 0</c>).
/// <c>PacketPermissionAttribute()</c> is parameterless, so a bare <c>[PacketPermission]</c> is dropped, the
/// generated metadata gets a null Permission, and <c>PacketHandler.CanExecute</c> skips the check (fail-open).
/// <para>
/// The encryption cases are controls, not findings: <c>PacketEncryptionAttribute</c> has optional parameters,
/// which the compiler materialises in <c>ConstructorArguments</c>, so a bare <c>[PacketEncryption]</c> is forwarded
/// and enforced. They guard against regressions in the same generator path.
/// </para>
/// </summary>
[Collection(TestCollections.RealServer)]
[Trait("Category", "Audit")]
public sealed class HOST01_BareHandlerAttributeAuditTests : IDisposable
{
    public HOST01_BareHandlerAttributeAuditTests()
    {
        TestAssemblySetup.EnsureHighLimits();
        if (!Nalix.Codec.DataFrames.PacketRegistry.IsBuilt)
        {
            Nalix.Codec.DataFrames.PacketRegistry.Build();
        }
    }

    public void Dispose() => Nalix.Framework.Injection.InstanceManager.Instance.Clear(dispose: false);

    [PacketHandler("AuditOpenPositive")]
    [Injectable]
    public sealed class OpenPositiveHandler
    {
        public static int ReceivedCount;

        // Positive control: NONE is admitted for a NONE-level client, so the probe must reach this handler.
        [PacketPermission(PermissionLevel.NONE)]
        [PacketOpcode(8901)]
        public void Handle(IPacketContext<AuditProbePacket> context) => Interlocked.Increment(ref ReceivedCount);
    }

    // Harness control: if the probe is not delivered at all, the zero-count assertions below prove nothing.
    [Fact]
    public async Task Control_Probe_IsDelivered_ToAdmittedHandler()
    {
        OpenPositiveHandler.ReceivedCount = 0;
        int count = await AuditDispatch.SendProbeAsync<OpenPositiveHandler>(
            8901, () => OpenPositiveHandler.ReceivedCount);

        Assert.Equal(1, count);
    }

    [PacketHandler("AuditBarePermission")]
    [Injectable]
    public sealed class BarePermissionHandler
    {
        public static int ReceivedCount;

        // Bare attribute: the author intends the default USER minimum level.
        [PacketPermission]
        [PacketOpcode(8901)]
        public void Handle(IPacketContext<AuditProbePacket> context) => Interlocked.Increment(ref ReceivedCount);
    }

    [PacketHandler("AuditExplicitPermission")]
    [Injectable]
    public sealed class ExplicitPermissionHandler
    {
        public static int ReceivedCount;

        [PacketPermission(PermissionLevel.USER)]
        [PacketOpcode(8901)]
        public void Handle(IPacketContext<AuditProbePacket> context) => Interlocked.Increment(ref ReceivedCount);
    }

    [PacketHandler("AuditBareEncryption")]
    [Injectable]
    public sealed class BareEncryptionHandler
    {
        public static int ReceivedCount;

        // Bare attribute: the author intends [PacketEncryption(true)] - plaintext must be refused.
        [PacketEncryption]
        [PacketOpcode(8901)]
        public void Handle(IPacketContext<AuditProbePacket> context) => Interlocked.Increment(ref ReceivedCount);
    }

    [PacketHandler("AuditExplicitEncryption")]
    [Injectable]
    public sealed class ExplicitEncryptionHandler
    {
        public static int ReceivedCount;

        [PacketEncryption(true)]
        [PacketOpcode(8901)]
        public void Handle(IPacketContext<AuditProbePacket> context) => Interlocked.Increment(ref ReceivedCount);
    }

    // Control: an explicit permission gate must deny an unauthenticated (NONE-level) client.
    // Passing proves the probe harness can observe a denial; it is not a finding.
    [Fact]
    public async Task Control_ExplicitPermission_DeniesUnauthenticatedClient()
    {
        ExplicitPermissionHandler.ReceivedCount = 0;
        int count = await AuditDispatch.SendProbeAsync<ExplicitPermissionHandler>(
            8901, () => ExplicitPermissionHandler.ReceivedCount);

        Assert.Equal(0, count);
    }

    // HOST-01a: a bare [PacketPermission] must still deny an unauthenticated client. Currently it does not.
    [Fact]
    public async Task HOST01a_BarePacketPermission_DeniesUnauthenticatedClient()
    {
        BarePermissionHandler.ReceivedCount = 0;
        int count = await AuditDispatch.SendProbeAsync<BarePermissionHandler>(
            8901, () => BarePermissionHandler.ReceivedCount);

        Assert.Equal(0, count);
    }

    // Control: an explicit encryption requirement must refuse a plaintext frame.
    [Fact]
    public async Task Control_ExplicitEncryption_RefusesPlaintextFrame()
    {
        ExplicitEncryptionHandler.ReceivedCount = 0;
        int count = await AuditDispatch.SendProbeAsync<ExplicitEncryptionHandler>(
            8901, () => ExplicitEncryptionHandler.ReceivedCount);

        Assert.Equal(0, count);
    }

    // Control (not a finding): a bare [PacketEncryption] is forwarded by the generator and refuses plaintext.
    [Fact]
    public async Task Control_BarePacketEncryption_RefusesPlaintextFrame()
    {
        BareEncryptionHandler.ReceivedCount = 0;
        int count = await AuditDispatch.SendProbeAsync<BareEncryptionHandler>(
            8901, () => BareEncryptionHandler.ReceivedCount);

        Assert.Equal(0, count);
    }
}
