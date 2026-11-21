// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.IO;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Nalix.Abstractions.Networking;
using Nalix.Network.Listeners.Tcp;
using Nalix.Network.Listeners.Web;
using NSubstitute;
using Xunit;

namespace Nalix.Network.Tests.Listeners;

[Collection(TestCollections.AsyncCallback)]
public sealed class ListenerMetricsTests
{
    private sealed class StubTcpListener : TcpListenerBase
    {
        public StubTcpListener(IProtocol protocol, IConnectionHub hub, IConnectionGuard guard) : base(12345, protocol, hub, guard) { }
    }

    private sealed class StubWebSocketListener : WebSocketListenerBase
    {
        public StubWebSocketListener(IProtocol protocol, IConnectionHub hub, IConnectionGuard guard) : base(12347, "/ws", protocol, hub, guard) { }
    }

    [Fact]
    public void TcpMetrics_AfterRecordCalls_ReflectsIncrements()
    {
        var protocol = Substitute.For<IProtocol>();
        var hub = Substitute.For<IConnectionHub>();
        var guard = Substitute.For<IConnectionGuard>();
        using var listener = new StubTcpListener(protocol, hub, guard);

        listener.Metrics.TotalAccepted.Should().Be(0);
        listener.Metrics.TotalRejected.Should().Be(0);
        listener.Metrics.TotalErrors.Should().Be(0);
        listener.Metrics.TotalProxyProtocolErrors.Should().Be(0);
        listener.Metrics.TotalQueueFullRejections.Should().Be(0);
        listener.Metrics.TotalLimiterRejections.Should().Be(0);

        // Record metrics
        listener.Metrics.RECORD_ACCEPTED();
        listener.Metrics.RECORD_PROXY_ERROR();
        listener.Metrics.RECORD_QUEUE_FULL_REJECTION();
        listener.Metrics.RECORD_LIMITER_REJECTION();
        listener.Metrics.RECORD_ERROR();

        // Check values
        listener.Metrics.TotalAccepted.Should().Be(1);
        listener.Metrics.TotalProxyProtocolErrors.Should().Be(1);
        listener.Metrics.TotalQueueFullRejections.Should().Be(1);
        listener.Metrics.TotalLimiterRejections.Should().Be(1);
        listener.Metrics.TotalErrors.Should().Be(1);

        // TotalRejected should be the sum of _totalRejected (0) + TotalQueueFullRejections (1) + TotalLimiterRejections (1) = 2
        listener.Metrics.TotalRejected.Should().Be(2);
    }

    [Fact]
    public void TcpGenerateReport_AfterRecordCalls_ContainsNewMetricsWithoutStaticFields()
    {
        var protocol = Substitute.For<IProtocol>();
        var hub = Substitute.For<IConnectionHub>();
        var guard = Substitute.For<IConnectionGuard>();
        using var listener = new StubTcpListener(protocol, hub, guard);

        listener.Metrics.RECORD_PROXY_ERROR();
        listener.Metrics.RECORD_QUEUE_FULL_REJECTION();
        listener.Metrics.RECORD_LIMITER_REJECTION();

        string report = listener.GenerateReport();

        report.Should().Contain("Queue Full      : 1");
        report.Should().Contain("Limiter/Guard   : 1");
        report.Should().Contain("Proxy Protocol Errs : 1");
        report.Should().Contain("Accept Queue Depth  : 0");
        
        // Assert static field LimiterEnabled has been removed
        report.Should().NotContain("LimiterEnabled");
    }

    [Fact]
    public void TcpWriteReportData_AfterRecordCalls_SerializesNewMetricsWithoutStaticFields()
    {
        var protocol = Substitute.For<IProtocol>();
        var hub = Substitute.For<IConnectionHub>();
        var guard = Substitute.For<IConnectionGuard>();
        using var listener = new StubTcpListener(protocol, hub, guard);

        listener.Metrics.RECORD_PROXY_ERROR();
        listener.Metrics.RECORD_QUEUE_FULL_REJECTION();
        listener.Metrics.RECORD_LIMITER_REJECTION();

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            listener.WriteReportData(writer);
        }

        string json = Encoding.UTF8.GetString(ms.ToArray());
        using var doc = JsonDocument.Parse(json);
        var metricsElement = doc.RootElement.GetProperty("Metrics");

