// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using Nalix.Abstractions;
using Nalix.Abstractions.Networking;
using Nalix.Framework;

namespace Nalix.Observability.Internal;

/// <summary>
/// Combines the per-transport reports of <typeparamref name="T"/> into one object keyed by transport name.
/// </summary>
/// <typeparam name="T">The per-transport reportable component, such as a listener or protocol.</typeparam>
internal sealed class TransportReport<T> : IReportable where T : class, IReportable
{
    public static readonly TransportReport<T> Instance = new();

    private TransportReport()
    {
    }

    public string GenerateReport() => string.Empty;

    public void WriteReportData(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        WRITE(writer, NetworkTransport.TCP, "TCP");
        WRITE(writer, NetworkTransport.UDP, "UDP");
        WRITE(writer, NetworkTransport.WEBSOCKET, "WEBSOCKET");
        writer.WriteEndObject();
    }

    private static void WRITE(Utf8JsonWriter writer, NetworkTransport transport, string name)
    {
        T? component = ReportRegistry.Instance.Get<T>(transport);
        if (component is null)
        {
            return;
        }

        writer.WritePropertyName(name);
        component.WriteReportData(writer);
    }
}
