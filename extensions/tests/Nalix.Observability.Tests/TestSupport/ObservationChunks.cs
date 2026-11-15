// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Observability.Contracts;

namespace Nalix.Observability.Tests.TestSupport;

/// <summary>
/// Collects the response chunks streamed by <c>RuntimeObservationHandlers.HandleAsync</c> and disposes them.
/// </summary>
internal static class ObservationChunks
{
    /// <summary>Consumes the whole stream and returns every chunk in order. The caller disposes the chunks.</summary>
    public static async Task<List<RuntimeObservation>> CollectAsync(IAsyncEnumerable<RuntimeObservation> chunks)
    {
        List<RuntimeObservation> collected = [];
        await foreach (RuntimeObservation chunk in chunks)
        {
            collected.Add(chunk);
        }

        return collected;
    }

    /// <summary>Disposes every chunk, releasing its payload lease.</summary>
    public static void DisposeAll(IEnumerable<RuntimeObservation> chunks)
    {
        foreach (RuntimeObservation chunk in chunks)
        {
            chunk.Dispose();
        }
    }
}
