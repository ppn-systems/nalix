// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions.Diagnostics;
using Nalix.Abstractions.Exceptions;
using Nalix.Environment.Configuration;
using Nalix.Environment.Time;
using Nalix.Framework.Injection;
using Nalix.Framework.Options;
using Nalix.Framework.Tasks;
using Nalix.Network.Internal.Transport;
using Nalix.Network.Options;

namespace Nalix.Network.RateLimiting;

public sealed partial class ConnectionGuard
{
    #region Initialization

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void INITIALIZE_METRICS()
    {
        _totalRejections = 0;
        _totalCleanedEntries = 0;
        _totalConnectionAttempts = 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void SCHEDULE_CLEANUP_JOB()
    {
        TaskManager taskManager = InstanceManager.Instance.GetOrCreateInstance<TaskManager>();

        _cleanupJob = InstanceManager.Instance.GetOrCreateInstance<TaskManager>().ScheduleRecurring(
            name: TaskNaming.Recurring.CleanupJobId(RecurringName, this.GetHashCode()),
            interval: _cleanupInterval,
            work: _ =>
            {
                this.RUN_CLEANUP_ONCE();
                return ValueTask.CompletedTask;
            },
            options: new RecurringOptions
            {
                NonReentrant = true,
                Tag = TaskNaming.Tags.Service,
                BackoffCap = TimeSpan.FromSeconds(15),
                Jitter = TimeSpan.FromMilliseconds(250),
                ExecutionTimeout = TimeSpan.FromSeconds(2)
            }
        );

        if (_banRepository.IsEnabled)
        {
            _saveJob = InstanceManager.Instance.GetOrCreateInstance<TaskManager>().ScheduleRecurring(
                name: TaskNaming.Recurring.CleanupJobId(RecurringName + ".save", this.GetHashCode()),
                interval: _banRepository.AutoSaveInterval,
                work: _ =>
                {
                    _banRepository.Save(_map);
                    return ValueTask.CompletedTask;
                },
                options: new RecurringOptions
                {
                    NonReentrant = true,
                    Tag = TaskNaming.Tags.Service,
                    ExecutionTimeout = TimeSpan.FromSeconds(10)
                }
            );
        }

        _ewmaJob = InstanceManager.Instance.GetOrCreateInstance<TaskManager>().ScheduleRecurring(
            name: TaskNaming.Recurring.CleanupJobId(RecurringName + ".ewma", this.GetHashCode()),
            interval: TimeSpan.FromSeconds(1),
            work: _ =>
            {
                this.UPDATE_EWMA_SHARED();
                return ValueTask.CompletedTask;
            },
            options: new RecurringOptions
            {
                NonReentrant = true,
                Tag = TaskNaming.Tags.Service,
                ExecutionTimeout = TimeSpan.FromSeconds(1)
            }
        );
    }

    #endregion Initialization

    #region Cleanup

    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void RUN_CLEANUP_ONCE()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            DateTime cutoff = Clock.NowUtc() - _inactivityThreshold;
            int scanned = 0;
            int removed = 0;

            int maxCleanupKeys = _config.MaxCleanupKeysPerRun > 0
                ? _config.MaxCleanupKeysPerRun
                : Math.Max(1000, _map.Count / 4);

            List<SocketEndpoint> keysToRemove = new(Math.Min(maxCleanupKeys, _map.Count));

            foreach (KeyValuePair<SocketEndpoint, ConnectionLimitEntry> kvp in _map)
            {
                if (scanned++ >= maxCleanupKeys)
                {
                    break;
                }

                bool lockTaken = false;
                bool shouldRemove = false;

                try
                {
                    kvp.Value.SpinLock.Enter(ref lockTaken);
                    shouldRemove = SHOULD_REMOVE_ENTRY(kvp.Value, cutoff);
                }
                finally
                {
                    if (lockTaken)
                    {
                        kvp.Value.SpinLock.Exit();
                    }
                }

                if (shouldRemove)
                {
                    keysToRemove.Add(kvp.Key);
                }
            }

            // Remove in separate pass to avoid holding locks
            foreach (SocketEndpoint key in keysToRemove)
            {
                if (_map.TryGetValue(key, out ConnectionLimitEntry? entry) && entry is not null)
                {
                    bool lockTaken = false;
                    bool canRemove = false;
                    try
                    {
                        entry.SpinLock.Enter(ref lockTaken);
                        // Double check under lock to prevent TOCTOU
                        if (SHOULD_REMOVE_ENTRY(entry, cutoff))
                        {
                            entry.IsRemoved = true;
                            canRemove = true;
                        }
                    }
                    finally
                    {
                        if (lockTaken)
                        {
                            entry.SpinLock.Exit();
                        }
                    }

                    if (canRemove)
                    {
                        if (_map.TryRemove(key, out ConnectionLimitEntry? removedEntry) && removedEntry is not null)
                        {
                            // Clear queue without lock since no one else can acquire it
                            removedEntry.RecentConnectionTimestamps.Clear();
                            removed++;
                        }
                    }
                }
            }

            long subnetCutoffTicks = Clock.NowUtc().Ticks - _windowTicks;
            int subnetRemoved = 0;

            foreach (KeyValuePair<uint, SubnetLimitEntry> kvp in _subnetMapV4)
            {
                if (SHOULD_REMOVE_SUBNET_ENTRY(kvp.Value, subnetCutoffTicks))
                {
                    bool lockTaken = false;
                    try
                    {
                        kvp.Value.SpinLock.Enter(ref lockTaken);
                        if (SHOULD_REMOVE_SUBNET_ENTRY(kvp.Value, subnetCutoffTicks))
                        {
                            kvp.Value.IsRemoved = true;
                            _ = _subnetMapV4.TryRemove(kvp.Key, out _);
                            subnetRemoved++;
                        }
                    }
                    finally
                    {
                        if (lockTaken)
                        {
                            kvp.Value.SpinLock.Exit();
                        }
                    }
                }
            }

            foreach (KeyValuePair<long, SubnetLimitEntry> kvp in _subnetMapV6)
            {
                if (SHOULD_REMOVE_SUBNET_ENTRY(kvp.Value, subnetCutoffTicks))
                {
                    bool lockTaken = false;
                    try
                    {
                        kvp.Value.SpinLock.Enter(ref lockTaken);
                        if (SHOULD_REMOVE_SUBNET_ENTRY(kvp.Value, subnetCutoffTicks))
                        {
                            kvp.Value.IsRemoved = true;
                            _ = _subnetMapV6.TryRemove(kvp.Key, out _);
                            subnetRemoved++;
                        }
                    }
                    finally
                    {
                        if (lockTaken)
                        {
                            kvp.Value.SpinLock.Exit();
                        }
                    }
                }
            }

            _ = Interlocked.Add(ref _totalCleanedEntries, removed);

            if (removed > 0 || subnetRemoved > 0)
            {
                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Debug))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Debug, new DiagnosticLog("NW.ConnectionGuard:Internal", $"cleanup-scanned scanned={scanned} removed={removed} subnet-removed={subnetRemoved} map-count={_map.Count}"));
                }
            }

            this.CORRECT_GLOBAL_COUNTER_DRIFT();
        }
        catch (Exception ex) when (ex is not ObjectDisposedException)
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.LoopFaulted))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.LoopFaulted, new DiagnosticLog("NW.ConnectionGuard:Internal", "cleanup-error", ex));
            }
        }
    }

    private void CORRECT_GLOBAL_COUNTER_DRIFT()
    {
        if (_maxGlobalConnections <= -1)
        {
            return;
        }

        int actualTotal = 0;
        foreach (KeyValuePair<SocketEndpoint, ConnectionLimitEntry> kvp in _map)
        {
            bool lockTaken = false;
            try
            {
                kvp.Value.SpinLock.Enter(ref lockTaken);
                actualTotal += kvp.Value.Info.CurrentConnections;
            }
            finally
            {
                if (lockTaken)
                {
                    kvp.Value.SpinLock.Exit();
                }
            }
        }

        // Per-endpoint slots include passthrough pseudo-connections, which have their own counter.
        actualTotal -= Volatile.Read(ref _passthroughConnections);

        int reported = Volatile.Read(ref _globalConnections);
        int drift = reported - actualTotal;
        if (Math.Abs(drift) > 5)
        {
            _ = Interlocked.Exchange(ref _globalConnections, actualTotal);
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Security.LimitDriftCorrected))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Security.LimitDriftCorrected, new DiagnosticLog("NW.ConnectionGuard:Internal", $"limit-drift-corrected reported={reported} actual={actualTotal}"));
            }
        }
    }

    /// <remarks>
    /// Info is read without lock — this is an intentional approximate check.
    /// On 64-bit CLR, struct reads up to 8 bytes are atomic. ConnectionLimitInfo
    /// is 24 bytes (int + DateTime + int), so a torn read is theoretically possible.
    /// For cleanup decisions, a false-negative (not removing) is safe; the entry
    /// will be reconsidered in the next cleanup cycle.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool SHOULD_REMOVE_ENTRY(ConnectionLimitEntry entry, DateTime cutoff)
    {
        long bannedUntil = Interlocked.Read(ref entry.BannedUntilTicks);
        if (bannedUntil > cutoff.Ticks)
        {
            return false;
        }

        // Read Info without lock — approximate check is fine for cleanup decisions.
        ConnectionLimitInfo info = entry.Info;
        return info.CurrentConnections <= 0 && info.LastConnectionTime < cutoff;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool SHOULD_REMOVE_SUBNET_ENTRY(SubnetLimitEntry entry, long cutoffTicks) => entry.CurrentConnections <= 0 && Volatile.Read(ref entry.LastSeenAtTicks) < cutoffTicks;

    #endregion Cleanup

    #region Hot Reload

    private DateTime _lastConfigReload = DateTime.MinValue;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void INITIALIZE_HOT_RELOAD()
    {
        TaskManager taskManager = InstanceManager.Instance.GetOrCreateInstance<TaskManager>();

        _hotReloadJob = InstanceManager.Instance.GetOrCreateInstance<TaskManager>().ScheduleRecurring(
            name: TaskNaming.Recurring.CleanupJobId(RecurringName + ".reload", this.GetHashCode()),
            interval: TimeSpan.FromSeconds(60),
            work: _ =>
            {
                this.CHECK_FILE_CHANGES();
                return ValueTask.CompletedTask;
            },
            options: new RecurringOptions
            {
                NonReentrant = true,
                Tag = TaskNaming.Tags.Service,
                Jitter = TimeSpan.FromSeconds(5)
            }
        );

        try
        {
            _configWatcher = new System.IO.FileSystemWatcher(Environment.IO.Directories.ConfigurationDirectory)
            {
                NotifyFilter = System.IO.NotifyFilters.LastWrite,
                Filter = "*.txt",
                EnableRaisingEvents = true
            };
            _configWatcher.Changed += this.OnFileChanged;
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Warning))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Warning, new DiagnosticLog("NW.ConnectionGuard:Internal", "watcher-init-failed relying-on-polling=true", ex));
            }
        }
    }

    private void OnFileChanged(object sender, System.IO.FileSystemEventArgs e)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        if (e.Name != null && (
            e.Name.Equals(ConfigurationManager.Instance.Get<ConnectionBlacklistStoreOptions>().StoreFileName, StringComparison.OrdinalIgnoreCase) ||
            e.Name.Equals(ConfigurationManager.Instance.Get<TrustedProxyOptions>().StoreFileName, StringComparison.OrdinalIgnoreCase)))
        {
            if (Interlocked.CompareExchange(ref _reloadPending, 1, 0) == 0)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(300).ConfigureAwait(false);
                    try
                    {
                        this.CHECK_FILE_CHANGES();
                    }
                    catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
                    {
                        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.LoopFaulted))
                        {
                            DiagnosticsEvents.Write(DiagnosticsEvents.Internal.LoopFaulted, new DiagnosticLog("NW.ConnectionGuard:OnFileChanged", "hot-reload-failed", ex));
                        }
                    }
                    finally
                    {
                        _ = Interlocked.Exchange(ref _reloadPending, 0);
                    }
                });
            }
        }
    }

    private void CHECK_FILE_CHANGES()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            string blacklistPath = System.IO.Path.Combine(Environment.IO.Directories.ConfigurationDirectory, ConfigurationManager.Instance.Get<ConnectionBlacklistStoreOptions>().StoreFileName);
            string proxiesPath = System.IO.Path.Combine(Environment.IO.Directories.ConfigurationDirectory, ConfigurationManager.Instance.Get<TrustedProxyOptions>().StoreFileName);

            DateTime maxConfigWrite = DateTime.MinValue;

            if (System.IO.File.Exists(blacklistPath))
            {
                DateTime lw = System.IO.File.GetLastWriteTimeUtc(blacklistPath);
                if (lw > maxConfigWrite)
                {
                    maxConfigWrite = lw;
                }
            }
            if (System.IO.File.Exists(proxiesPath))
            {
                DateTime lw = System.IO.File.GetLastWriteTimeUtc(proxiesPath);
                if (lw > maxConfigWrite)
                {
                    maxConfigWrite = lw;
                }
            }

            if (maxConfigWrite > _lastConfigReload)
            {
                _lastConfigReload = maxConfigWrite;
                _accessList.Reload();
            }
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.LoopFaulted))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.LoopFaulted, new DiagnosticLog("NW.ConnectionGuard:Internal", "hot-reload-check-failed", ex));
            }
        }
    }

    #endregion Hot Reload
}
