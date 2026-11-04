// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using Nalix.Abstractions;
using Nalix.Abstractions.Diagnostics;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Codec.DataFrames;
using Nalix.Framework;
using Nalix.Observability.Contracts;
using Nalix.Observability.Internal;
using Nalix.Observability.Internal.History;
using Nalix.Observability.Tests.TestSupport;
using NSubstitute;

namespace Nalix.Observability.Tests;

/// <summary>
/// Verifies the chunks and payloads returned by <see cref="RuntimeObservationHandlers"/>.
/// </summary>
[Collection(TestCollections.ObservabilityState)]
public sealed class RuntimeObservationHandlersTests
{
    [Fact]
    public async Task HandleAsync_WhenNoTargetsRequested_ReturnsMalformedPacket()
    {
        using RuntimeObservation request = PacketBase<RuntimeObservation>.Create();
        request.Initialize(RuntimeObservationStage.REQUEST, RuntimeObservationTargets.NONE);

        List<RuntimeObservation> chunks = await ObservationChunks.CollectAsync(RuntimeObservationHandlers.HandleAsync(CreateContext(request)));
        try
        {
            RuntimeObservation response = Assert.Single(chunks);
            Assert.Equal(RuntimeObservationStage.RESPONSE, response.Stage);
            Assert.Equal(ProtocolReason.MALFORMED_PACKET, response.Reason);
            Assert.True(response.ObservationData.IsEmpty);
        }
        finally
        {
            ObservationChunks.DisposeAll(chunks);
        }
    }

    [Fact]
    public async Task HandleAsync_WhenOnlyUnregisteredTargetsRequested_ReturnsNotFound()
    {
        _ = ReportRegistry.Instance.Unregister<IReportable>(CoreTelemetryTarget.TokenBucketLimiter);
        using RuntimeObservation request = PacketBase<RuntimeObservation>.Create();
        request.Initialize(RuntimeObservationStage.REQUEST, RuntimeObservationTargets.TOKEN_BUCKET_LIMITER);

        List<RuntimeObservation> chunks = await ObservationChunks.CollectAsync(RuntimeObservationHandlers.HandleAsync(CreateContext(request)));
        try
        {
            RuntimeObservation response = Assert.Single(chunks);
            Assert.Equal(RuntimeObservationStage.RESPONSE, response.Stage);
            Assert.Equal(ProtocolReason.NOT_FOUND, response.Reason);
            Assert.True(response.SampledAt > 0);
            Assert.True(response.ObservationData.IsEmpty);
        }
        finally
        {
            ObservationChunks.DisposeAll(chunks);
        }
    }

    [Fact]
    public async Task HandleAsync_WhenProcessRequested_ReturnsSampledPayload()
    {
        using RuntimeObservation request = PacketBase<RuntimeObservation>.Create();
        request.Initialize(RuntimeObservationStage.REQUEST, RuntimeObservationTargets.PROCESS);

        List<RuntimeObservation> chunks = await ObservationChunks.CollectAsync(RuntimeObservationHandlers.HandleAsync(CreateContext(request)));
        try
        {
            RuntimeObservation response = Assert.Single(chunks);
            Assert.Equal(ProtocolReason.NONE, response.Reason);
            Assert.Equal(RuntimeObservationTargets.PROCESS, response.Targets);
            Assert.True(response.SampledAt > 0);
            Assert.False(response.ObservationData.IsEmpty);

            using JsonDocument document = JsonDocument.Parse(response.ObservationData);
            Assert.Equal(JsonValueKind.Object, document.RootElement.GetProperty(RuntimeObservationSections.Process).ValueKind);
        }
        finally
        {
            ObservationChunks.DisposeAll(chunks);
        }
    }

