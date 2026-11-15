// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using Nalix.Abstractions;

namespace Nalix.Observability.Tests.TestSupport;

/// <summary>
/// Reportable that writes <c>{"Items":[...]}</c> with <paramref name="items"/> objects of <paramref name="itemChars"/>-character names.
/// </summary>
internal sealed class ArrayReportable(int items, int itemChars) : IReportable
{
    public string GenerateReport() => string.Empty;

    public void WriteReportData(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteStartArray("Items");
        for (int i = 0; i < items; i++)
        {
            writer.WriteStartObject();
            writer.WriteString("Name", new string('x', itemChars));
            writer.WriteNumber("Index", i);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}
