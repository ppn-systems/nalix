// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Nalix.Abstractions.Diagnostics;

namespace Nalix.SDK;

/// <summary>
/// Diagnostic events of the client SDK, published through a <see cref="DiagnosticListener"/> named
/// <see cref="ListenerName"/>, the same way as the other Nalix packages. Every log entry is an event named after its
/// level (<see cref="Internal"/>) whose payload is a <see cref="DiagnosticLog"/>.
/// </summary>
/// <remarks>
/// <para>
/// Nothing is formatted or allocated while no subscriber is listening. Subscribe with <see cref="SubscribeLogs"/>,
/// or with <see cref="DiagnosticListener.AllListeners"/> to bridge into any logging framework:
/// <code>
/// using IDisposable logs = Nalix.SDK.DiagnosticsEvents.SubscribeLogs(
///     (level, log) => Console.WriteLine($"[{level}] {log.Tag}: {log.Message} {log.Exception}"),
///     minimumLevel: Nalix.SDK.DiagnosticsEvents.Internal.Information);
/// </code>
/// </para>
/// <para>On the browser (Blazor WebAssembly) <see cref="DiagnosticListener"/> is not supported and the listener is a no-op.</para>
/// </remarks>
public static class DiagnosticsEvents
{
    /// <summary>The name of the SDK's <see cref="DiagnosticListener"/>.</summary>
    public const string ListenerName = "SDK";

    /// <summary>The SDK's diagnostic source.</summary>
    public static readonly DiagnosticListener Source = Environment.Diagnostics.DiagnosticListenerFactory.Create(ListenerName);

    /// <summary>Writes a diagnostic event.</summary>
    /// <typeparam name="T">The payload type.</typeparam>
    /// <param name="name">The event name.</param>
    /// <param name="payload">The event payload.</param>
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "DiagnosticSource.Write<T>() requires unreferenced-code analysis for payload property discovery. " +
            "Nalix diagnostic payloads are observational only and do not affect runtime behavior.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "Trimming",
        "IL2091",
        Justification = "Diagnostic payloads are observational only; public-property preservation is not required for runtime behavior.")]
    public static void Write<T>(string name, T payload) => Source.Write(name, payload);

    /// <summary>
    /// Subscribes <paramref name="onLog"/> to the SDK's log entries at <paramref name="minimumLevel"/> and above.
    /// </summary>
    /// <param name="onLog">Receives the level event name (one of <see cref="Internal"/>) and the entry.</param>
    /// <param name="minimumLevel">The lowest level to receive; defaults to <see cref="Internal.Information"/>.</param>
    /// <returns>A handle that ends the subscription when disposed.</returns>
    public static IDisposable SubscribeLogs(Action<string, DiagnosticLog> onLog, string minimumLevel = Internal.Information)
    {
        ArgumentNullException.ThrowIfNull(onLog);

        int threshold = Internal.Rank(minimumLevel);
        if (threshold < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumLevel), minimumLevel, "Expected one of the DiagnosticsEvents.Internal level names.");
        }

        return Source.Subscribe(new LogObserver(onLog), name => Internal.Rank(name) >= threshold);
    }

    /// <summary>Log levels. Each is the event name; the payload is a <see cref="DiagnosticLog"/>.</summary>
    public static class Internal
    {
        /// <summary>Very detailed flow (per frame / per request). Off unless explicitly enabled.</summary>
        public const string Trace = "Internal.Trace";

        /// <summary>Diagnostic detail for development.</summary>
        public const string Debug = "Internal.Debug";

        /// <summary>Normal lifecycle: connected, handshake done, reconnected.</summary>
        public const string Information = "Internal.Information";

        /// <summary>Something went wrong but the SDK recovers or continues.</summary>
        public const string Warning = "Internal.Warning";

        /// <summary>An operation failed and the application should know.</summary>
        public const string Error = "Internal.Error";

        internal static int Rank(string? name) => name switch
        {
            Trace => 0,
            Debug => 1,
            Information => 2,
            Warning => 3,
            Error => 4,
            _ => -1,
        };
    }

    private sealed class LogObserver(Action<string, DiagnosticLog> onLog) : IObserver<KeyValuePair<string, object?>>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (value.Value is DiagnosticLog log)
            {
                onLog(value.Key, log);
            }
        }
    }
}

/// <summary>
/// Writes SDK log entries. Callers on hot paths check <see cref="IsEnabled"/> before building the message.
/// </summary>
internal static class SdkLog
{
    public static bool IsEnabled(string level) => DiagnosticsEvents.Source.IsEnabled(level);

    public static void Trace(string tag, string message) => Write(DiagnosticsEvents.Internal.Trace, tag, message, null);

    public static void Debug(string tag, string message, Exception? exception = null) => Write(DiagnosticsEvents.Internal.Debug, tag, message, exception);

    public static void Info(string tag, string message) => Write(DiagnosticsEvents.Internal.Information, tag, message, null);

    public static void Warn(string tag, string message, Exception? exception = null) => Write(DiagnosticsEvents.Internal.Warning, tag, message, exception);

    public static void Error(string tag, string message, Exception? exception = null) => Write(DiagnosticsEvents.Internal.Error, tag, message, exception);

    private static void Write(string level, string tag, string message, Exception? exception)
    {
        if (DiagnosticsEvents.Source.IsEnabled(level))
        {
            DiagnosticsEvents.Write(level, new DiagnosticLog(tag, message, exception));
        }
    }
}
