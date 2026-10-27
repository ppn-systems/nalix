// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using Nalix.Abstractions.Networking;
using Nalix.Framework;
using Nalix.Integration.Tests.TestSupport;
using Nalix.Observability.Internal;

namespace Nalix.Integration.Tests.Observability;

/// <summary>
/// Verifies that <see cref="TransportReport{T}"/> keys each registered transport's report by name.
/// </summary>
[Collection(TestCollections.ObservabilityState)]
public sealed class TransportReportTests
{
    [Fact]
    public void WriteReportData_WhenListenerRegisteredForTcp_WritesReportUnderTcp()
    {
        ReportRegistry.Instance.Register<IListener>(NetworkTransport.TCP, new FakeListener());
        try
        {
            using JsonDocument document = ReportJson.Capture(TransportReport<IListener>.Instance);

            Assert.Equal(3, document.RootElement.GetProperty("TCP").GetProperty("Accepted").GetInt32());
        }
        finally
        {
            _ = ReportRegistry.Instance.Unregister<IListener>(NetworkTransport.TCP);
        }
    }

    [Fact]
    public void WriteReportData_WhenNothingRegistered_WritesEmptyObject()
    {
        _ = ReportRegistry.Instance.Unregister<IListener>(NetworkTransport.TCP);
        _ = ReportRegistry.Instance.Unregister<IListener>(NetworkTransport.UDP);
        _ = ReportRegistry.Instance.Unregister<IListener>(NetworkTransport.WEBSOCKET);

        using JsonDocument document = ReportJson.Capture(TransportReport<IListener>.Instance);

        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        Assert.Equal(0, document.RootElement.EnumerateObject().Count());
    }
}