    [Fact]
    public async Task HandleAsync_WhenSnapshotExceedsBudget_StreamsChunksWithRequestSequenceId()
    {
        const ushort SequenceId = 7;
        RuntimeObservationTargets requested = RuntimeObservationTargets.CONCURRENCY_GATE | RuntimeObservationTargets.TOKEN_BUCKET_LIMITER;
        int originalBudget = ObservabilityLimits.PayloadBudget;
        ReportRegistry.Instance.Register<IReportable>(CoreTelemetryTarget.ConcurrencyGate, new ArrayReportable(items: 40, itemChars: 100));
        ReportRegistry.Instance.Register<IReportable>(CoreTelemetryTarget.TokenBucketLimiter, new ArrayReportable(items: 40, itemChars: 100));
        try
        {
            ObservabilityLimits.PayloadBudget = ObservabilityLimits.MinPayloadBytes;
            using RuntimeObservation request = PacketBase<RuntimeObservation>.Create();
            request.Initialize(RuntimeObservationStage.REQUEST, requested);
            request.SequenceId = SequenceId;

            List<RuntimeObservation> chunks = await ObservationChunks.CollectAsync(RuntimeObservationHandlers.HandleAsync(CreateContext(request)));
            try
            {
                Assert.True(chunks.Count > 1, $"expected several chunks, got {chunks.Count}");
                RuntimeObservationTargets union = RuntimeObservationTargets.NONE;
                foreach (RuntimeObservation chunk in chunks)
                {
                    Assert.Equal(RuntimeObservationStage.RESPONSE, chunk.Stage);
                    Assert.Equal(SequenceId, chunk.SequenceId);
                    Assert.True(chunk.ObservationData.Length <= ObservabilityLimits.MinPayloadBytes);
                    union |= chunk.Targets;
                }

                Assert.Equal(requested, union);
            }
            finally
            {
                ObservationChunks.DisposeAll(chunks);
            }
        }
        finally
        {
            ObservabilityLimits.PayloadBudget = originalBudget;
            _ = ReportRegistry.Instance.Unregister<IReportable>(CoreTelemetryTarget.ConcurrencyGate);
            _ = ReportRegistry.Instance.Unregister<IReportable>(CoreTelemetryTarget.TokenBucketLimiter);
        }
    }

    [Fact]
    public async Task HandleAsync_WhenHistoryDisabled_ReturnsOperationUnsupported()
    {
        HistoryRecorder.Current?.Dispose();
        using RuntimeObservation request = HistoryRequest("""{"from":1,"to":2}""");

        List<RuntimeObservation> chunks = await ObservationChunks.CollectAsync(RuntimeObservationHandlers.HandleAsync(CreateContext(request)));
        try
        {
            RuntimeObservation response = Assert.Single(chunks);
            Assert.Equal(RuntimeObservationStage.HISTORY_RESPONSE, response.Stage);
            Assert.Equal(ProtocolReason.OPERATION_UNSUPPORTED, response.Reason);
        }
        finally
        {
            ObservationChunks.DisposeAll(chunks);
        }
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
            List<RuntimeObservation> rejectedChunks = await ObservationChunks.CollectAsync(RuntimeObservationHandlers.HandleAsync(CreateContext(malformed)));
            try
            {
                Assert.Equal(ProtocolReason.MALFORMED_PACKET, Assert.Single(rejectedChunks).Reason);
            }
            finally
            {
                ObservationChunks.DisposeAll(rejectedChunks);
            }

            using RuntimeObservation catalogRequest = HistoryRequest($$"""{"from":{{now - 1000}},"to":{{now + 1000}}}""");
            List<RuntimeObservation> catalogChunks = await ObservationChunks.CollectAsync(RuntimeObservationHandlers.HandleAsync(CreateContext(catalogRequest)));
            try
            {
                RuntimeObservation catalog = Assert.Single(catalogChunks);
                using JsonDocument json = JsonDocument.Parse(catalog.ObservationData);
                Assert.Equal(ProtocolReason.NONE, catalog.Reason);
                Assert.Single(json.RootElement.GetProperty(RuntimeObservationHistory.Days).EnumerateArray());
            }
            finally
            {
                ObservationChunks.DisposeAll(catalogChunks);
            }

            using RuntimeObservation seriesRequest = HistoryRequest($$"""{"from":{{now - 1000}},"to":{{now + 1000}},"points":4,"metrics":["Process.WorkingSetBytes"]}""");
            List<RuntimeObservation> seriesChunks = await ObservationChunks.CollectAsync(RuntimeObservationHandlers.HandleAsync(CreateContext(seriesRequest)));
            try
            {
                RuntimeObservation series = Assert.Single(seriesChunks);
                using JsonDocument json = JsonDocument.Parse(series.ObservationData);
                JsonElement avg = json.RootElement.GetProperty(RuntimeObservationHistory.Series).GetProperty("Process.WorkingSetBytes").GetProperty(RuntimeObservationHistory.Average);
                Assert.Contains(avg.EnumerateArray(), v => v.ValueKind == JsonValueKind.Number && v.GetDouble() > 0);
            }
            finally
            {
                ObservationChunks.DisposeAll(seriesChunks);
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
