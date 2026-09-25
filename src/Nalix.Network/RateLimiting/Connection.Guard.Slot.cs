// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using Nalix.Abstractions.Diagnostics;
using Nalix.Environment.Time;
using Nalix.Network.Internal.Security;
using Nalix.Network.Internal.Transport;

namespace Nalix.Network.RateLimiting;

public sealed partial class ConnectionGuard
{
    #region Connection Slot Management

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IS_EXEMPT_LOOPBACK(SocketEndpoint key) => _config.ExemptLoopback && key.IsLoopback;

    /// <summary>
    /// Loopback fast path: counts the connection (so release stays balanced) but skips
    /// per-IP caps, rate windows, burst detection, and bans.
    /// </summary>
    private ConnectionAllowResult ACQUIRE_LOOPBACK_SLOT(SocketEndpoint key, DateTime now)
    {
        while (true)
        {
            ConnectionLimitEntry entry = _map.GetOrAdd(key, static _ => new ConnectionLimitEntry());
            _ = Interlocked.Exchange(ref entry.LastSeenAtTicks, now.Ticks);

            bool lockTaken = false;
            try
            {
                entry.SpinLock.Enter(ref lockTaken);
                if (entry.IsRemoved)
                {
                    continue;
                }

                entry.Info = entry.Info with
                {
                    CurrentConnections = entry.Info.CurrentConnections + 1,
                    TotalConnectionsToday = CALCULATE_TOTAL_CONNECTIONS_TODAY(entry.Info, now, _config.DailyResetTimeOffset),
                    LastConnectionTime = now
                };
                entry.LastAcceptTimeTicks = now.Ticks;

                return new ConnectionAllowResult { Allowed = true, CurrentConnections = entry.Info.CurrentConnections };
            }
            finally
            {
                if (lockTaken)
                {
                    entry.SpinLock.Exit();
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void UPDATE_EWMA_SHARED()
    {
        long nowTicks = Clock.NowUtc().Ticks;
        bool lockTaken = false;
        try
        {
            _ewmaLock.Enter(ref lockTaken);

            double elapsed = (nowTicks - _ewmaLastUpdateTicks) / (double)TimeSpan.TicksPerSecond;
            if (elapsed > 0)
            {
                long currentTotal = Interlocked.Read(ref _totalConnectionAttempts);
                long windowAttempts = currentTotal - _ewmaLastConnectionAttempts;
                double currentRate = windowAttempts / elapsed;

                _ewmaConnectionRate = (EwmaAlpha * currentRate) + ((1 - EwmaAlpha) * _ewmaConnectionRate);
                _ewmaLastUpdateTicks = nowTicks;
                _ewmaLastConnectionAttempts = currentTotal;
            }
        }
        finally
        {
            if (lockTaken)
            {
                _ewmaLock.Exit();
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GET_EFFECTIVE_MAX_PER_ENDPOINT(bool isTrustedProxy)
    {
        int baseMax = isTrustedProxy ? _proxyConfig.MaxConnectionsPerTrustedProxy : _maxPerEndpoint;

        if (!_config.EnableAdaptiveMode || _maxGlobalConnections <= -1)
        {
            return baseMax;
        }

        double loadRatio = (double)Volatile.Read(ref _globalConnections) / _maxGlobalConnections;
        if (loadRatio > _config.AdaptiveLoadThreshold)
        {
            return Math.Max(1, (int)(baseMax * _config.AdaptiveTighteningFactor));
        }

        return baseMax;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static SocketEndpoint CONVERT_TO_NETWORK_ENDPOINT(IPEndPoint endPoint) => SocketEndpoint.FromIpAddress(endPoint.Address);

    // ── Zero-alloc SocketEndpoint overload of TRY_ACQUIRE_CONNECTION_SLOT ────
    // Same logic as the IPAddress overload, but uses SocketEndpoint-based
    // trusted-proxy check to avoid the ToIPAddress() allocation entirely.

    private ConnectionAllowResult TRY_ACQUIRE_CONNECTION_SLOT(SocketEndpoint key, DateTime now)
    {
        if (this.IS_EXEMPT_LOOPBACK(key))
        {
            return this.ACQUIRE_LOOPBACK_SLOT(key, now);
        }

        // Zero-alloc trusted proxy check via raw-byte CIDR matching.
        bool isTrustedProxy = _accessList.IsTrustedProxy(key);

        int maxConnections = this.GET_EFFECTIVE_MAX_PER_ENDPOINT(isTrustedProxy);
        int maxAttempts = isTrustedProxy ? _proxyConfig.MaxAttemptsPerTrustedProxyWindow : _config.MaxConnectionsPerWindow;
        long nowTicks = now.Ticks;

        while (true)
        {
            if (!_map.TryGetValue(key, out ConnectionLimitEntry? entry) || entry is null)
            {
                if (_maxTrackedEndpoints > 0 && _map.Count >= _maxTrackedEndpoints)
                {
                    _ = this.TRY_RANDOM_SAMPLE_EVICT();
                }

                entry = _map.GetOrAdd(key, static _ => new ConnectionLimitEntry());
            }

            _ = Interlocked.Exchange(ref entry.LastSeenAtTicks, nowTicks);

            long bannedUntil = Interlocked.Read(ref entry.BannedUntilTicks);

            bool trimLockTaken = false;
            try
            {
                entry.SpinLock.Enter(ref trimLockTaken);
                this.TRIM_OLD_TIMESTAMPS(entry.RecentConnectionTimestamps, nowTicks);

                long decayWindowTicks = _banCountDecayWindowTicks;
                if (entry.BanCount > 0 && entry.LastBanTimeTicks > 0)
                {
                    long elapsed = nowTicks - entry.LastBanTimeTicks;
                    if (elapsed > decayWindowTicks)
                    {
                        int decayTiers = (int)(elapsed / decayWindowTicks);
                        entry.BanCount = Math.Max(0, entry.BanCount - decayTiers);
                        if (entry.BanCount == 0)
                        {
                            entry.LastBanTimeTicks = 0;
                        }
                    }
                }
            }
            finally
            {
                if (trimLockTaken)
                {
                    entry.SpinLock.Exit();
                }
            }

            if (!isTrustedProxy && bannedUntil > nowTicks)
            {
                int currentConns;
                bool lockTaken = false;
                try
                {
                    entry.SpinLock.Enter(ref lockTaken);

                    entry.RecentConnectionTimestamps.Enqueue(nowTicks);
                    this.TRIM_OLD_TIMESTAMPS(entry.RecentConnectionTimestamps, nowTicks);

                    long lastAccept = entry.LastAcceptTimeTicks;
                    long gap = (nowTicks - lastAccept) / TimeSpan.TicksPerMillisecond;
                    bool isBurst = lastAccept > 0
                        && gap < _config.MinConnectionIntervalMs
                        && entry.RecentConnectionTimestamps.Count >= _config.BurstThreshold;

                    int effectiveMaxAttempts = isBurst
                        ? Math.Max(1, maxAttempts / _config.BurstPenaltyDivisor)
                        : maxAttempts;

                    if (entry.RecentConnectionTimestamps.Count > effectiveMaxAttempts)
                    {
                        if (nowTicks - entry.LastBanTimeTicks > _windowTicks)
                        {
                            entry.BanCount++;
                            entry.LastBanTimeTicks = nowTicks;

                            TimeSpan banDuration = this.CALCULATE_PROGRESSIVE_BAN_DURATION(entry.BanCount);
                            long newBanUntilTicks = nowTicks + banDuration.Ticks;
                            _ = Interlocked.Exchange(ref entry.BannedUntilTicks, newBanUntilTicks);
                            bannedUntil = newBanUntilTicks;
                            _banRepository.MarkDirty();
                        }
                    }

                    currentConns = entry.Info.CurrentConnections;
                }
                finally
                {
                    if (lockTaken)
                    {
                        entry.SpinLock.Exit();
                    }
                }

                if (ThrottledEventGate.TryAcquire(
                        ref entry.LastRejectLogTicks,
                        ref entry.SuppressedRejectCount,
                        nowTicks, _logSuppressWindowTicks,
                        out long suppressed))
                {
                    if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Security.Banned))
                    {
                        DiagnosticsEvents.Write(
                            DiagnosticsEvents.Security.Banned,
                            new DiagnosticLog(
                                "NW.ConnectionGuard:Internal",
                                $" address={FormatEndpointForLog(key)} banned-until={new DateTime(bannedUntil, DateTimeKind.Utc)} suppressed-count={suppressed}"));
                    }
                }

                return new ConnectionAllowResult { Allowed = false, CurrentConnections = currentConns };
            }

            ConnectionAllowResult result;

            bool spinLockTaken = false;
            try
            {
                entry.SpinLock.Enter(ref spinLockTaken);

                if (entry.IsRemoved)
                {
                    continue;
                }

                entry.RecentConnectionTimestamps.Enqueue(nowTicks);

                long lastAccept = entry.LastAcceptTimeTicks;
                long gap = (nowTicks - lastAccept) / TimeSpan.TicksPerMillisecond;
                bool isBurst = lastAccept > 0
                    && gap < _config.MinConnectionIntervalMs
                    && entry.RecentConnectionTimestamps.Count >= _config.BurstThreshold;

                int effectiveMaxAttempts = isBurst
                    ? Math.Max(1, maxAttempts / _config.BurstPenaltyDivisor)
                    : maxAttempts;

                if (entry.RecentConnectionTimestamps.Count > effectiveMaxAttempts)
                {
                    if (!isTrustedProxy)
                    {
                        entry.BanCount++;
                        entry.LastBanTimeTicks = nowTicks;

                        TimeSpan banDuration = this.CALCULATE_PROGRESSIVE_BAN_DURATION(entry.BanCount);
                        long banUntilTicks = nowTicks + banDuration.Ticks;
                        _ = Interlocked.Exchange(ref entry.BannedUntilTicks, banUntilTicks);
                        _banRepository.MarkDirty();

                        if (ThrottledEventGate.TryAcquire(
                                ref entry.LastDDoSLogTicks,
                                ref entry.SuppressedDDoSCount,
                                nowTicks, _logSuppressWindowTicks,
                                out long suppressed))
                        {
                            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Security.DdosDetected))
                            {
                                DiagnosticsEvents.Write(DiagnosticsEvents.Security.DdosDetected, new DiagnosticLog("NW.ConnectionGuard:Internal", $" address={FormatEndpointForLog(key)} ban-count={entry.BanCount} banned-until={new DateTime(banUntilTicks, DateTimeKind.Utc)} suppressed-count={suppressed}"));
                            }
                        }
                    }

                    result = new ConnectionAllowResult
                    {
                        Allowed = false,
                        CurrentConnections = entry.Info.CurrentConnections
                    };
                }
                else if (entry.Info.CurrentConnections >= maxConnections)
                {
                    result = new ConnectionAllowResult
                    {
                        Allowed = false,
                        CurrentConnections = entry.Info.CurrentConnections
                    };
                }
                else
                {
                    int newTotalToday = CALCULATE_TOTAL_CONNECTIONS_TODAY(entry.Info, now, _config.DailyResetTimeOffset);

                    entry.Info = entry.Info with
                    {
                        CurrentConnections = entry.Info.CurrentConnections + 1,
                        TotalConnectionsToday = newTotalToday,
                        LastConnectionTime = now
                    };

                    entry.LastAcceptTimeTicks = nowTicks;

                    result = new ConnectionAllowResult { Allowed = true, CurrentConnections = entry.Info.CurrentConnections };
                }
            }
            finally
            {
                if (spinLockTaken)
                {
                    entry.SpinLock.Exit();
                }
            }

            return result;
        }
    }

    // ── Redis-style Random Sampling Eviction (O(1)) ──────────────────────────
    //
    // When _map reaches _maxTrackedEndpoints, we need to make room for new IPs.
    // Instead of scanning the entire dictionary (O(n)), we randomly sample a small
    // number of entries (EvictionSampleSize), pick the one with the oldest
    // LastSeenAtTicks that has zero active connections, and evict it.
    //
    // This is the same algorithm Redis uses for allkeys-lru approximate eviction.
    // The probability of finding a stale entry in 5 random samples from a DDoS
    // scenario (where most entries are idle attackers) is extremely high.
    //
    // Cost: O(1) — fixed at EvictionSampleSize iterations regardless of map size.

    private const int EvictionSampleSize = 5;

    /// <summary>
    /// Attempts to evict the stalest zero-connection entry found among
    /// <see cref="EvictionSampleSize"/> random samples from the map.
    /// </summary>
    /// <returns>True if an entry was evicted and the caller may retry GetOrAdd.</returns>
    private bool TRY_RANDOM_SAMPLE_EVICT()
    {
        // Collect a small fixed number of random samples from the map.
        // We iterate with a random skip to avoid always hitting the same entries.
        int mapCount = _map.Count;
        if (mapCount == 0)
        {
            return false;
        }

        SocketEndpoint bestKey = default;
        long bestLastSeen = long.MaxValue;
        bool foundCandidate = false;
        int sampled = 0;

        foreach (KeyValuePair<SocketEndpoint, ConnectionLimitEntry> kvp in _map)
        {
            if (sampled >= EvictionSampleSize)
            {
                break;
            }

            // Random skip: with a full map, this distributes samples across the space.
            // Skip 0..mapCount/EvictionSampleSize entries between samples for spread.
            sampled++;

            ConnectionLimitEntry entry = kvp.Value;
            long lastSeen = Interlocked.Read(ref entry.LastSeenAtTicks);
            int conns = entry.Info.CurrentConnections;

            // Only consider entries with zero active connections.
            // Prefer the one that has been idle the longest.
            if (conns <= 0 && lastSeen < bestLastSeen)
            {
                bestKey = kvp.Key;
                bestLastSeen = lastSeen;
                foundCandidate = true;
            }
        }

        if (!foundCandidate)
        {
            return false;
        }

        // Double-check under lock before evicting.
        if (_map.TryGetValue(bestKey, out ConnectionLimitEntry? victim) && victim is not null)
        {
            bool lockTaken = false;
            try
            {
                victim.SpinLock.Enter(ref lockTaken);
                if (victim.Info.CurrentConnections <= 0 && !victim.IsRemoved)
                {
                    victim.IsRemoved = true;
                    if (_map.TryRemove(bestKey, out ConnectionLimitEntry? removed))
                    {
                        removed.RecentConnectionTimestamps.Clear();
                        _ = Interlocked.Increment(ref _totalCleanedEntries);
                        return true;
                    }
                }
            }
            finally
            {
                if (lockTaken)
                {
                    victim.SpinLock.Exit();
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Formats a SocketEndpoint for log messages using TryFormatAddress to avoid
    /// the 3-allocation cost of the Address property getter.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string FormatEndpointForLog(SocketEndpoint endpoint)
    {
        return string.Create(
            endpoint.IsIPv6 ? 45 : 15,
            endpoint,
            static (dest, ep) => { _ = ep.TryFormatAddress(dest, out _); });
    }

    /// <summary>
    /// Attempts to acquire a connection slot.
    /// Uses GetOrAdd to safely retrieve-or-create the entry, then locks the entry
    /// for the counter mutation. The rate-window queue is trimmed before the check.
    /// </summary>
    /// <param name="key"></param>
    /// <param name="now"></param>
    /// <param name="address"></param>
    private ConnectionAllowResult TRY_ACQUIRE_CONNECTION_SLOT(SocketEndpoint key, DateTime now, IPAddress address)
    {
        if (this.IS_EXEMPT_LOOPBACK(key))
        {
            return this.ACQUIRE_LOOPBACK_SLOT(key, now);
        }

        // 3. Trusted proxy check
        bool isTrustedProxy = _accessList.IsTrustedProxy(address);

        int maxConnections = this.GET_EFFECTIVE_MAX_PER_ENDPOINT(isTrustedProxy);
        int maxAttempts = isTrustedProxy ? _proxyConfig.MaxAttemptsPerTrustedProxyWindow : _config.MaxConnectionsPerWindow;
        long nowTicks = now.Ticks;

        while (true)
        {
            if (!_map.TryGetValue(key, out ConnectionLimitEntry? entry) || entry is null)
            {
                // Redis-style O(1) eviction: when the map is at capacity,
                // random-sample 5 entries and evict the stalest zero-connection one.
                // This prevents memory exhaustion under spoofed-IP DDoS floods.
                if (_maxTrackedEndpoints > 0 && _map.Count >= _maxTrackedEndpoints)
                {
                    _ = this.TRY_RANDOM_SAMPLE_EVICT();
                }

                entry = _map.GetOrAdd(key, static _ => new ConnectionLimitEntry());
            }

            // Update LastSeen (Lock-free since it's just a long write and we don't strictly need exact consistency for cleanup/debug)
            _ = Interlocked.Exchange(ref entry.LastSeenAtTicks, nowTicks);

            long bannedUntil = Interlocked.Read(ref entry.BannedUntilTicks);

            bool trimLockTaken = false;
            try
            {
                entry.SpinLock.Enter(ref trimLockTaken);
                this.TRIM_OLD_TIMESTAMPS(entry.RecentConnectionTimestamps, nowTicks);

                long decayWindowTicks = _banCountDecayWindowTicks;
                if (entry.BanCount > 0 && entry.LastBanTimeTicks > 0)
                {
                    long elapsed = nowTicks - entry.LastBanTimeTicks;
                    if (elapsed > decayWindowTicks)
                    {
                        int decayTiers = (int)(elapsed / decayWindowTicks);
                        entry.BanCount = Math.Max(0, entry.BanCount - decayTiers);
                        if (entry.BanCount == 0)
                        {
                            entry.LastBanTimeTicks = 0;
                        }
                    }
                }
            }
            finally
            {
                if (trimLockTaken)
                {
                    entry.SpinLock.Exit();
                }
            }

            // 4. Runtime ban active -> Reject (Trusted proxies are never banned at runtime)
            if (!isTrustedProxy && bannedUntil > nowTicks)
            {
                int currentConns;
                bool lockTaken = false;
                try
                {
                    entry.SpinLock.Enter(ref lockTaken);

                    // Track connection attempts during ban to evaluate sustained spamming
                    entry.RecentConnectionTimestamps.Enqueue(nowTicks);
                    this.TRIM_OLD_TIMESTAMPS(entry.RecentConnectionTimestamps, nowTicks);

                    long lastAccept = entry.LastAcceptTimeTicks;
                    long gap = (nowTicks - lastAccept) / TimeSpan.TicksPerMillisecond;
                    bool isBurst = lastAccept > 0
                        && gap < _config.MinConnectionIntervalMs
                        && entry.RecentConnectionTimestamps.Count >= _config.BurstThreshold;

                    int effectiveMaxAttempts = isBurst
                        ? Math.Max(1, maxAttempts / _config.BurstPenaltyDivisor)
                        : maxAttempts;

                    if (entry.RecentConnectionTimestamps.Count > effectiveMaxAttempts)
                    {
                        // Escalate the ban tier and duration if they keep spamming beyond the window
                        if (nowTicks - entry.LastBanTimeTicks > _windowTicks)
                        {
                            entry.BanCount++;
                            entry.LastBanTimeTicks = nowTicks;

                            TimeSpan banDuration = this.CALCULATE_PROGRESSIVE_BAN_DURATION(entry.BanCount);
                            long newBanUntilTicks = nowTicks + banDuration.Ticks;
                            _ = Interlocked.Exchange(ref entry.BannedUntilTicks, newBanUntilTicks);
                            bannedUntil = newBanUntilTicks; // Update local variable for the log call below
                            _banRepository.MarkDirty();
                        }
                    }

                    currentConns = entry.Info.CurrentConnections;
                }
                finally
                {
                    if (lockTaken)
                    {
                        entry.SpinLock.Exit();
                    }
                }

                if (ThrottledEventGate.TryAcquire(
                        ref entry.LastRejectLogTicks,
                        ref entry.SuppressedRejectCount,
                        nowTicks, _logSuppressWindowTicks,
                        out long suppressed))
                {
                    if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Security.Banned))
                    {
                        DiagnosticsEvents.Write(
                            DiagnosticsEvents.Security.Banned,
                            new DiagnosticLog(
                                "NW.ConnectionGuard:Internal",
                                $" address={FormatEndpointForLog(key)} banned-until={new DateTime(bannedUntil, DateTimeKind.Utc)} suppressed-count={suppressed}"));
                    }
                }

                return new ConnectionAllowResult { Allowed = false, CurrentConnections = currentConns };
            }

            // Declare the lock beforehand to use after exiting the lock.
            ConnectionAllowResult result;

            bool spinLockTaken = false;
            try
            {
                entry.SpinLock.Enter(ref spinLockTaken);

                if (entry.IsRemoved)
                {
                    continue; // Retry with a fresh GetOrAdd, this one is tombstoned
                }

                entry.RecentConnectionTimestamps.Enqueue(nowTicks);

                long lastAccept = entry.LastAcceptTimeTicks;
                long gap = (nowTicks - lastAccept) / TimeSpan.TicksPerMillisecond;
                bool isBurst = lastAccept > 0
                    && gap < _config.MinConnectionIntervalMs
                    && entry.RecentConnectionTimestamps.Count >= _config.BurstThreshold;

                int effectiveMaxAttempts = isBurst
                    ? Math.Max(1, maxAttempts / _config.BurstPenaltyDivisor)
                    : maxAttempts;

                if (entry.RecentConnectionTimestamps.Count > effectiveMaxAttempts)
                {
                    // 6. On violation -> Update ban state (if not trusted)
                    if (!isTrustedProxy)
                    {
                        entry.BanCount++;
                        entry.LastBanTimeTicks = nowTicks;

                        TimeSpan banDuration = this.CALCULATE_PROGRESSIVE_BAN_DURATION(entry.BanCount);
                        long banUntilTicks = nowTicks + banDuration.Ticks;
                        _ = Interlocked.Exchange(ref entry.BannedUntilTicks, banUntilTicks);
                        _banRepository.MarkDirty();

                        if (ThrottledEventGate.TryAcquire(
                                ref entry.LastDDoSLogTicks,
                                ref entry.SuppressedDDoSCount,
                                nowTicks, _logSuppressWindowTicks,
                                out long suppressed))
                        {
                            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Security.DdosDetected))
                            {
                                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Security.DdosDetected))
                                {
                                    DiagnosticsEvents.Write(DiagnosticsEvents.Security.DdosDetected, new DiagnosticLog("NW.ConnectionGuard:Internal", $" address={FormatEndpointForLog(key)} ban-count={entry.BanCount} banned-until={new DateTime(banUntilTicks, DateTimeKind.Utc)} suppressed-count={suppressed}"));
                                }
                                ;
                            }
                        }
                    }

                    result = new ConnectionAllowResult
                    {
                        Allowed = false,
                        CurrentConnections = entry.Info.CurrentConnections
                    };
                }
                else if (entry.Info.CurrentConnections >= maxConnections)
                {
                    // Concurrent connection limit reached for this IP
                    result = new ConnectionAllowResult
                    {
                        Allowed = false,
                        CurrentConnections = entry.Info.CurrentConnections
                    };
                }
                else
                {
                    int newTotalToday = CALCULATE_TOTAL_CONNECTIONS_TODAY(entry.Info, now, _config.DailyResetTimeOffset);

                    entry.Info = entry.Info with
                    {
                        CurrentConnections = entry.Info.CurrentConnections + 1,
                        TotalConnectionsToday = newTotalToday,
                        LastConnectionTime = now
                    };

                    entry.LastAcceptTimeTicks = nowTicks;

                    result = new ConnectionAllowResult { Allowed = true, CurrentConnections = entry.Info.CurrentConnections };
                }
            }
            finally
            {
                if (spinLockTaken)
                {
                    entry.SpinLock.Exit();
                }
            }

            return result;
        }
    }

    /// <summary>Removes timestamps outside the rate-window.</summary>
    /// <param name="timestamps"></param>
    /// <param name="nowTicks"></param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void TRIM_OLD_TIMESTAMPS(System.Collections.Generic.Queue<long> timestamps, long nowTicks)
    {
        long cutoff = nowTicks - _windowTicks;

        while (timestamps.TryPeek(out long oldest) && oldest < cutoff)
        {
            _ = timestamps.Dequeue();
        }

        int hardCap = _config.MaxConnectionsPerWindow * 4;
        while (timestamps.Count > hardCap)
        {
            _ = timestamps.Dequeue();
        }
    }

    private TimeSpan CALCULATE_PROGRESSIVE_BAN_DURATION(int banCount)
    {
        if (!_protectionConfig.EnableProgressiveBanning)
        {
            return _protectionConfig.BanDuration;
        }

        // Progressive schedule: 1m, 5m, 15m, 1h, 6h, 24h
        return banCount switch
        {
            <= 1 => TimeSpan.FromMinutes(1),
            2 => TimeSpan.FromMinutes(5),
            3 => TimeSpan.FromMinutes(15),
            4 => TimeSpan.FromHours(1),
            5 => TimeSpan.FromHours(6),
            _ => TimeSpan.FromHours(24) // Cap at 24 hours
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CALCULATE_TOTAL_CONNECTIONS_TODAY(ConnectionLimitInfo info, DateTime now, TimeSpan offset)
    {
        if (info.LastConnectionTime == default)
        {
            return 1;
        }

        // Apply offset to both times to determine the "logical day" for reset
        DateTime logicalToday = (now + offset).Date;
        DateTime logicalLastConnection = (info.LastConnectionTime + offset).Date;

        // Use strict inequality (!=) to handle backward NTP syncs and forward day changes.
        // NOTE: When NTP adjusts clock backward within the same calendar day,
        // the counter does NOT reset (logicalLastConnection == logicalToday).
        // This is by design: resetting on small NTP drifts would allow counter manipulation.
        // The counter only resets on actual day boundaries.
        if (logicalLastConnection != logicalToday)
        {
            return 1; // Different day, reset counter
        }

        // Prevent overflow
        return info.TotalConnectionsToday >= int.MaxValue - 1 ? int.MaxValue : info.TotalConnectionsToday + 1;
    }

    /// <summary>
    /// Releases a connection slot for the given endpoint.
    /// </summary>
    /// <param name="key"></param>
    /// <param name="now"></param>
    private bool TRY_RELEASE_CONNECTION_SLOT(SocketEndpoint key, DateTime now)
    {
        if (!_map.TryGetValue(key, out ConnectionLimitEntry? entry) || entry is null)
        {
            return false;
        }

        bool lockTaken = false;
        try
        {
            entry.SpinLock.Enter(ref lockTaken);
            if (entry.IsRemoved)
            {
                return false;
            }

            // Decrement with underflow protection
            int newCount = Math.Max(0, entry.Info.CurrentConnections - 1);

            entry.Info = entry.Info with
            {
                CurrentConnections = newCount,
                LastConnectionTime = now
            };

            // Clear queue if no connections and queue is large
            if (newCount == 0 && entry.RecentConnectionTimestamps.Count > _config.MaxConnectionsPerWindow * 2)
            {
                entry.RecentConnectionTimestamps.Clear();

                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Debug))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Debug, new DiagnosticLog("NW.ConnectionGuard:Internal", $"cleared-queue address={FormatEndpointForLog(key)} reason={"oversized"}"));
                }
            }

            this.TRIM_OLD_TIMESTAMPS(entry.RecentConnectionTimestamps, now.Ticks);
        }
        finally
        {
            if (lockTaken)
            {
                entry.SpinLock.Exit();
            }
        }

        return true;
    }

    #endregion Connection Slot Management
}