        metricsElement.GetProperty("QueueFullRejections").GetInt64().Should().Be(1);
        metricsElement.GetProperty("LimiterRejections").GetInt64().Should().Be(1);
        metricsElement.GetProperty("ProxyProtocolErrors").GetInt64().Should().Be(1);
        metricsElement.GetProperty("AcceptQueueDepth").GetInt64().Should().Be(0);

        // Assert static field LimiterEnabled has been removed
        doc.RootElement.GetProperty("Connections").TryGetProperty("LimiterEnabled", out _).Should().BeFalse();
    }

    [Fact]
    public void WebSocketMetrics_AfterRecordCalls_ReflectsIncrements()
    {
        var protocol = Substitute.For<IProtocol>();
        var hub = Substitute.For<IConnectionHub>();
        var guard = Substitute.For<IConnectionGuard>();
        using var listener = new StubWebSocketListener(protocol, hub, guard);

        listener.Metrics.TotalAccepted.Should().Be(0);
        listener.Metrics.TotalRejected.Should().Be(0);
        listener.Metrics.TotalQueueFullRejections.Should().Be(0);
        listener.Metrics.TotalLimiterRejections.Should().Be(0);
        listener.Metrics.TotalErrors.Should().Be(0);

        // Record metrics
        listener.Metrics.RECORD_ACCEPTED();
        listener.Metrics.RECORD_REJECTED();
        listener.Metrics.RECORD_QUEUE_FULL_REJECTION();
        listener.Metrics.RECORD_LIMITER_REJECTION();
        listener.Metrics.RECORD_ERROR();

        // Check values
        listener.Metrics.TotalAccepted.Should().Be(1);
        listener.Metrics.TotalQueueFullRejections.Should().Be(1);
        listener.Metrics.TotalLimiterRejections.Should().Be(1);
        listener.Metrics.TotalErrors.Should().Be(1);

        // TotalRejected should be the sum: _totalRejected (1) + TotalQueueFullRejections (1) + TotalLimiterRejections (1) = 3
        listener.Metrics.TotalRejected.Should().Be(3);
    }

    [Fact]
    public void WebSocketGenerateReport_AfterRecordCalls_ContainsNewMetrics()
    {
        var protocol = Substitute.For<IProtocol>();
        var hub = Substitute.For<IConnectionHub>();
        var guard = Substitute.For<IConnectionGuard>();
        using var listener = new StubWebSocketListener(protocol, hub, guard);

        listener.Metrics.RECORD_QUEUE_FULL_REJECTION();
        listener.Metrics.RECORD_LIMITER_REJECTION();

        string report = listener.GenerateReport();

        report.Should().Contain("Total Accepted      : 0");
        report.Should().Contain("Total Rejected      : 2");
        report.Should().Contain("  - Queue Full      : 1");
        report.Should().Contain("  - Limiter/Guard   : 1");
        report.Should().Contain("Total Errors        : 0");
        report.Should().Contain("Accept Queue Depth  : 0");
    }

    [Fact]
    public void WebSocketWriteReportData_AfterRecordCalls_SerializesNewMetrics()
    {
        var protocol = Substitute.For<IProtocol>();
        var hub = Substitute.For<IConnectionHub>();
        var guard = Substitute.For<IConnectionGuard>();
        using var listener = new StubWebSocketListener(protocol, hub, guard);

        listener.Metrics.RECORD_QUEUE_FULL_REJECTION();
        listener.Metrics.RECORD_LIMITER_REJECTION();

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            listener.WriteReportData(writer);
        }

        string json = Encoding.UTF8.GetString(ms.ToArray());
        using var doc = JsonDocument.Parse(json);
        var metricsElement = doc.RootElement.GetProperty("Metrics");

        metricsElement.GetProperty("TotalAccepted").GetInt64().Should().Be(0);
        metricsElement.GetProperty("TotalRejected").GetInt64().Should().Be(2);
        metricsElement.GetProperty("QueueFullRejections").GetInt64().Should().Be(1);
        metricsElement.GetProperty("LimiterRejections").GetInt64().Should().Be(1);
        metricsElement.GetProperty("TotalErrors").GetInt64().Should().Be(0);
        metricsElement.GetProperty("AcceptQueueDepth").GetInt64().Should().Be(0);
    }
}
