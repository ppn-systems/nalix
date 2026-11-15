// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text;
using System.Text.Json;
using Nalix.Abstractions;
using Nalix.Abstractions.Diagnostics;
using Nalix.Environment.Memory;
using Nalix.Framework;
using Nalix.Observability.Contracts;
using Nalix.Observability.Internal;
using Nalix.Observability.Tests.TestSupport;

namespace Nalix.Observability.Tests.Internal;

/// <summary>
/// Verifies how <see cref="ObservationWriter.WriteChunks"/> packs sections into budget-sized chunks, trims or omits
/// sections that do not fit, and keeps serving the rest when one reportable fails.
/// </summary>
[Collection(TestCollections.ObservabilityState)]
public sealed class ObservationWriterChunkingTests
{
    private static readonly (CoreTelemetryTarget Core, RuntimeObservationTargets Target)[] s_coreSections =
    [
        (CoreTelemetryTarget.PacketDispatch, RuntimeObservationTargets.DISPATCH),
        (CoreTelemetryTarget.Tasks, RuntimeObservationTargets.TASKS),
        (CoreTelemetryTarget.Buffers, RuntimeObservationTargets.BUFFERS),
        (CoreTelemetryTarget.Connections, RuntimeObservationTargets.CONNECTIONS),
        (CoreTelemetryTarget.ObjectPools, RuntimeObservationTargets.OBJECT_POOLS),
        (CoreTelemetryTarget.ConnectionGuard, RuntimeObservationTargets.CONNECTION_GUARD),
        (CoreTelemetryTarget.ConcurrencyGate, RuntimeObservationTargets.CONCURRENCY_GATE),
        (CoreTelemetryTarget.PolicyRateLimiter, RuntimeObservationTargets.POLICY_RATE_LIMITER),
        (CoreTelemetryTarget.TokenBucketLimiter, RuntimeObservationTargets.TOKEN_BUCKET_LIMITER),
        (CoreTelemetryTarget.Sessions, RuntimeObservationTargets.SESSIONS),
    ];

    [Fact]
    public void WriteChunks_WhenSectionsExceedSmallBudget_SplitsIntoBoundedChunks()
    {
        const int Budget = 128;
        RuntimeObservationTargets requested = AllCoreTargets();
        RegisterCore();
        try
        {
            List<(BufferLease Payload, RuntimeObservationTargets Written)> chunks = ObservationWriter.WriteChunks(requested, Budget);
            try
            {
                Assert.True(chunks.Count > 1);
                RuntimeObservationTargets union = RuntimeObservationTargets.NONE;
                HashSet<string> names = [];
                foreach ((BufferLease payload, RuntimeObservationTargets written) in chunks)
                {
                    Assert.True(payload.Length <= Budget, $"chunk is {payload.Length} bytes");
                    Assert.Equal(RuntimeObservationTargets.NONE, union & written);
                    union |= written;

                    using JsonDocument document = JsonDocument.Parse(payload.Memory);
                    foreach (JsonProperty property in document.RootElement.EnumerateObject())
                    {
                        Assert.True(names.Add(property.Name), $"section {property.Name} repeated");
                    }
                }

                Assert.Equal(requested, union);
            }
            finally
            {
                DisposeAll(chunks);
            }
        }
        finally
        {
            UnregisterCore();
        }
    }

    [Fact]
    public void WriteChunks_WhenOneSectionExceedsBudget_TrimsItWithTruncatedMarker()
    {
        const int Budget = 8 * 1024;
        ReportRegistry.Instance.Register<IReportable>(CoreTelemetryTarget.ConcurrencyGate, new ArrayReportable(items: 2_000, itemChars: 20));
        try
        {
            List<(BufferLease Payload, RuntimeObservationTargets Written)> chunks =
                ObservationWriter.WriteChunks(RuntimeObservationTargets.CONCURRENCY_GATE, Budget);
            try
            {
                Assert.Single(chunks);
                (BufferLease payload, RuntimeObservationTargets written) = chunks[0];
                Assert.Equal(RuntimeObservationTargets.CONCURRENCY_GATE, written);
                Assert.True(payload.Length <= Budget, $"chunk is {payload.Length} bytes");

                using JsonDocument document = JsonDocument.Parse(payload.Memory);
                JsonElement section = document.RootElement.GetProperty(RuntimeObservationSections.ConcurrencyGate);
                Assert.True(section.GetProperty(JsonTrimmer.TruncatedProperty).GetBoolean());
                Assert.InRange(section.GetProperty("Items").GetArrayLength(), 1, 1_999);
            }
            finally
            {
                DisposeAll(chunks);
            }
        }
        finally
        {
            _ = ReportRegistry.Instance.Unregister<IReportable>(CoreTelemetryTarget.ConcurrencyGate);
        }
    }

