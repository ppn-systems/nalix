// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using Nalix.Network.Internal.Transport;

namespace Nalix.Network.RateLimiting;

public sealed partial class ConnectionGuard
{
    private readonly ConcurrentDictionary<uint, SubnetLimitEntry> _subnetMapV4 = new();
    private readonly ConcurrentDictionary<long, SubnetLimitEntry> _subnetMapV6 = new();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint EXTRACT_IPV4_SUBNET_KEY(IPAddress address)
    {
        // /24 -> shift by 8 bits
#pragma warning disable CS0618
        uint ip = (uint)address.Address;
#pragma warning restore CS0618
        // Need to be careful with endianness. The address.Address is usually in network byte order.
        // It's just a grouping key, so any stable 24-bit extraction is fine.
        Span<byte> bytes = stackalloc byte[4];
        _ = address.TryWriteBytes(bytes, out _);
        return (uint)((bytes[0] << 16) | (bytes[1] << 8) | bytes[2]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long EXTRACT_IPV6_SUBNET_KEY(IPAddress address)
    {
        // /48 -> top 48 bits
        Span<byte> bytes = stackalloc byte[16];
        _ = address.TryWriteBytes(bytes, out _);
        long key = 0;
        for (int i = 0; i < 6; i++)
        {
            key = (key << 8) | bytes[i];
        }
        return key;
    }

    private SubnetAllowResult TRY_ACQUIRE_SUBNET_SLOT(IPAddress address, long nowTicks)
    {
        int maxConnections = _config.MaxConnectionsPerSubnet;
        int maxAttempts = _config.MaxSubnetConnectionsPerWindow;

        // Dual-stack sockets (DualMode) surface IPv4 clients as IPv4-mapped-IPv6
        // (::ffff:a.b.c.d), whose AddressFamily is InterNetworkV6. Unmap first so
        // this matches SocketEndpoint.FromIpAddress's normalization -- otherwise
        // this method increments the IPv6 subnet map while Release() (which goes
        // through the normalized SocketEndpoint key) decrements the IPv4 map,
        // permanently leaking one subnet slot per request until the real limit
        // (default 50) is hit and every subsequent connection is rejected forever.
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        bool isIpv4 = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;

        while (true)
        {
            SubnetLimitEntry? entry;
            if (isIpv4)
            {
                uint key = EXTRACT_IPV4_SUBNET_KEY(address);
                if (!_subnetMapV4.TryGetValue(key, out entry) || entry is null)
                {
                    if (_maxTrackedEndpoints > 0 && _subnetMapV4.Count >= _maxTrackedEndpoints)
                    {
                        _ = this.TRY_RANDOM_SAMPLE_EVICT_SUBNET(_subnetMapV4);
                    }

                    entry = _subnetMapV4.GetOrAdd(key, static _ => new SubnetLimitEntry());
                }
            }
            else
            {
                long key = EXTRACT_IPV6_SUBNET_KEY(address);
                if (!_subnetMapV6.TryGetValue(key, out entry) || entry is null)
                {
                    if (_maxTrackedEndpoints > 0 && _subnetMapV6.Count >= _maxTrackedEndpoints)
                    {
                        _ = this.TRY_RANDOM_SAMPLE_EVICT_SUBNET(_subnetMapV6);
                    }

                    entry = _subnetMapV6.GetOrAdd(key, static _ => new SubnetLimitEntry());
                }
            }

            _ = Interlocked.Exchange(ref entry.LastSeenAtTicks, nowTicks);

            SubnetAllowResult result;
            bool spinLockTaken = false;
            try
            {
                entry.SpinLock.Enter(ref spinLockTaken);

                if (entry.IsRemoved)
                {
                    continue;
                }

                this.TRIM_OLD_TIMESTAMPS(entry.RecentConnectionTimestamps, nowTicks, maxAttempts);

                if (entry.RecentConnectionTimestamps.Count >= maxAttempts)
                {
                    result = new SubnetAllowResult { Allowed = false };
                }
                else if (entry.CurrentConnections >= maxConnections)
                {
                    result = new SubnetAllowResult { Allowed = false };
                }
                else
                {
                    entry.CurrentConnections++;
                    entry.RecentConnectionTimestamps.Enqueue(nowTicks);
                    result = new SubnetAllowResult { Allowed = true };
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

    /// <summary>
    /// Zero-alloc overload: extracts subnet key from SocketEndpoint raw bytes
    /// via TryGetSubnetKey instead of going through IPAddress.
    /// </summary>
    private SubnetAllowResult TRY_ACQUIRE_SUBNET_SLOT(SocketEndpoint endpoint, long nowTicks)
    {
        if (!endpoint.TryGetSubnetKey(out uint ipv4Key, out long ipv6Key, out bool isV6))
        {
            return new SubnetAllowResult { Allowed = true }; // Empty endpoint — skip
        }

        int maxConnections = _config.MaxConnectionsPerSubnet;
        int maxAttempts = _config.MaxSubnetConnectionsPerWindow;

        while (true)
        {
            SubnetLimitEntry? entry;
            if (!isV6)
            {
                if (!_subnetMapV4.TryGetValue(ipv4Key, out entry) || entry is null)
                {
                    if (_maxTrackedEndpoints > 0 && _subnetMapV4.Count >= _maxTrackedEndpoints)
                    {
                        _ = this.TRY_RANDOM_SAMPLE_EVICT_SUBNET(_subnetMapV4);
                    }

                    entry = _subnetMapV4.GetOrAdd(ipv4Key, static _ => new SubnetLimitEntry());
                }
            }
            else
            {
                if (!_subnetMapV6.TryGetValue(ipv6Key, out entry) || entry is null)
                {
                    if (_maxTrackedEndpoints > 0 && _subnetMapV6.Count >= _maxTrackedEndpoints)
                    {
                        _ = this.TRY_RANDOM_SAMPLE_EVICT_SUBNET(_subnetMapV6);
                    }

                    entry = _subnetMapV6.GetOrAdd(ipv6Key, static _ => new SubnetLimitEntry());
                }
            }

            _ = Interlocked.Exchange(ref entry.LastSeenAtTicks, nowTicks);

            SubnetAllowResult result;
            bool spinLockTaken = false;
            try
            {
                entry.SpinLock.Enter(ref spinLockTaken);

                if (entry.IsRemoved)
                {
                    continue;
                }

                this.TRIM_OLD_TIMESTAMPS(entry.RecentConnectionTimestamps, nowTicks, maxAttempts);

                if (entry.RecentConnectionTimestamps.Count >= maxAttempts)
                {
                    result = new SubnetAllowResult { Allowed = false };
                }
                else if (entry.CurrentConnections >= maxConnections)
                {
                    result = new SubnetAllowResult { Allowed = false };
                }
                else
                {
                    entry.CurrentConnections++;
                    entry.RecentConnectionTimestamps.Enqueue(nowTicks);
                    result = new SubnetAllowResult { Allowed = true };
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

    private void TRY_RELEASE_SUBNET_SLOT(IPAddress address, DateTime now)
    {
        bool isIpv4 = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
        SubnetLimitEntry? entry;
        if (isIpv4)
        {
            uint key = EXTRACT_IPV4_SUBNET_KEY(address);
            _ = _subnetMapV4.TryGetValue(key, out entry);
        }
        else
        {
            long key = EXTRACT_IPV6_SUBNET_KEY(address);
            _ = _subnetMapV6.TryGetValue(key, out entry);
        }

        if (entry is null)
        {
            return;
        }

        this.RELEASE_SUBNET_ENTRY(entry, now);
    }

    /// <summary>
    /// Releases a subnet entry. Called from OnConnectionClosed/Release after
    /// obtaining the entry via TryGetSubnetKey (zero-alloc path).
    /// </summary>
    private void RELEASE_SUBNET_ENTRY(SubnetLimitEntry entry, DateTime now)
    {
        bool lockTaken = false;
        try
        {
            entry.SpinLock.Enter(ref lockTaken);
            if (entry.IsRemoved)
            {
                return;
            }

            entry.CurrentConnections = Math.Max(0, entry.CurrentConnections - 1);

            if (entry.CurrentConnections == 0 && entry.RecentConnectionTimestamps.Count > _config.MaxSubnetConnectionsPerWindow * 2)
            {
                entry.RecentConnectionTimestamps.Clear();
            }

            this.TRIM_OLD_TIMESTAMPS(entry.RecentConnectionTimestamps, now.Ticks, _config.MaxSubnetConnectionsPerWindow);
        }
        finally
        {
            if (lockTaken)
            {
                entry.SpinLock.Exit();
            }
        }
    }

    private bool TRY_RANDOM_SAMPLE_EVICT_SUBNET<TKey>(ConcurrentDictionary<TKey, SubnetLimitEntry> subnetMap)
        where TKey : notnull
    {
        int mapCount = subnetMap.Count;
        if (mapCount == 0)
        {
            return false;
        }

        TKey bestKey = default!;
        long bestLastSeen = long.MaxValue;
        bool foundCandidate = false;

        TKey fallbackKey = default!;
        bool hasFallback = false;

        int sampled = 0;
        int maxSkip = Math.Max(1, mapCount / EvictionSampleSize);
        int skip = mapCount > EvictionSampleSize ? Random.Shared.Next(0, maxSkip) : 0;

        foreach (KeyValuePair<TKey, SubnetLimitEntry> kvp in subnetMap)
        {
            if (sampled >= EvictionSampleSize)
            {
                break;
            }

            SubnetLimitEntry entry = kvp.Value;
            if (Volatile.Read(ref entry.CurrentConnections) > 0)
            {
                continue;
            }

            long lastSeen = Interlocked.Read(ref entry.LastSeenAtTicks);
            if (!hasFallback)
            {
                fallbackKey = kvp.Key;
                hasFallback = true;
            }

            if (skip > 0)
            {
                skip--;
                continue;
            }

            sampled++;
            skip = mapCount > EvictionSampleSize ? Random.Shared.Next(0, maxSkip) : 0;

            if (lastSeen < bestLastSeen)
            {
                bestKey = kvp.Key;
                bestLastSeen = lastSeen;
                foundCandidate = true;
            }
        }

        if (!foundCandidate)
        {
            if (!hasFallback)
            {
                return false;
            }

            bestKey = fallbackKey;
        }

        if (subnetMap.TryGetValue(bestKey, out SubnetLimitEntry? victim) && victim is not null)
        {
            bool lockTaken = false;
            try
            {
                victim.SpinLock.Enter(ref lockTaken);
                if (victim.CurrentConnections <= 0 && !victim.IsRemoved)
                {
                    victim.IsRemoved = true;
                    if (subnetMap.TryRemove(bestKey, out SubnetLimitEntry? removed) && removed is not null)
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
}
