// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using Nalix.Abstractions;

namespace Nalix.Observability.Tests.TestSupport;

/// <summary>
/// Reportable that writes a one-property JSON object, or throws before writing when <paramref name="throws"/> is set.
/// </summary>
internal sealed class FakeReportable(bool throws = false) : IReportable
{
    public string GenerateReport() => string.Empty;

    public void WriteReportData(Utf8JsonWriter writer)
    {
        if (throws)
        {
            throw new InvalidOperationException("Simulated reportable failure.");
        }

        writer.WriteStartObject();
        writer.WriteNumber("Value", 1);
        writer.WriteEndObject();
    }
}
