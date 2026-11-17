// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Nalix.Abstractions.Diagnostics;

namespace Nalix.Integration.Tests;

/// <summary>
/// Subscribes to every event of the SDK's diagnostic listener for the whole test run, the way an application that
/// collects logs would. Without a subscriber the SDK skips its diagnostic writes, so the tests would never execute
/// them; with it, every diagnostic on a path a test takes is built and written, which also checks it does not throw.
/// </summary>
internal sealed class SdkDiagnosticsSink : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>
{
    private static readonly SdkDiagnosticsSink s_instance = new();

    [ModuleInitializer]
    internal static void Initialize() => _ = DiagnosticListener.AllListeners.Subscribe(s_instance);

    public void OnNext(DiagnosticListener listener)
    {
        if (listener.Name == Nalix.SDK.DiagnosticsEvents.ListenerName)
        {
            _ = listener.Subscribe(this, static _ => true);
        }
    }

    public void OnNext(KeyValuePair<string, object?> value)
    {
        // Touch the payload as a log sink would, so formatting problems surface in the tests.
        if (value.Value is DiagnosticLog log)
        {
            _ = log.Tag.Length + log.Message.Length + (log.Exception?.Message.Length ?? 0);
        }
    }

    public void OnCompleted()
    {
    }

    public void OnError(Exception error)
    {
    }
}
