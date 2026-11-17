// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Networking.Packets;

namespace Nalix.SDK;

/// <summary>
/// Names of the client-side tracing and metrics sources. Subscribe with any
/// <see cref="ActivityListener"/>/<see cref="MeterListener"/>, or with OpenTelemetry:
/// <c>.AddSource(SdkTelemetry.ActivitySourceName)</c> and <c>.AddMeter(SdkTelemetry.MeterName)</c>.
/// Nothing is recorded, and nothing is allocated, while no listener is attached.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>Activity <c>nalix.request</c> (client kind) per <c>RequestAsync</c>/<c>TryRequestAsync</c> call,
/// tagged <c>nalix.opcode</c>, <c>nalix.response.type</c>, <c>nalix.request.attempts</c>,
/// <c>nalix.request.outcome</c> and, on failure, <c>error.type</c>.</description></item>
/// <item><description>Histogram <c>nalix.client.request.duration</c> (seconds), tagged <c>nalix.response.type</c> and
/// <c>nalix.request.outcome</c> (<c>ok</c>, <c>timeout</c>, <c>not_connected</c>, <c>network_error</c>,
/// <c>canceled</c>, <c>error</c>).</description></item>
/// <item><description>Counter <c>nalix.client.request.retries</c>, tagged <c>nalix.response.type</c>.</description></item>
/// <item><description>Counter <c>nalix.client.reconnect.attempts</c>, tagged <c>nalix.reconnect.outcome</c>
/// (<c>resumed</c>, <c>handshake</c>, <c>failed</c>).</description></item>
/// </list>
/// Trace context is not propagated to the server; that needs request metadata on the wire.
/// </remarks>
public static class SdkTelemetry
{
    /// <summary>The name of the SDK's <see cref="ActivitySource"/>.</summary>
    public const string ActivitySourceName = "Nalix.SDK";

    /// <summary>The name of the SDK's <see cref="Meter"/>.</summary>
    public const string MeterName = "Nalix.SDK";

    private static readonly ActivitySource s_source = new(ActivitySourceName);
    private static readonly Meter s_meter = new(MeterName);

    private static readonly Histogram<double> s_requestDuration = s_meter.CreateHistogram<double>(
        "nalix.client.request.duration", unit: "s", description: "Duration of a request, including every retry.");

    private static readonly Counter<long> s_requestRetries = s_meter.CreateCounter<long>(
        "nalix.client.request.retries", unit: "{retry}", description: "Retries started after an attempt timed out.");

    private static readonly Counter<long> s_reconnectAttempts = s_meter.CreateCounter<long>(
        "nalix.client.reconnect.attempts", unit: "{attempt}", description: "Automatic reconnect attempts.");

    internal const string OutcomeOk = "ok";
    internal const string OutcomeTimeout = "timeout";
    internal const string OutcomeNotConnected = "not_connected";
    internal const string OutcomeNetworkError = "network_error";
    internal const string OutcomeCanceled = "canceled";
    internal const string OutcomeError = "error";

    internal const string ReconnectResumed = "resumed";
    internal const string ReconnectHandshake = "handshake";
    internal const string ReconnectFailed = "failed";

    /// <summary>Starts the request activity, or returns <see langword="null"/> when nobody listens.</summary>
    internal static Activity? StartRequest(IPacket request, string responseType)
    {
        if (!s_source.HasListeners())
        {
            return null;
        }

        Activity? activity = s_source.StartActivity("nalix.request", ActivityKind.Client);
        if (activity is { IsAllDataRequested: true })
        {
            _ = activity.SetTag("nalix.opcode", (int)request.Header.OpCode);
            _ = activity.SetTag("nalix.response.type", responseType);
        }

        return activity;
    }

    /// <summary>Records a retry about to start.</summary>
    internal static void RecordRetry(string responseType)
    {
        if (s_requestRetries.Enabled)
        {
            s_requestRetries.Add(1, new TagList { { "nalix.response.type", responseType } });
        }
    }

    /// <summary>Ends the request activity and records the request duration.</summary>
    internal static void CompleteRequest(Activity? activity, long startTimestamp, string responseType, string outcome, int attempts, Exception? error = null)
    {
        if (s_requestDuration.Enabled)
        {
            s_requestDuration.Record(
                Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds,
                new TagList { { "nalix.response.type", responseType }, { "nalix.request.outcome", outcome } });
        }

        if (activity is null)
        {
            return;
        }

        if (activity.IsAllDataRequested)
        {
            _ = activity.SetTag("nalix.request.attempts", attempts);
            _ = activity.SetTag("nalix.request.outcome", outcome);
            if (error is not null)
            {
                _ = activity.SetTag("error.type", error.GetType().FullName);
            }
        }

        if (outcome != OutcomeOk)
        {
            _ = activity.SetStatus(ActivityStatusCode.Error, error?.Message ?? outcome);
        }

        activity.Dispose();
    }

    /// <summary>Maps an exception that ended a request to its outcome tag.</summary>
    internal static string OutcomeOf(Exception error) => error switch
    {
        TimeoutException => OutcomeTimeout,
        OperationCanceledException => OutcomeCanceled,
        NetworkException => OutcomeNetworkError,
        _ => OutcomeError,
    };

    /// <summary>Records one automatic reconnect attempt.</summary>
    internal static void RecordReconnect(string outcome)
    {
        if (s_reconnectAttempts.Enabled)
        {
            s_reconnectAttempts.Add(1, new TagList { { "nalix.reconnect.outcome", outcome } });
        }
    }
}
