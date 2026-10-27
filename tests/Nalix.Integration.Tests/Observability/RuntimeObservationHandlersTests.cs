// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using Nalix.Abstractions;
using Nalix.Abstractions.Diagnostics;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Codec.DataFrames;
using Nalix.Framework;
using Nalix.Integration.Tests.TestSupport;
using Nalix.Observability;
using Nalix.Observability.Contracts;
using NSubstitute;

namespace Nalix.Integration.Tests.Observability;

/// <summary>
/// Verifies the reason and payload returned by <see cref="RuntimeObservationHandlers"/>.
/// </summary>
[Collection(TestCollections.ObservabilityState)]
public sealed class RuntimeObservationHandlersTests
{
    [Fact]
    public async Task HandleAsync_WhenNoTargetsRequested_ReturnsMalformedPacket()
    {
        using RuntimeObservation request = PacketBase<RuntimeObservation>.Create();
        request.Initialize(RuntimeObservationStage.REQUEST, RuntimeObservationTargets.NONE);

        using RuntimeObservation response = await RuntimeObservationHandlers.HandleAsync(CreateContext(request));

        Assert.Equal(RuntimeObservationStage.RESPONSE, response.Stage);
        Assert.Equal(ProtocolReason.MALFORMED_PACKET, response.Reason);
        Assert.True(response.ObservationData.IsEmpty);
    }

    [Fact]
    public async Task HandleAsync_WhenOnlyUnregisteredTargetsRequested_ReturnsNotFound()
    {
        _ = ReportRegistry.Instance.Unregister<IReportable>(CoreTelemetryTarget.TokenBucketLimiter);
        using RuntimeObservation request = PacketBase<RuntimeObservation>.Create();
        request.Initialize(RuntimeObservationStage.REQUEST, RuntimeObservationTargets.TOKEN_BUCKET_LIMITER);

        using RuntimeObservation response = await RuntimeObservationHandlers.HandleAsync(CreateContext(request));

        Assert.Equal(RuntimeObservationStage.RESPONSE, response.Stage);
        Assert.Equal(ProtocolReason.NOT_FOUND, response.Reason);
        Assert.True(response.SampledAt > 0);
        Assert.True(response.ObservationData.IsEmpty);
    }

    [Fact]
    public async Task HandleAsync_WhenProcessRequested_ReturnsSampledPayload()
    {
        using RuntimeObservation request = PacketBase<RuntimeObservation>.Create();
        request.Initialize(RuntimeObservationStage.REQUEST, RuntimeObservationTargets.PROCESS);

        using RuntimeObservation response = await RuntimeObservationHandlers.HandleAsync(CreateContext(request));

        Assert.Equal(ProtocolReason.NONE, response.Reason);
        Assert.Equal(RuntimeObservationTargets.PROCESS, response.Targets);
        Assert.True(response.SampledAt > 0);
        Assert.False(response.ObservationData.IsEmpty);

        using JsonDocument document = JsonDocument.Parse(response.ObservationData);
        Assert.Equal(JsonValueKind.Object, document.RootElement.GetProperty(RuntimeObservationSections.Process).ValueKind);
    }

    private static IPacketContext<RuntimeObservation> CreateContext(RuntimeObservation packet)
    {
        IPacketContext<RuntimeObservation> context = Substitute.For<IPacketContext<RuntimeObservation>>();
        context.Packet.Returns(packet);
        return context;
    }
}
