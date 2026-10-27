// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using Nalix.Abstractions;
using Nalix.Abstractions.Diagnostics;
using Nalix.Abstractions.Networking;
using Nalix.Environment.Memory;
using Nalix.Framework;
using Nalix.Framework.Injection;
using Nalix.Observability.Contracts;

namespace Nalix.Observability.Internal;

/// <summary>
/// Writes the requested report sections into one pooled JSON payload.
/// </summary>
/// <remarks>
/// Each section is rendered into a scratch buffer first and copied in only when it completes,
/// so a reportable that throws drops its own section instead of corrupting the whole payload.
/// Writers are cached per thread and retargeted with <see cref="Utf8JsonWriter.Reset(System.Buffers.IBufferWriter{byte})"/>.
/// </remarks>
internal static class ObservationWriter
{
    private static readonly JsonWriterOptions s_options = new() { Indented = false, SkipValidation = true };

    private static readonly Section[] s_sections =
    [
        new(RuntimeObservationTargets.PROCESS, RuntimeObservationSections.Process, static () => ProcessReport.Instance),
        new(RuntimeObservationTargets.DISPATCH, RuntimeObservationSections.Dispatch, static () => CORE(CoreTelemetryTarget.PacketDispatch)),
        new(RuntimeObservationTargets.TASKS, RuntimeObservationSections.Tasks, static () => CORE(CoreTelemetryTarget.Tasks)),
        new(RuntimeObservationTargets.BUFFERS, RuntimeObservationSections.Buffers, static () => CORE(CoreTelemetryTarget.Buffers)),
        new(RuntimeObservationTargets.CONNECTIONS, RuntimeObservationSections.Connections, static () => CORE(CoreTelemetryTarget.Connections)),
        new(RuntimeObservationTargets.INSTANCES, RuntimeObservationSections.Instances, static () => InstanceManager.Instance),
        new(RuntimeObservationTargets.OBJECT_POOLS, RuntimeObservationSections.ObjectPools, static () => CORE(CoreTelemetryTarget.ObjectPools)),
        new(RuntimeObservationTargets.CONNECTION_GUARD, RuntimeObservationSections.ConnectionGuard, static () => CORE(CoreTelemetryTarget.ConnectionGuard)),
        new(RuntimeObservationTargets.CONCURRENCY_GATE, RuntimeObservationSections.ConcurrencyGate, static () => CORE(CoreTelemetryTarget.ConcurrencyGate)),
        new(RuntimeObservationTargets.POLICY_RATE_LIMITER, RuntimeObservationSections.PolicyRateLimiter, static () => CORE(CoreTelemetryTarget.PolicyRateLimiter)),
        new(RuntimeObservationTargets.TOKEN_BUCKET_LIMITER, RuntimeObservationSections.TokenBucketLimiter, static () => CORE(CoreTelemetryTarget.TokenBucketLimiter)),
        new(RuntimeObservationTargets.SESSIONS, RuntimeObservationSections.Sessions, static () => CORE(CoreTelemetryTarget.Sessions)),
        new(RuntimeObservationTargets.LISTENERS, RuntimeObservationSections.Listeners, static () => TransportReport<IListener>.Instance),
        new(RuntimeObservationTargets.PROTOCOLS, RuntimeObservationSections.Protocols, static () => TransportReport<IProtocol>.Instance),
    ];

    [ThreadStatic] private static Utf8JsonWriter? t_payloadWriter;
    [ThreadStatic] private static Utf8JsonWriter? t_sectionWriter;

    /// <summary>
    /// Writes every requested and registered section.
    /// </summary>
    /// <param name="targets">The requested subsystems.</param>
    /// <param name="written">Receives the subsystems that were actually written.</param>
    /// <returns>A lease owning the UTF-8 JSON payload; the caller owns and must dispose it.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design", "CA1031:Do not catch general exception types",
        Justification = "A faulty reportable must only drop its own section.")]
    public static BufferLease Write(RuntimeObservationTargets targets, out RuntimeObservationTargets written)
    {
        written = RuntimeObservationTargets.NONE;

        using BufferWriter payload = new(8 * 1024);
        using BufferWriter scratch = new(4 * 1024);

        Utf8JsonWriter writer = RENT(ref t_payloadWriter, payload);
        Utf8JsonWriter sectionWriter = RENT(ref t_sectionWriter, scratch);

        writer.WriteStartObject();

        foreach (Section section in s_sections)
        {
            if ((targets & section.Target) == 0)
            {
                continue;
            }

            try
            {
                IReportable? report = section.Resolve();
                if (report is null)
                {
                    continue;
                }

                scratch.Clear();
                sectionWriter.Reset(scratch);
                report.WriteReportData(sectionWriter);
                sectionWriter.Flush();

                writer.WritePropertyName(section.Name);
                writer.WriteRawValue(scratch.WrittenSpan, skipInputValidation: true);
                written |= section.Target;
            }
            catch
            {
                // Section omitted; the remaining sections are still served.
            }
        }

        writer.WriteEndObject();
        writer.Flush();

        return payload.ExtractLease();
    }

    private static Utf8JsonWriter RENT(ref Utf8JsonWriter? cached, BufferWriter target)
    {
        if (cached is null)
        {
            cached = new Utf8JsonWriter(target, s_options);
        }
        else
        {
            cached.Reset(target);
        }

        return cached;
    }

    private static IReportable? CORE(CoreTelemetryTarget target) => ReportRegistry.Instance.Get<IReportable>(target);

    private readonly record struct Section(RuntimeObservationTargets Target, string Name, Func<IReportable?> Resolve);
}
