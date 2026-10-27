// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using Nalix.Abstractions;
using Nalix.Abstractions.Diagnostics;
using Nalix.Environment.Memory;
using Nalix.Framework;
using Nalix.Integration.Tests.TestSupport;
using Nalix.Observability.Contracts;
using Nalix.Observability.Internal;

namespace Nalix.Integration.Tests.Observability;

/// <summary>
/// Verifies which sections <see cref="ObservationWriter"/> emits, which flags it reports, and that a faulty section is dropped alone.
/// </summary>
[Collection(TestCollections.ObservabilityState)]
public sealed class ObservationWriterTests
{
    private static readonly CoreTelemetryTarget[] s_coreTargets =
    [
        CoreTelemetryTarget.PacketDispatch,
        CoreTelemetryTarget.Tasks,
        CoreTelemetryTarget.Buffers,
        CoreTelemetryTarget.Connections,
        CoreTelemetryTarget.ObjectPools,
        CoreTelemetryTarget.ConnectionGuard,
        CoreTelemetryTarget.ConcurrencyGate,
        CoreTelemetryTarget.PolicyRateLimiter,
        CoreTelemetryTarget.TokenBucketLimiter,
        CoreTelemetryTarget.Sessions,
    ];

    private static readonly (RuntimeObservationTargets Target, string Name)[] s_sections =
    [
        (RuntimeObservationTargets.PROCESS, RuntimeObservationSections.Process),
        (RuntimeObservationTargets.DISPATCH, RuntimeObservationSections.Dispatch),
        (RuntimeObservationTargets.TASKS, RuntimeObservationSections.Tasks),
        (RuntimeObservationTargets.BUFFERS, RuntimeObservationSections.Buffers),
        (RuntimeObservationTargets.CONNECTIONS, RuntimeObservationSections.Connections),
        (RuntimeObservationTargets.INSTANCES, RuntimeObservationSections.Instances),
        (RuntimeObservationTargets.OBJECT_POOLS, RuntimeObservationSections.ObjectPools),
        (RuntimeObservationTargets.CONNECTION_GUARD, RuntimeObservationSections.ConnectionGuard),
        (RuntimeObservationTargets.CONCURRENCY_GATE, RuntimeObservationSections.ConcurrencyGate),
        (RuntimeObservationTargets.POLICY_RATE_LIMITER, RuntimeObservationSections.PolicyRateLimiter),
        (RuntimeObservationTargets.TOKEN_BUCKET_LIMITER, RuntimeObservationSections.TokenBucketLimiter),
        (RuntimeObservationTargets.SESSIONS, RuntimeObservationSections.Sessions),
        (RuntimeObservationTargets.LISTENERS, RuntimeObservationSections.Listeners),
        (RuntimeObservationTargets.PROTOCOLS, RuntimeObservationSections.Protocols),
    ];

    [Fact]
    public void Write_WhenProcessRequested_WritesProcessCounters()
    {
        using BufferLease lease = ObservationWriter.Write(RuntimeObservationTargets.PROCESS, out RuntimeObservationTargets written);
        using JsonDocument document = JsonDocument.Parse(lease.Memory);

        Assert.Equal(RuntimeObservationTargets.PROCESS, written);

        JsonElement process = document.RootElement.GetProperty(RuntimeObservationSections.Process);
        Assert.Equal(JsonValueKind.Object, process.ValueKind);

        foreach (string name in ProcessReportTests.NumericFields)
        {
            Assert.Equal(JsonValueKind.Number, process.GetProperty(name).ValueKind);
        }
    }

    [Fact]
    public void Write_WhenTransportSectionsRequested_WritesObjectsAndSetsFlags()
    {
        RuntimeObservationTargets requested = RuntimeObservationTargets.LISTENERS | RuntimeObservationTargets.PROTOCOLS;

        using BufferLease lease = ObservationWriter.Write(requested, out RuntimeObservationTargets written);
        using JsonDocument document = JsonDocument.Parse(lease.Memory);

        Assert.Equal(requested, written);
        Assert.Equal(JsonValueKind.Object, document.RootElement.GetProperty(RuntimeObservationSections.Listeners).ValueKind);
        Assert.Equal(JsonValueKind.Object, document.RootElement.GetProperty(RuntimeObservationSections.Protocols).ValueKind);
    }

    [Fact]
    public void Write_WhenAllTargetsRequested_WritesEveryResolvableSection()
    {
        RegisterCoreReportables();
        try
        {
            using BufferLease lease = ObservationWriter.Write(RuntimeObservationTargets.ALL, out RuntimeObservationTargets written);
            using JsonDocument document = JsonDocument.Parse(lease.Memory);

            Assert.Equal(RuntimeObservationTargets.ALL, written);
            foreach ((RuntimeObservationTargets _, string name) in s_sections)
            {
                Assert.True(document.RootElement.TryGetProperty(name, out _), name);
            }
        }
        finally
        {
            UnregisterCoreReportables();
        }
    }

    [Fact]
    public void Write_WhenReportableNotRegistered_OmitsSectionAndKeepsRequestedOthers()
    {
        _ = ReportRegistry.Instance.Unregister<IReportable>(CoreTelemetryTarget.TokenBucketLimiter);

        RuntimeObservationTargets requested = RuntimeObservationTargets.PROCESS | RuntimeObservationTargets.TOKEN_BUCKET_LIMITER;

        using BufferLease lease = ObservationWriter.Write(requested, out RuntimeObservationTargets written);
        using JsonDocument document = JsonDocument.Parse(lease.Memory);

        Assert.Equal(RuntimeObservationTargets.PROCESS, written);
        Assert.True(document.RootElement.TryGetProperty(RuntimeObservationSections.Process, out _));
        Assert.False(document.RootElement.TryGetProperty(RuntimeObservationSections.TokenBucketLimiter, out _));
    }

    [Fact]
    public void Write_WhenReportableThrows_DropsOnlyThatSection()
    {
        ReportRegistry.Instance.Register<IReportable>(CoreTelemetryTarget.Tasks, new FakeReportable(throws: true));
        ReportRegistry.Instance.Register<IReportable>(CoreTelemetryTarget.Sessions, new FakeReportable());
        try
        {
            RuntimeObservationTargets requested =
                RuntimeObservationTargets.PROCESS | RuntimeObservationTargets.TASKS | RuntimeObservationTargets.SESSIONS;

            using BufferLease lease = ObservationWriter.Write(requested, out RuntimeObservationTargets written);
            using JsonDocument document = JsonDocument.Parse(lease.Memory);

            Assert.Equal(RuntimeObservationTargets.PROCESS | RuntimeObservationTargets.SESSIONS, written);
            Assert.True(document.RootElement.TryGetProperty(RuntimeObservationSections.Process, out _));
            Assert.False(document.RootElement.TryGetProperty(RuntimeObservationSections.Tasks, out _));
            Assert.True(document.RootElement.TryGetProperty(RuntimeObservationSections.Sessions, out _));
        }
        finally
        {
            _ = ReportRegistry.Instance.Unregister<IReportable>(CoreTelemetryTarget.Tasks);
            _ = ReportRegistry.Instance.Unregister<IReportable>(CoreTelemetryTarget.Sessions);
        }
    }

    private static void RegisterCoreReportables()
    {
        foreach (CoreTelemetryTarget target in s_coreTargets)
        {
            ReportRegistry.Instance.Register<IReportable>(target, new FakeReportable());
        }
    }

    private static void UnregisterCoreReportables()
    {
        foreach (CoreTelemetryTarget target in s_coreTargets)
        {
            _ = ReportRegistry.Instance.Unregister<IReportable>(target);
        }
    }
}
