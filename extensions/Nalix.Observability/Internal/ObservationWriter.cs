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

    private const int MaxArrayItems = 4096;

    [ThreadStatic] private static Utf8JsonWriter? t_payloadWriter;
    [ThreadStatic] private static Utf8JsonWriter? t_sectionWriter;

    /// <summary>
    /// Writes every requested and registered section into as many payloads ("chunks") as needed so that none
    /// exceeds <paramref name="budget"/> bytes.
    /// </summary>
    /// <remarks>
    /// Sections are packed in catalog order; when the next one does not fit, the current chunk is closed and a new
    /// one started. A section larger than the whole budget on its own has its arrays capped (see
    /// <see cref="JsonTrimmer"/>) until it fits, or is omitted. Each chunk reports the sections it carries.
    /// </remarks>
    /// <param name="targets">The requested subsystems.</param>
    /// <param name="budget">The maximum size of one chunk in bytes.</param>
    /// <returns>The chunks, in order; the caller owns and must dispose every lease. Never empty.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design", "CA1031:Do not catch general exception types",
        Justification = "A faulty reportable must only drop its own section.")]
    public static List<(BufferLease Payload, RuntimeObservationTargets Written)> WriteChunks(RuntimeObservationTargets targets, int budget)
    {
        List<(BufferLease, RuntimeObservationTargets)> chunks = [];
        using BufferWriter scratch = new(4 * 1024);
        using BufferWriter trimmed = new(1024);
        Utf8JsonWriter sectionWriter = RENT(ref t_sectionWriter, scratch);

        BufferWriter? payload = null;
        Utf8JsonWriter? writer = null;
        RuntimeObservationTargets written = RuntimeObservationTargets.NONE;
        int used = 0;

        try
        {
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

                    ReadOnlySpan<byte> body = scratch.WrittenSpan;
                    int overhead = section.Name.Length + 4;
                    if (body.Length + overhead + 2 > budget)
                    {
                        body = FIT(body, budget - overhead - 2, trimmed);
                        if (body.IsEmpty)
                        {
                            continue;
                        }
                    }

                    if (writer is not null && used + body.Length + overhead > budget)
                    {
                        chunks.Add((CLOSE(writer, payload!), written));
                        writer = null;
                    }

                    if (writer is null)
                    {
                        payload?.Dispose();
                        payload = new BufferWriter(Math.Min(budget, 8 * 1024));
                        writer = RENT(ref t_payloadWriter, payload);
                        writer.WriteStartObject();
                        written = RuntimeObservationTargets.NONE;
                        used = 2;
                    }

                    writer.WritePropertyName(section.Name);
                    writer.WriteRawValue(body, skipInputValidation: true);
                    used += body.Length + overhead;
                    written |= section.Target;
                }
                catch
                {
                    // Section omitted; the remaining sections are still served.
                }
            }

            if (writer is not null)
            {
                chunks.Add((CLOSE(writer, payload!), written));
                writer = null;
            }
            else if (chunks.Count == 0)
            {
                // Nothing resolvable: one empty object, so callers always have a chunk to answer with.
                payload = new BufferWriter(16);
                writer = RENT(ref t_payloadWriter, payload);
                writer.WriteStartObject();
                chunks.Add((CLOSE(writer, payload), RuntimeObservationTargets.NONE));
                writer = null;
            }
        }
        finally
        {
            // Idempotent: after CLOSE extracted the lease the writer owns nothing.
            payload?.Dispose();
        }

        return chunks;
    }

    /// <summary>Writes every requested and registered section into a single payload, with no size limit.</summary>
    /// <param name="targets">The requested subsystems.</param>
    /// <param name="written">Receives the subsystems that were actually written.</param>
    /// <returns>A lease owning the UTF-8 JSON payload; the caller owns and must dispose it.</returns>
    public static BufferLease Write(RuntimeObservationTargets targets, out RuntimeObservationTargets written)
    {
        List<(BufferLease Payload, RuntimeObservationTargets Written)> chunks = WriteChunks(targets, int.MaxValue - 1024);
        written = chunks[0].Written;
        return chunks[0].Payload;
    }

    private static BufferLease CLOSE(Utf8JsonWriter writer, BufferWriter payload)
    {
        writer.WriteEndObject();
        writer.Flush();
        BufferLease lease = payload.ExtractLease();
        payload.Dispose();
        return lease;
    }

    // Largest per-array item cap whose trimmed output fits `limit`, found by binary search (≈12 passes for 4096).
    private static ReadOnlySpan<byte> FIT(ReadOnlySpan<byte> body, int limit, BufferWriter output)
    {
        int low = 0;
        int high = MaxArrayItems;
        int best = -1;
        while (low <= high)
        {
            int cap = low + ((high - low) / 2);
            output.Clear();
            _ = JsonTrimmer.Trim(body, cap, output);
            if (output.WrittenSpan.Length <= limit)
            {
                best = cap;
                low = cap + 1;
            }
            else
            {
                high = cap - 1;
            }
        }

        if (best < 0)
        {
            return [];
        }

        output.Clear();
        _ = JsonTrimmer.Trim(body, best, output);
        return output.WrittenSpan;
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
