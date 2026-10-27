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
using Nalix.Observability.Internal.History;
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

    [Fact]
    public async Task HandleAsync_WhenHistoryDisabled_ReturnsOperationUnsupported()
    {
        HistoryRecorder.Current?.Dispose();
        using RuntimeObservation request = HistoryRequest("""{"from":1,"to":2}""");

        using RuntimeObservation response = await RuntimeObservationHandlers.HandleAsync(CreateContext(request));

        Assert.Equal(RuntimeObservationStage.HISTORY_RESPONSE, response.Stage);
        Assert.Equal(ProtocolReason.OPERATION_UNSUPPORTED, response.Reason);
    }

    [Fact]
    public async Task HandleAsync_WhenHistoryQueried_ReturnsCatalogThenSeries()
    {
        string directory = Path.Combine(Path.GetTempPath(), "nxh-" + Path.GetRandomFileName());
        HistoryRecorder.Start(new ObservabilityOptions { HistoryDirectory = directory, HistoryTargets = RuntimeObservationTargets.PROCESS });
        try
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            HistoryRecorder.Current!.Sample(now);

            using RuntimeObservation malformed = HistoryRequest("""{"from":5,"to":1}""");
            using RuntimeObservation rejected = await RuntimeObservationHandlers.HandleAsync(CreateContext(malformed));
            Assert.Equal(ProtocolReason.MALFORMED_PACKET, rejected.Reason);

            using RuntimeObservation catalogRequest = HistoryRequest($$"""{"from":{{now - 1000}},"to":{{now + 1000}}}""");
            using RuntimeObservation catalog = await RuntimeObservationHandlers.HandleAsync(CreateContext(catalogRequest));
            using (JsonDocument json = JsonDocument.Parse(catalog.ObservationData))
            {
                Assert.Equal(ProtocolReason.NONE, catalog.Reason);
                Assert.Single(json.RootElement.GetProperty(RuntimeObservationHistory.Days).EnumerateArray());
            }

            using RuntimeObservation seriesRequest = HistoryRequest($$"""{"from":{{now - 1000}},"to":{{now + 1000}},"points":4,"metrics":["Process.WorkingSetBytes"]}""");
            using RuntimeObservation series = await RuntimeObservationHandlers.HandleAsync(CreateContext(seriesRequest));
            using (JsonDocument json = JsonDocument.Parse(series.ObservationData))
            {
                JsonElement avg = json.RootElement.GetProperty(RuntimeObservationHistory.Series).GetProperty("Process.WorkingSetBytes").GetProperty(RuntimeObservationHistory.Average);
                Assert.Contains(avg.EnumerateArray(), v => v.ValueKind == JsonValueKind.Number && v.GetDouble() > 0);
            }
        }
        finally
        {
            HistoryRecorder.Current?.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static RuntimeObservation HistoryRequest(string json)
    {
        RuntimeObservation request = PacketBase<RuntimeObservation>.Create();
        request.Initialize(RuntimeObservationStage.HISTORY_REQUEST, RuntimeObservationTargets.NONE, observationData: System.Text.Encoding.UTF8.GetBytes(json));
        return request;
    }

    private static IPacketContext<RuntimeObservation> CreateContext(RuntimeObservation packet)
    {
        IPacketContext<RuntimeObservation> context = Substitute.For<IPacketContext<RuntimeObservation>>();
        context.Packet.Returns(packet);
        return context;
    }
}
