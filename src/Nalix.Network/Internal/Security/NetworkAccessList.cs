// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Nalix.Abstractions.Diagnostics;
using Nalix.Abstractions.Exceptions;
using Nalix.Environment.Configuration;
using Nalix.Environment.IO;
using Nalix.Network.Internal.Transport;
using Nalix.Network.Options;

namespace Nalix.Network.Internal.Security;

/// <summary>
/// Encapsulates blacklist loading and trusted proxy checking for connection filtering.
/// </summary>
internal sealed class NetworkAccessList
{

    private readonly TrustedProxyOptions _proxyConfig;
    private readonly ConnectionBlacklistStoreOptions _blacklistConfig;
    private volatile AccessListState _state;

    private sealed class AccessListState
    {
        public AccessListState(
            List<IPNetwork> trustedProxies,
            HashSet<IPAddress> blacklistedIps,
            HashSet<SocketEndpoint> blacklistedEndpoints,
            List<IPNetwork> blacklistedNetworks)
        {
            this.TrustedProxies = trustedProxies;
            this.BlacklistedIps = blacklistedIps;
            this.BlacklistedEndpoints = blacklistedEndpoints;
            this.BlacklistedNetworks = blacklistedNetworks;
        }

        public List<IPNetwork> TrustedProxies { get; }
        public HashSet<IPAddress> BlacklistedIps { get; }
        public HashSet<SocketEndpoint> BlacklistedEndpoints { get; }
        public List<IPNetwork> BlacklistedNetworks { get; }
    }

    public NetworkAccessList(TrustedProxyOptions proxyConfig)
    {
        _proxyConfig = proxyConfig;

        ConnectionBlacklistStoreOptions blacklistConfig = ConfigurationManager.Instance.Get<ConnectionBlacklistStoreOptions>();
        blacklistConfig.Validate();
        _blacklistConfig = blacklistConfig;

        List<IPNetwork> proxies = this.LoadTrustedProxies(proxyConfig);
        (HashSet<IPAddress> ips, HashSet<SocketEndpoint> endpoints, List<IPNetwork> networks) = this.LoadBlacklistedIps(blacklistConfig);

        _state = new AccessListState(proxies, ips, endpoints, networks);
    }

