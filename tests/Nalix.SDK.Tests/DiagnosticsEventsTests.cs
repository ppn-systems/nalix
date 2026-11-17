// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Concurrent;
using System.Linq;
using Nalix.Abstractions.Diagnostics;
using Xunit;

namespace Nalix.SDK.Tests;

/// <summary>
/// SDK log entries flow through <see cref="DiagnosticsEvents.Source"/>; <see cref="DiagnosticsEvents.SubscribeLogs"/>
/// delivers those at or above the chosen level. The listener is process-wide, so assertions use a unique tag.
/// </summary>
public sealed class DiagnosticsEventsTests
{
    [Fact]
    public void SubscribeLogs_DeliversEntriesAtOrAboveTheMinimumLevel()
    {
        string tag = "Test." + Guid.NewGuid().ToString("N");
        ConcurrentQueue<(string Level, DiagnosticLog Log)> seen = new();

        using (DiagnosticsEvents.SubscribeLogs((level, log) => seen.Enqueue((level, log)), DiagnosticsEvents.Internal.Information))
        {
            SdkLog.Trace(tag, "trace");
            SdkLog.Debug(tag, "debug");
            SdkLog.Info(tag, "info");
            SdkLog.Warn(tag, "warn", new InvalidOperationException("w"));
            SdkLog.Error(tag, "error");
        }

        SdkLog.Error(tag, "after dispose");

        (string Level, DiagnosticLog Log)[] mine = [.. seen.Where(e => e.Log.Tag == tag)];
        Assert.Equal(["info", "warn", "error"], mine.Select(e => e.Log.Message));
        Assert.Equal(
            [DiagnosticsEvents.Internal.Information, DiagnosticsEvents.Internal.Warning, DiagnosticsEvents.Internal.Error],
            mine.Select(e => e.Level));
        Assert.IsType<InvalidOperationException>(mine[1].Log.Exception);
    }

    [Fact]
    public void SubscribeLogs_WithUnknownLevel_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() => DiagnosticsEvents.SubscribeLogs((_, _) => { }, "Internal.Verbose"));
}
