// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Buffers;
using System.Text.Json;
using Nalix.Abstractions;

namespace Nalix.Integration.Tests.TestSupport;

/// <summary>
/// Renders a reportable into a standalone JSON document for assertions.
/// </summary>
internal static class ReportJson
{
    /// <summary>Writes the reportable's data and parses it. The caller disposes the returned document.</summary>
    public static JsonDocument Capture(IReportable report)
    {
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            report.WriteReportData(writer);
        }

        return JsonDocument.Parse(buffer.WrittenMemory);
    }
}
