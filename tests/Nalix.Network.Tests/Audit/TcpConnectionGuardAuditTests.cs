// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using FluentAssertions;
using Nalix.Environment.Configuration;
using Nalix.Network.Internal.Transport;
using Nalix.Network.Options;
using Nalix.Network.RateLimiting;
using Xunit;

namespace Nalix.Network.Tests.Audit;

/// <summary>
/// Audit reproductions for the TCP side of <see cref="ConnectionGuard"/>.
/// Each test is expected to FAIL on the current code and documents a defect in docs/audit/network-tcp.md.
/// Private state is reached via reflection because the guard exposes no inspection surface for ban tiers.
/// </summary>
[Collection(TestCollections.NetworkConfig)]
[Trait("Category", "Audit")]
public sealed class TcpConnectionGuardAuditTests
{
    private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    // ConnectionLimitEntry is internal; resolve it from the guard's own _map type argument.
    private static readonly Type s_entryType =
        typeof(ConnectionGuard).GetField("_map", BindingFlags.Instance | BindingFlags.NonPublic)!
            .FieldType.GetGenericArguments()[1];

    private static IPAddress GetUniqueIp()
    {
        byte[] b = Guid.NewGuid().ToByteArray();
        byte first = (byte)(b[0] % 223 + 1);
        if (first == 127)
        {
            first = 126; // loopback is exempt from quotas by default
        }

        return new IPAddress(new byte[] { first, b[1], b[2], b[3] });
    }

    private static ConnectionQuotaOptions PermissiveQuota() => new()
    {
        MaxConnectionsPerIpAddress = 1000,
        MaxConnectionsPerWindow = 1000,
        MaxConnectionsPerSubnet = 1000,
        MaxSubnetConnectionsPerWindow = 1000,
    };

    /// <summary>Returns the internal per-IP entry for <paramref name="ip"/>, or null if none exists.</summary>
    private static object? GetEntry(ConnectionGuard guard, IPAddress ip)
    {
        object map = typeof(ConnectionGuard).GetField("_map", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(guard)!;
        MethodInfo tryGet = map.GetType().GetMethod("TryGetValue", AnyInstance)!;
        object?[] args = { SocketEndpoint.FromIpAddress(ip), null };
        bool found = (bool)tryGet.Invoke(map, args)!;
        return found ? args[1] : null;
    }

    private static bool MapContains(ConnectionGuard guard, IPAddress ip) => GetEntry(guard, ip) is not null;

    private static void SetField(object target, string name, object value) =>
        target.GetType().GetField(name, AnyInstance)!.SetValue(target, value);

    private static int GetInt(object target, string name) =>
        (int)target.GetType().GetField(name, AnyInstance)!.GetValue(target)!;

