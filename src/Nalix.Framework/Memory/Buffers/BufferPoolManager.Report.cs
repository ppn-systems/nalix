// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Nalix.Framework.Memory.Internal.Buffers;

namespace Nalix.Framework.Memory.Buffers;

public sealed partial class BufferPoolManager
{
    /// <summary>
    /// Generates a report on the current state of the buffer pool.
    /// </summary>
    /// <returns>A string containing the report.</returns>
    [DebuggerStepThrough]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public string GenerateReport()
    {
        StringBuilder sb = new();

        this.APPEND_REPORT_HEADER(sb);
        this.APPEND_SUSPICIOUS_BUFFERS(sb);

        return sb.ToString();
    }

    /// <inheritdoc/>
    public void WriteReportData(System.Text.Json.Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        (long rents, long returns, long bytesRented) = this.ReadCounters();

        writer.WriteStartObject();
        writer.WriteString("UtcNow", DateTime.UtcNow);
        writer.WriteNumber("RentCount", rents);
        writer.WriteNumber("ReturnCount", returns);
        writer.WriteNumber("TotalBytesRented", bytesRented);
        writer.WriteNumber("OutstandingCount", rents - returns);

        double uptimeSec = (DateTime.UtcNow - _startTime).TotalSeconds;
        double throughputMBps = uptimeSec > 0
            ? (double)bytesRented / (1024 * 1024) / uptimeSec
            : 0;
        writer.WriteNumber("ThroughputMBps", throughputMBps);

        writer.WriteEndObject();
    }

    #region Private: Reporting

    private void APPEND_SUSPICIOUS_BUFFERS(StringBuilder sb)
    {
        if (!_config.EnableBufferLeakDetection)
        {
            return;
        }

        _ = sb.AppendLine("Suspicious Buffers (Outstanding > " + _config.SuspiciousThresholdSeconds + "s):");
        _ = sb.AppendLine("----------------------------------------------------------------------------------------------");
        _ = sb.AppendLine("SIZE (bytes) | Elapsed (s) | Stack Trace (first line)");
        _ = sb.AppendLine("----------------------------------------------------------------------------------------------");

        long now = Stopwatch.GetTimestamp();
        long thresholdTicks = _config.SuspiciousThresholdSeconds * Stopwatch.Frequency;
        int found = 0;

        List<WeakReference<BufferSentinel>> survivors = new();

        foreach (WeakReference<BufferSentinel> weakRef in _sentinelTracker)
        {
            if (weakRef.TryGetTarget(out BufferSentinel? sentinel))
            {
                if (sentinel.IsReturned)
                {
                    continue;
                }

                survivors.Add(weakRef);

                long elapsed = now - sentinel.RentTimestamp;
                if (elapsed >= thresholdTicks)
                {
                    found++;
                    double elapsedSec = elapsed / (double)Stopwatch.Frequency;

                    string stack = "N/A (CaptureStackTrace=false)";
                    if (!string.IsNullOrEmpty(sentinel.StackTrace))
                    {
                        int firstLineEnd = sentinel.StackTrace.IndexOf('\n', StringComparison.Ordinal);
                        stack = firstLineEnd > 0
                            ? sentinel.StackTrace[..firstLineEnd].Trim()
                            : sentinel.StackTrace;
                    }

                    if (found <= 20)
                    {
                        _ = sb.AppendLine(CultureInfo.InvariantCulture,
                            $"{sentinel.Size,12} | {elapsedSec,11:F1} | {stack}");
                    }
                }
            }
        }

        if (_sentinelTracker.Count > 10000 && survivors.Count < _sentinelTracker.Count * 0.7)
        {
            ConcurrentBag<WeakReference<BufferSentinel>> newBag = new();
            foreach (WeakReference<BufferSentinel> wr in survivors)
            {
                newBag.Add(wr);
            }
            _sentinelTracker = newBag;
        }

        if (found > 20)
        {
            _ = sb.AppendLine(CultureInfo.InvariantCulture,
                $"... and {found - 20} more suspicious buffers.");
        }

        if (found == 0)
        {
            _ = sb.AppendLine("(None detected)");
        }

        _ = sb.AppendLine("----------------------------------------------------------------------------------------------");
        _ = sb.AppendLine();
    }

    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void APPEND_REPORT_HEADER(StringBuilder sb)
    {
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] BufferPoolManager Status:");
        _ = sb.AppendLine();

        _ = sb.AppendLine("======================================================================");
        _ = sb.AppendLine("Overall Statistics (ArrayPool.Shared wrapper)");
        _ = sb.AppendLine("======================================================================");
        (long rents, long returns, long bytesRented) = this.ReadCounters();

        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Rent Count                : {rents}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Return Count              : {returns}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Outstanding Count         : {rents - returns}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Total Bytes Rented        : {bytesRented}");

        double uptimeSec = (DateTime.UtcNow - _startTime).TotalSeconds;
        double throughputMBps = uptimeSec > 0 ? (double)bytesRented / (1024 * 1024) / uptimeSec : 0;
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Throughput                : {throughputMBps:F2} MB/s");
        _ = sb.AppendLine("======================================================================");
        _ = sb.AppendLine();
    }

    #endregion Private: Reporting
}