    [Fact]
    public void WriteChunks_WhenSectionCannotFitEvenEmpty_OmitsSectionAndKeepsOthers()
    {
        const int Budget = 8 * 1024;
        ReportRegistry.Instance.Register<IReportable>(CoreTelemetryTarget.ConcurrencyGate, new WideReportable(properties: 3_000));
        ReportRegistry.Instance.Register<IReportable>(CoreTelemetryTarget.Tasks, new FakeReportable());
        try
        {
            List<(BufferLease Payload, RuntimeObservationTargets Written)> chunks =
                ObservationWriter.WriteChunks(RuntimeObservationTargets.CONCURRENCY_GATE | RuntimeObservationTargets.TASKS, Budget);
            try
            {
                Assert.Single(chunks);
                (BufferLease payload, RuntimeObservationTargets written) = chunks[0];
                Assert.Equal(RuntimeObservationTargets.TASKS, written);

                using JsonDocument document = JsonDocument.Parse(payload.Memory);
                Assert.False(document.RootElement.TryGetProperty(RuntimeObservationSections.ConcurrencyGate, out _));
                Assert.True(document.RootElement.TryGetProperty(RuntimeObservationSections.Tasks, out _));
            }
            finally
            {
                DisposeAll(chunks);
            }
        }
        finally
        {
            _ = ReportRegistry.Instance.Unregister<IReportable>(CoreTelemetryTarget.ConcurrencyGate);
            _ = ReportRegistry.Instance.Unregister<IReportable>(CoreTelemetryTarget.Tasks);
        }
    }

    [Fact]
    public void WriteChunks_WhenNothingResolvable_ReturnsSingleEmptyChunkWithNone()
    {
        UnregisterCore();

        List<(BufferLease Payload, RuntimeObservationTargets Written)> chunks =
            ObservationWriter.WriteChunks(RuntimeObservationTargets.TASKS | RuntimeObservationTargets.SESSIONS, 8 * 1024);
        try
        {
            Assert.Single(chunks);
            (BufferLease payload, RuntimeObservationTargets written) = chunks[0];
            Assert.Equal(RuntimeObservationTargets.NONE, written);
            Assert.Equal("{}", Encoding.UTF8.GetString(payload.Memory.Span));
        }
        finally
        {
            DisposeAll(chunks);
        }
    }

    [Fact]
    public void WriteChunks_WhenReportableThrows_DropsOnlyThatSection()
    {
        ReportRegistry.Instance.Register<IReportable>(CoreTelemetryTarget.ConcurrencyGate, new FakeReportable(throws: true));
        ReportRegistry.Instance.Register<IReportable>(CoreTelemetryTarget.TokenBucketLimiter, new FakeReportable());
        try
        {
            RuntimeObservationTargets requested = RuntimeObservationTargets.CONCURRENCY_GATE | RuntimeObservationTargets.TOKEN_BUCKET_LIMITER;

            List<(BufferLease Payload, RuntimeObservationTargets Written)> chunks = ObservationWriter.WriteChunks(requested, 8 * 1024);
            try
            {
                Assert.Single(chunks);
                (BufferLease payload, RuntimeObservationTargets written) = chunks[0];
                Assert.Equal(RuntimeObservationTargets.TOKEN_BUCKET_LIMITER, written);

                using JsonDocument document = JsonDocument.Parse(payload.Memory);
                Assert.False(document.RootElement.TryGetProperty(RuntimeObservationSections.ConcurrencyGate, out _));
                Assert.True(document.RootElement.TryGetProperty(RuntimeObservationSections.TokenBucketLimiter, out _));
            }
            finally
            {
                DisposeAll(chunks);
            }
        }
        finally
        {
            _ = ReportRegistry.Instance.Unregister<IReportable>(CoreTelemetryTarget.ConcurrencyGate);
            _ = ReportRegistry.Instance.Unregister<IReportable>(CoreTelemetryTarget.TokenBucketLimiter);
        }
    }

    private static RuntimeObservationTargets AllCoreTargets()
    {
        RuntimeObservationTargets targets = RuntimeObservationTargets.NONE;
        foreach ((CoreTelemetryTarget _, RuntimeObservationTargets target) in s_coreSections)
        {
            targets |= target;
        }

        return targets;
    }

    private static void RegisterCore()
    {
        foreach ((CoreTelemetryTarget core, RuntimeObservationTargets _) in s_coreSections)
        {
            ReportRegistry.Instance.Register<IReportable>(core, new FakeReportable());
        }
    }

    private static void UnregisterCore()
    {
        foreach ((CoreTelemetryTarget core, RuntimeObservationTargets _) in s_coreSections)
        {
            _ = ReportRegistry.Instance.Unregister<IReportable>(core);
        }
    }

    private static void DisposeAll(List<(BufferLease Payload, RuntimeObservationTargets Written)> chunks)
    {
        foreach ((BufferLease payload, RuntimeObservationTargets _) in chunks)
        {
            payload.Dispose();
        }
    }
}