    private static void RunCleanupOnce(ConnectionGuard guard) =>
        typeof(ConnectionGuard).GetMethod("RUN_CLEANUP_ONCE", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(guard, null);

    /// <summary>
    /// NETTCP-01: The ban-count decay block in TRY_ACQUIRE_CONNECTION_SLOT subtracts
    /// <c>elapsed / decayWindow</c> from <c>BanCount</c> but never advances <c>LastBanTimeTicks</c>.
    /// Every subsequent connection attempt therefore re-subtracts the same tiers, so a single
    /// elapsed decay window erodes the progressive ban count on every accept, not once per window.
    /// </summary>
    [Fact]
    public void NETTCP01_BanCountDecay_IsAppliedOncePerElapsedWindow_NotOnEveryAttempt()
    {
        using ConnectionGuard guard = new(PermissiveQuota());
        IPAddress ip = GetUniqueIp();
        IPEndPoint endpoint = new(ip, 40000);

        guard.TryAccept(endpoint).Should().BeTrue();
        guard.Release(endpoint);

        object entry = GetEntry(guard, ip)!;
        TimeSpan decayWindow = ConfigurationManager.Instance.Get<ConnectionBanStoreOptions>().BanCountDecayWindow;

        // Three full decay windows have elapsed since the last ban -> exactly 3 tiers should decay, once.
        SetField(entry, "BanCount", 10);
        SetField(entry, "LastBanTimeTicks", DateTime.UtcNow.Ticks - (3 * decayWindow.Ticks) - TimeSpan.TicksPerMinute);

        guard.TryAccept(endpoint).Should().BeTrue();
        int afterFirstAttempt = GetInt(entry, "BanCount");

        guard.TryAccept(endpoint).Should().BeTrue();
        int afterSecondAttempt = GetInt(entry, "BanCount");

        afterFirstAttempt.Should().Be(7, "three elapsed windows should decay the count from 10 to 7");
        afterSecondAttempt.Should().Be(afterFirstAttempt,
            "the same elapsed decay window must not be subtracted again on a later connection attempt");
    }

    /// <summary>
    /// NETTCP-02: <c>SHOULD_REMOVE_ENTRY</c> (used by <c>RUN_CLEANUP_ONCE</c>) removes any idle, unbanned
    /// entry, ignoring <c>BanCount</c>. The eviction sampler (<c>TRY_RANDOM_SAMPLE_EVICT</c>) explicitly
    /// protects entries with <c>BanCount &gt; 0</c>, so the two paths disagree. A banned-then-quiet client
    /// loses its escalation state after the inactivity threshold (5 min default), so the next offense
    /// restarts at the 1-minute tier.
    /// </summary>
    [Fact]
    public void NETTCP02_Cleanup_DoesNotEvictIdleEntry_ThatCarriesEscalatedBanCount()
    {
        ConnectionQuotaOptions options = PermissiveQuota();
        options.InactivityThreshold = TimeSpan.FromSeconds(1);

        using ConnectionGuard guard = new(options);
        IPAddress ip = GetUniqueIp();
        IPEndPoint endpoint = new(ip, 40001);

        guard.TryAccept(endpoint).Should().BeTrue();
        guard.Release(endpoint);

        object entry = GetEntry(guard, ip)!;
        SetField(entry, "BanCount", 3);
        SetField(entry, "BannedUntilTicks", 0L); // ban has expired

        // Make the entry idle for well beyond the inactivity threshold.
        Type infoType = s_entryType.GetField("Info", AnyInstance)!.FieldType;
        object staleInfo = Activator.CreateInstance(infoType, 0, DateTime.UtcNow.AddMinutes(-10), 0)!;
        SetField(entry, "Info", staleInfo);

        RunCleanupOnce(guard);

        MapContains(guard, ip).Should().BeTrue(
            "an idle entry that still holds an escalated ban count must survive cleanup");
    }

    /// <summary>
    /// NETTCP-02 follow-up: retention of a <c>BanCount &gt; 0</c> entry must be bounded. Once its remaining
    /// tiers have decayed away, an idle entry is removed by cleanup.
    /// </summary>
    [Fact]
    public void NETTCP02_Cleanup_RemovesIdleEntry_OnceBanCountHasDecayed()
    {
        ConnectionQuotaOptions options = PermissiveQuota();
        options.InactivityThreshold = TimeSpan.FromSeconds(1);

        using ConnectionGuard guard = new(options);
        IPAddress ip = GetUniqueIp();
        IPEndPoint endpoint = new(ip, 40002);

        guard.TryAccept(endpoint).Should().BeTrue();
        guard.Release(endpoint);

        object entry = GetEntry(guard, ip)!;
        TimeSpan decayWindow = ConfigurationManager.Instance.Get<ConnectionBanStoreOptions>().BanCountDecayWindow;

        // Two tiers, last banned well beyond two decay windows ago -> all tiers have decayed.
        SetField(entry, "BanCount", 2);
        SetField(entry, "LastBanTimeTicks", DateTime.UtcNow.Ticks - (2 * decayWindow.Ticks) - TimeSpan.TicksPerMinute);
        SetField(entry, "BannedUntilTicks", 0L);

        Type infoType = s_entryType.GetField("Info", AnyInstance)!.FieldType;
        object staleInfo = Activator.CreateInstance(infoType, 0, DateTime.UtcNow.AddMinutes(-10), 0)!;
        SetField(entry, "Info", staleInfo);

        RunCleanupOnce(guard);

        MapContains(guard, ip).Should().BeFalse(
            "an idle entry whose ban tiers have fully decayed must be removed so memory stays bounded");
    }
}
