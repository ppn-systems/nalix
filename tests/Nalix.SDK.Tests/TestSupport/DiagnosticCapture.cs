// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Nalix.Abstractions.Diagnostics;

namespace Nalix.SDK.Tests.TestSupport;

/// <summary>
/// Records SDK diagnostic events whose name starts with <c>prefix</c>, as an application would subscribe to
/// <see cref="DiagnosticsEvents.Source"/>. The listener is process-wide, so tests filter what they assert on.
/// </summary>
internal sealed class DiagnosticCapture : IObserver<KeyValuePair<string, object?>>, IDisposable
{
    private readonly IDisposable _subscription;

    public DiagnosticCapture(string prefix)
        => _subscription = DiagnosticsEvents.Source.Subscribe(this, name => name.StartsWith(prefix, StringComparison.Ordinal));

    public ConcurrentQueue<(string Name, DiagnosticLog Log)> Events { get; } = new();

    public void OnNext(KeyValuePair<string, object?> value)
    {
        if (value.Value is DiagnosticLog log)
        {
            this.Events.Enqueue((value.Key, log));
        }
    }

    public void OnCompleted()
    {
    }

    public void OnError(Exception error)
    {
    }

    public void Dispose() => _subscription.Dispose();
}
