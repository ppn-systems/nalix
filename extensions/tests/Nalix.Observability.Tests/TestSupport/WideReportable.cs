// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using Nalix.Abstractions;

namespace Nalix.Observability.Tests.TestSupport;

/// <summary>
/// Reportable that writes one object with <paramref name="properties"/> numeric properties. Its size cannot be
/// reduced by array trimming, so it only fits a budget that holds all of them.
/// </summary>
internal sealed class WideReportable(int properties) : IReportable
{
    public string GenerateReport() => string.Empty;

    public void WriteReportData(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        for (int i = 0; i < properties; i++)
        {
            writer.WriteNumber("Property" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), i);
        }

        writer.WriteEndObject();
    }
}
