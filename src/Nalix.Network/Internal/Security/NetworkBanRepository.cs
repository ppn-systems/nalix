// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Nalix.Abstractions.Diagnostics;
using Nalix.Abstractions.Exceptions;
using Nalix.Environment.Configuration;
using Nalix.Environment.IO;
using Nalix.Environment.Time;
using Nalix.Network.Internal.Transport;
using Nalix.Network.Options;
using Nalix.Network.RateLimiting;

namespace Nalix.Network.Internal.Security;

/// <summary>
/// Encapsulates the loading, saving, and dirty-state tracking of network bans.
/// </summary>
internal sealed class NetworkBanRepository
{

    private readonly ConnectionBanStoreOptions _storeConfig;
    private readonly string _filePath;
    private int _persistenceDirty;

    public NetworkBanRepository()
    {

        _storeConfig = ConfigurationManager.Instance.Get<ConnectionBanStoreOptions>();
        _storeConfig.Validate();
        _filePath = Path.Combine(Directories.DataDirectory, _storeConfig.StoreFileName);
    }

    public bool IsEnabled => _storeConfig.Enabled;

    public TimeSpan AutoSaveInterval => _storeConfig.AutoSaveInterval;

    public void MarkDirty() => _ = Interlocked.Exchange(ref _persistenceDirty, 1);

    public void Load(ConcurrentDictionary<SocketEndpoint, ConnectionGuard.ConnectionLimitEntry> map)
    {
        if (!_storeConfig.Enabled)
        {
            return;
        }

        DateTime now = Clock.NowUtc();
        List<NetworkBanRecord> records;

        try
        {
            records = NetworkBanStore.Load(_filePath, _storeConfig.MaxPersistedBans, _storeConfig.BanCountDecayWindow, now.Ticks);
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Warning))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Warning, new DiagnosticLog("NW.NetworkBanRepository:Load", $"ban file corrupted or unreadable, renaming to .corrupt file=file-path={_filePath}", ex));
            }

            try
            {
                if (File.Exists(_filePath))
                {
                    string corruptPath = _filePath + $".corrupt.{now:yyyyMMddHHmmss}";
                    File.Move(_filePath, corruptPath, overwrite: true);
                }
            }
            catch (Exception renameEx) when (ExceptionClassifier.IsNonFatal(renameEx))
            {
                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Debug))
                {
                    // renameEx, "[NW.NetworkBanRepository] failed to rename corrupt file");
                }
            }

            // Force save current memory state to a fresh file
            this.Save(map, force: true);

            return;
        }

        foreach (NetworkBanRecord record in records)
        {
            ConnectionGuard.ConnectionLimitEntry entry = new()
            {
                BannedUntilTicks = record.BannedUntilTicks,
                BanCount = record.BanCount,
                LastBanTimeTicks = record.LastBanTimeTicks,
                LastSeenAtTicks = record.LastSeenAtTicks
            };

            _ = map.TryAdd(SocketEndpoint.FromNetworkEndpoint(record.Endpoint), entry);
        }

        if (records.Count > 0 && DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Information))
        {
            DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Information, new DiagnosticLog("NW.NetworkBanRepository:Load", $"Loaded records-count={records.Count} persisted bans."));
        }
    }

    public void Save(ConcurrentDictionary<SocketEndpoint, ConnectionGuard.ConnectionLimitEntry> map, bool force = false)
    {
        if (!force && Interlocked.CompareExchange(ref _persistenceDirty, 0, 1) == 0)
        {
            return; // No changes to save
        }

        try
        {
            List<NetworkBanRecord> snapshot = new();
            DateTime now = Clock.NowUtc();

            // Minimal lock to collect snapshot
            foreach (KeyValuePair<SocketEndpoint, ConnectionGuard.ConnectionLimitEntry> kvp in map)
            {
                long bannedUntil = Interlocked.Read(ref kvp.Value.BannedUntilTicks);
                int banCount = kvp.Value.BanCount;

                // Only save entries that are currently banned or have a progressive ban count (to retain decay state)
                if (bannedUntil > now.Ticks || banCount > 0)
                {
                    bool lockTaken = false;
                    long lastBanTime;
                    long lastSeen;

                    try
                    {
                        kvp.Value.SpinLock.Enter(ref lockTaken);
                        lastBanTime = kvp.Value.LastBanTimeTicks;
                        lastSeen = Interlocked.Read(ref kvp.Value.LastSeenAtTicks);
                    }
                    finally
                    {
                        if (lockTaken)
                        {
                            kvp.Value.SpinLock.Exit();
                        }
                    }

                    snapshot.Add(new NetworkBanRecord(kvp.Key, bannedUntil, banCount, lastBanTime, lastSeen));
                }
            }

            if (snapshot.Count > 0)
            {
                NetworkBanStore.Save(_filePath, snapshot, snapshot.Count);

                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Debug))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Debug, new DiagnosticLog("NW.NetworkBanRepository:Save", $"Persisted snapshot-count={snapshot.Count} bans to disk."));
                }
            }
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            // Restore dirty flag if save failed
            _ = Interlocked.Exchange(ref _persistenceDirty, 1);
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Error))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Error, new DiagnosticLog("NW.NetworkBanRepository:Save", "failed to save banned ips.", ex));
            }
        }
    }
}







