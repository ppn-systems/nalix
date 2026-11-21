// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading.Tasks;
using Nalix.Codec.ProtocolFrames;
using Nalix.SDK.Options;
using Nalix.SDK.Tests.TestSupport;
using Nalix.SDK.Transport.Extensions;
using Xunit;

namespace Nalix.SDK.Tests.Transport.Extensions;

/// <summary>
/// Requests emit a <c>nalix.request</c> activity and request metrics through <see cref="SdkTelemetry"/>.
/// Listeners are process-wide, so each test runs under its own parent activity and only inspects its children.
/// </summary>
[Collection(TestCollections.RealServer)]
public sealed class RequestTelemetryTests : IDisposable
{
    private static readonly ActivitySource s_testSource = new("Nalix.SDK.Tests.Telemetry");

    private readonly ConcurrentQueue<Activity> _stopped = new();
    private readonly ConcurrentQueue<(string Instrument, object? Value, Dictionary<string, object?> Tags)> _measurements = new();
    private readonly ActivityListener _activityListener;
    private readonly MeterListener _meterListener;

    public RequestTelemetryTests()
    {
        _activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is SdkTelemetry.ActivitySourceName or "Nalix.SDK.Tests.Telemetry",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => _stopped.Enqueue(activity),
        };
        ActivitySource.AddActivityListener(_activityListener);

        _meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == SdkTelemetry.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        _meterListener.SetMeasurementEventCallback<double>((i, v, tags, _) => this.Record(i, v, tags));
        _meterListener.SetMeasurementEventCallback<long>((i, v, tags, _) => this.Record(i, v, tags));
        _meterListener.Start();
    }

    public void Dispose()
    {
        _activityListener.Dispose();
        _meterListener.Dispose();
    }

    [Fact]
    public async Task RequestAsync_WhenItTimesOutAfterARetry_RecordsActivityRetryAndDuration()
    {
        FakeSession session = new(isConnected: true);
        using Activity root = s_testSource.StartActivity("test-root")!;

        _ = await Assert.ThrowsAsync<TimeoutException>(async () =>
            await session.RequestAsync<Control>(new Control(), RequestOptions.Default.WithTimeout(50).WithRetry(1)));

        Activity request = Assert.Single(this.ChildRequests(root));
        Assert.Equal(ActivityKind.Client, request.Kind);
        Assert.Equal(ActivityStatusCode.Error, request.Status);
        Assert.Equal("timeout", request.GetTagItem("nalix.request.outcome"));
        Assert.Equal(2, request.GetTagItem("nalix.request.attempts"));
        Assert.Equal(nameof(Control), request.GetTagItem("nalix.response.type"));
        Assert.Equal(typeof(TimeoutException).FullName, request.GetTagItem("error.type"));

        Assert.Contains(_measurements, m => m.Instrument == "nalix.client.request.retries" && (long)m.Value! >= 1);
        Assert.Contains(_measurements, m => m.Instrument == "nalix.client.request.duration"
            && Equals(m.Tags["nalix.request.outcome"], "timeout") && (double)m.Value! > 0);
    }

    [Fact]
    public async Task TryRequestAsync_WhenItTimesOut_RecordsTimeoutOutcome()
    {
        FakeSession session = new(isConnected: true);
        using Activity root = s_testSource.StartActivity("test-root")!;

        RequestOutcome<Control> outcome = await session.TryRequestAsync<Control>(new Control(), RequestOptions.Default.WithTimeout(50));

        Assert.Equal(RequestOutcomeKind.TimedOut, outcome.Kind);
        Activity request = Assert.Single(this.ChildRequests(root));
        Assert.Equal("timeout", request.GetTagItem("nalix.request.outcome"));
        Assert.Equal(1, request.GetTagItem("nalix.request.attempts"));
    }

    [Fact]
    public async Task RequestAsync_WhenNotConnected_RecordsNetworkErrorOutcome()
    {
        FakeSession session = new(isConnected: false);
        using Activity root = s_testSource.StartActivity("test-root")!;

        _ = await Assert.ThrowsAnyAsync<Exception>(async () => await session.RequestAsync<Control>(new Control()));

        Activity request = Assert.Single(this.ChildRequests(root));
        Assert.Equal("network_error", request.GetTagItem("nalix.request.outcome"));
        Assert.Equal(0, request.GetTagItem("nalix.request.attempts"));
    }

    private List<Activity> ChildRequests(Activity root)
        => [.. _stopped.Where(a => a.OperationName == "nalix.request" && a.ParentSpanId == root.SpanId)];

    private void Record(Instrument instrument, object value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        Dictionary<string, object?> copy = [];
        foreach (KeyValuePair<string, object?> tag in tags)
        {
            copy[tag.Key] = tag.Value;
        }

        _measurements.Enqueue((instrument.Name, value, copy));
    }
}
