// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Threading;
using Microsoft.Extensions.Logging;
using Nalix.Abstractions.Diagnostics;
using Nalix.Framework;
using Xunit;

namespace Nalix.Hosting.Tests.Application;

[Collection(TestCollections.RealServer)]
public sealed class NetworkApplicationBuilderLoggerTests : IDisposable
{
    [Fact]
    public void UseLogger_CalledTwice_DeliversDiagnosticsOnlyToTheLatestLogger()
    {
        CountingLogger first = new();
        CountingLogger second = new();

        _ = NetworkApplication.CreateBuilder().UseLogger(first).UseLogger(second);

        DiagnosticsEvents.Write(DiagnosticsEvents.Tasks.Started, new DiagnosticLog("Test", "hello"));

        Assert.Equal(0, first.Count);
        Assert.Equal(1, second.Count);
    }

    public void Dispose()
    {
        Bootstrap.DiagnosticChannel?.Dispose();
        Bootstrap.DiagnosticChannel = null;
    }

    private sealed class CountingLogger : ILogger
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (state?.ToString()?.Contains("hello", StringComparison.Ordinal) == true)
            {
                _ = Interlocked.Increment(ref _count);
            }
        }
    }
}