    #region APIs

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsBlacklisted(IPAddress address)
    {
        AccessListState state = _state;

        if (state.BlacklistedIps.Contains(address))
        {
            return true;
        }

        if (state.BlacklistedNetworks.Count > 0)
        {
            for (int i = 0; i < state.BlacklistedNetworks.Count; i++)
            {
                if (state.BlacklistedNetworks[i].Contains(address))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Zero-allocation blacklist check using SocketEndpoint.
    /// Exact-match uses HashSet&lt;SocketEndpoint&gt; (O(1), no alloc).
    /// CIDR match compares raw bytes against network prefix (no IPAddress alloc).
    /// Falls back to IPAddress only for CIDR networks (rare case).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public bool IsBlacklisted(SocketEndpoint endpoint)
    {
        AccessListState state = _state;

        // Fast path: exact match via SocketEndpoint HashSet — zero allocation.
        if (state.BlacklistedEndpoints.Contains(endpoint))
        {
            return true;
        }

        // Slow path: CIDR network matching from raw bytes.
        if (state.BlacklistedNetworks.Count > 0)
        {
            Span<byte> addrBytes = stackalloc byte[endpoint.IsIPv6 ? 16 : 4];
            _ = endpoint.TryWriteAddressBytes(addrBytes, out int written);

            for (int i = 0; i < state.BlacklistedNetworks.Count; i++)
            {
                if (CIDR_MATCH(addrBytes, written, state.BlacklistedNetworks[i]))
                {
                    return true;
                }
            }
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsTrustedProxy(IPAddress address)
    {
        AccessListState state = _state;

        if (state.TrustedProxies.Count > 0)
        {
            for (int i = 0; i < state.TrustedProxies.Count; i++)
            {
                if (state.TrustedProxies[i].Contains(address))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Zero-allocation trusted proxy check using SocketEndpoint.
    /// Compares raw bytes against network prefix — no IPAddress allocation.
    /// Falls back to IPAddress only when raw-byte comparison is unsupported.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public bool IsTrustedProxy(SocketEndpoint endpoint)
    {
        AccessListState state = _state;

        if (state.TrustedProxies.Count > 0)
        {
            Span<byte> addrBytes = stackalloc byte[endpoint.IsIPv6 ? 16 : 4];
            _ = endpoint.TryWriteAddressBytes(addrBytes, out int written);

            for (int i = 0; i < state.TrustedProxies.Count; i++)
            {
                if (CIDR_MATCH(addrBytes, written, state.TrustedProxies[i]))
                {
                    return true;
                }
            }
        }

        return false;
    }

    #endregion APIs

    #region CIDR Matching

    /// <summary>
    /// Checks whether an IP address (as raw bytes) falls within an IPNetwork CIDR range.
    /// Works directly from big-endian address bytes — no IPAddress allocation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool CIDR_MATCH(ReadOnlySpan<byte> addrBytes, int addrLen, IPNetwork network)
    {
        int prefixLen = network.PrefixLength;

        // Get network address bytes.
        IPAddress baseAddr = network.BaseAddress;
        Span<byte> netBytes = stackalloc byte[16];
        if (!baseAddr.TryWriteBytes(netBytes, out int netWritten))
        {
            return false;
        }

        // Address family mismatch: IPv4 address can't match IPv6 network and vice versa.
        if (addrLen != netWritten)
        {
            // Handle IPv4-mapped IPv6: network is /::ffff:x.x.x.y and address is 4 bytes.
            if (netWritten == 16 && addrLen == 4 && baseAddr.IsIPv4MappedToIPv6)
            {
                // Compare against the last 4 bytes of the IPv6 network address.
                return prefixLen >= 96 && MatchPrefix(addrBytes, 4, netBytes[12..], prefixLen - 96);
            }

            return false;
        }

        return MatchPrefix(addrBytes, addrLen, netBytes, prefixLen);
    }

    /// <summary>
    /// Compares the first <paramref name="prefixBits"/> bits of two big-endian byte spans.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool MatchPrefix(ReadOnlySpan<byte> a, int aLen, ReadOnlySpan<byte> b, int prefixBits)
    {
        if (prefixBits <= 0)
        {
            return true;
        }

        int maxBits = aLen * 8;
        if (prefixBits > maxBits)
        {
            prefixBits = maxBits;
        }

        int fullBytes = prefixBits / 8;
        int remainingBits = prefixBits % 8;

        for (int i = 0; i < fullBytes; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }

        if (remainingBits > 0 && fullBytes < aLen)
        {
            int mask = 0xFF << (8 - remainingBits);
            if ((a[fullBytes] & mask) != (b[fullBytes] & mask))
            {
                return false;
            }
        }

        return true;
    }

    #endregion CIDR Matching

    #region Loading Methods

    public void Reload()
    {
        try
        {
            AccessListState currentState = _state;
            List<IPNetwork>? proxies;
            HashSet<IPAddress>? ips;
            List<IPNetwork>? networks;

            try
            {
                proxies = this.LoadTrustedProxies(_proxyConfig, currentState.TrustedProxies);
            }
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
            {
                proxies = currentState.TrustedProxies; // Retain old state on failure
                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Warning))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Warning, new DiagnosticLog("NW.AccessListState:Reload", "reload-trusted-proxies-failed retaining-old-state=true", ex));
                }
            }

            HashSet<SocketEndpoint> endpoints;

            try
            {
                (ips, endpoints, networks) = this.LoadBlacklistedIps(_blacklistConfig, currentState.BlacklistedIps, currentState.BlacklistedNetworks);
            }
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
            {
                ips = currentState.BlacklistedIps; // Retain old state on failure
                endpoints = currentState.BlacklistedEndpoints;
                networks = currentState.BlacklistedNetworks;
                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Warning))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Warning, new DiagnosticLog("NW.AccessListState:Reload", "reload-blacklists-failed retaining-old-state=true", ex));
                }
            }

            _state = new AccessListState(proxies, ips, endpoints, networks);
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Error))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Error, new DiagnosticLog("NW.AccessListState:Reload", "reload-failed", ex));
            }
        }
    }

    private List<IPNetwork> LoadTrustedProxies(TrustedProxyOptions proxyConfig, List<IPNetwork>? existingProxies = null)
    {
        string path = Path.Combine(Directories.ConfigurationDirectory, proxyConfig.StoreFileName);
        if (!File.Exists(path))
        {
            NetworkStore.Save(path, Array.Empty<IPNetwork>(), "Trusted Proxies");
        }
        List<IPNetwork> networks = NetworkStore.Load(path, proxyConfig.MaxTrustedProxies);

        bool hasChanges = existingProxies == null || !AreNetworksEqual(networks, existingProxies);

        if (networks.Count > 0 && hasChanges && DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Information))
        {
            DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Information, new DiagnosticLog("NW.AccessListState:Internal", $"loaded trusted-proxies networks-count={networks.Count} source=disk"));
        }

        return networks;
    }

    private (HashSet<IPAddress>, HashSet<SocketEndpoint>, List<IPNetwork>) LoadBlacklistedIps(ConnectionBlacklistStoreOptions blacklistConfig, HashSet<IPAddress>? existingIps = null, List<IPNetwork>? existingNetworks = null)
    {
        HashSet<IPAddress> ips = new();
        HashSet<SocketEndpoint> endpoints = new();
        List<IPNetwork> netList = new();

        if (!blacklistConfig.Enabled)
        {
            return (ips, endpoints, netList);
        }

        string path = Path.Combine(Directories.ConfigurationDirectory, blacklistConfig.StoreFileName);
        if (!File.Exists(path))
        {
            NetworkStore.Save(path, Array.Empty<IPNetwork>(), "Blacklisted IPs/Networks");
        }
        List<IPNetwork> networks = NetworkStore.Load(path, blacklistConfig.MaxBlacklistedIps);

        foreach (IPNetwork network in networks)
        {
            if ((network.BaseAddress.AddressFamily == AddressFamily.InterNetwork && network.PrefixLength == 32) ||
                (network.BaseAddress.AddressFamily == AddressFamily.InterNetworkV6 && network.PrefixLength == 128))
            {
                _ = ips.Add(network.BaseAddress);
                _ = endpoints.Add(SocketEndpoint.FromIpAddress(network.BaseAddress));
            }
            else
            {
                netList.Add(network);
            }
        }

        bool hasChanges = existingIps == null || existingNetworks == null || !ips.SetEquals(existingIps) || !AreNetworksEqual(netList, existingNetworks);

        if (networks.Count > 0 && hasChanges && DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Information))
        {
            DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Information, new DiagnosticLog("NW.AccessListState:Internal", $"loaded networks-count={networks.Count} blacklisted-ips-count={ips.Count} blacklisted-networks-count={netList.Count} source=disk"));
        }

        return (ips, endpoints, netList);
    }

    private static bool AreNetworksEqual(List<IPNetwork> a, List<IPNetwork> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Count; i++)
        {
            if (!a[i].Equals(b[i]))
            {
                return false;
            }
        }

        return true;
    }

    #endregion Loading Methods
}
