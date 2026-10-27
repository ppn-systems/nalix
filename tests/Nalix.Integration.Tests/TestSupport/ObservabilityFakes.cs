// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using Nalix.Abstractions;
using Nalix.Abstractions.Networking;

namespace Nalix.Integration.Tests.TestSupport;

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

/// <summary>
/// Listener that reports a fixed <c>Accepted</c> counter.
/// </summary>
internal sealed class FakeListener : IListener
{
    public void Activate(CancellationToken cancellationToken = default)
    {
    }

    public void Deactivate(CancellationToken cancellationToken = default)
    {
    }

    public void Dispose()
    {
    }

    public string GenerateReport() => string.Empty;

    public void WriteReportData(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteNumber("Accepted", 3);
        writer.WriteEndObject();
    }
}
