// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.IO;
using System.Net;
using FluentAssertions;
using Nalix.Environment.IO;
using Nalix.Network.Internal.Security;
using Nalix.Network.Options;
using Nalix.Network.Tests.TestSupport;
using Xunit;

namespace Nalix.Network.Tests.Internal.Security;

[Collection(TestCollections.NetworkConfig)]
public sealed class NetworkAccessListTests
{
    [Fact]
    public void IsBlacklisted_WhenIpBlacklistedInFile_ReturnsTrue()
    {
        using ConfigurationOptionsScope<ConnectionBlacklistStoreOptions> blacklistScope = new();
        ConnectionBlacklistStoreOptions blacklistOptions = blacklistScope.Options;
        blacklistOptions.Enabled = true;
        blacklistOptions.StoreFileName = "blacklist_nal_test.txt";
        blacklistOptions.MaxBlacklistedIps = 10;

        using ConfigurationOptionsScope<TrustedProxyOptions> proxyScope = new();
        TrustedProxyOptions proxyOptions = proxyScope.Options;
        proxyOptions.StoreFileName = "trusted_proxies_nal_test.txt";

        string blacklistPath = Path.Combine(Directories.ConfigurationDirectory, blacklistOptions.StoreFileName);
        string proxyPath = Path.Combine(Directories.ConfigurationDirectory, proxyOptions.StoreFileName);
        string? dir = Path.GetDirectoryName(blacklistPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            _ = Directory.CreateDirectory(dir);
        }

        File.WriteAllLines(blacklistPath, new[]
        {
            "# Test Blacklist",
            "1.2.3.4",
            "   ",
            "# Another Comment",
            "5.6.7.8"
        });

        try
        {
            NetworkAccessList accessList = new(proxyOptions);

            accessList.IsBlacklisted(IPAddress.Parse("1.2.3.4")).Should().BeTrue();
            accessList.IsBlacklisted(IPAddress.Parse("5.6.7.8")).Should().BeTrue();
            accessList.IsBlacklisted(IPAddress.Parse("10.0.0.1")).Should().BeFalse();
        }
        finally
        {
            if (File.Exists(blacklistPath))
            {
                File.Delete(blacklistPath);
            }
            if (File.Exists(proxyPath))
            {
                File.Delete(proxyPath);
            }
        }
    }

    [Fact]
    public void IsBlacklisted_WhenIpInBlacklistedCidr_ReturnsTrue()
    {
        using ConfigurationOptionsScope<ConnectionBlacklistStoreOptions> blacklistScope = new();
        ConnectionBlacklistStoreOptions blacklistOptions = blacklistScope.Options;
        blacklistOptions.Enabled = true;
        blacklistOptions.StoreFileName = "blacklist_cidr_nal_test.txt";
        blacklistOptions.MaxBlacklistedIps = 10;

        using ConfigurationOptionsScope<TrustedProxyOptions> proxyScope = new();
        TrustedProxyOptions proxyOptions = proxyScope.Options;
        proxyOptions.StoreFileName = "trusted_proxies_nal_test.txt";

        string blacklistPath = Path.Combine(Directories.ConfigurationDirectory, blacklistOptions.StoreFileName);
        string proxyPath = Path.Combine(Directories.ConfigurationDirectory, proxyOptions.StoreFileName);
        string? dir = Path.GetDirectoryName(blacklistPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            _ = Directory.CreateDirectory(dir);
        }

        File.WriteAllLines(blacklistPath, new[]
        {
            "# Test Blacklist CIDR",
            "192.168.1.0/24",
            "2001:db8::/32"
        });

        try
        {
            NetworkAccessList accessList = new(proxyOptions);

            accessList.IsBlacklisted(IPAddress.Parse("192.168.1.5")).Should().BeTrue();
            accessList.IsBlacklisted(IPAddress.Parse("192.168.1.99")).Should().BeTrue();
            accessList.IsBlacklisted(IPAddress.Parse("192.168.2.1")).Should().BeFalse();
            accessList.IsBlacklisted(IPAddress.Parse("2001:db8::1")).Should().BeTrue();
            accessList.IsBlacklisted(IPAddress.Parse("2001:db9::1")).Should().BeFalse();
        }
        finally
        {
            if (File.Exists(blacklistPath))
            {
                File.Delete(blacklistPath);
            }
            if (File.Exists(proxyPath))
            {
                File.Delete(proxyPath);
            }
        }
    }

    [Fact]
    public void IsTrustedProxy_WhenIpInTrustedProxiesFile_ReturnsTrue()
    {
        using ConfigurationOptionsScope<ConnectionBlacklistStoreOptions> blacklistScope = new();
        ConnectionBlacklistStoreOptions blacklistOptions = blacklistScope.Options;
        blacklistOptions.Enabled = false;
        blacklistOptions.StoreFileName = "blacklist_nal_test.txt";

        using ConfigurationOptionsScope<TrustedProxyOptions> proxyScope = new();
        TrustedProxyOptions proxyOptions = proxyScope.Options;
        proxyOptions.StoreFileName = "trusted_proxies_nal_test.txt";
        proxyOptions.MaxTrustedProxies = 10;

        string proxyPath = Path.Combine(Directories.ConfigurationDirectory, proxyOptions.StoreFileName);
        string? dir = Path.GetDirectoryName(proxyPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            _ = Directory.CreateDirectory(dir);
        }

        File.WriteAllLines(proxyPath, new[]
        {
            "# Test Trusted Proxies",
            "10.0.0.0/24",
            "192.168.100.5"
        });

        try
        {
            NetworkAccessList accessList = new(proxyOptions);

            accessList.IsTrustedProxy(IPAddress.Parse("10.0.0.50")).Should().BeTrue();
            accessList.IsTrustedProxy(IPAddress.Parse("192.168.100.5")).Should().BeTrue();
            accessList.IsTrustedProxy(IPAddress.Parse("1.2.3.4")).Should().BeFalse();
        }
        finally
        {
            if (File.Exists(proxyPath))
            {
                File.Delete(proxyPath);
            }
        }
    }

    [Fact]
    public void Ctor_WhenFilesDoNotExist_CreatesThem()
    {
        using ConfigurationOptionsScope<ConnectionBlacklistStoreOptions> blacklistScope = new();
        ConnectionBlacklistStoreOptions blacklistOptions = blacklistScope.Options;
        blacklistOptions.Enabled = true;
        blacklistOptions.StoreFileName = "blacklist_autocreate_test.txt";

        using ConfigurationOptionsScope<TrustedProxyOptions> proxyScope = new();
        TrustedProxyOptions proxyOptions = proxyScope.Options;
        proxyOptions.StoreFileName = "trusted_proxies_autocreate_test.txt";

        string blacklistPath = Path.Combine(Directories.ConfigurationDirectory, blacklistOptions.StoreFileName);
        string proxyPath = Path.Combine(Directories.ConfigurationDirectory, proxyOptions.StoreFileName);

        if (File.Exists(blacklistPath)) File.Delete(blacklistPath);
        if (File.Exists(proxyPath)) File.Delete(proxyPath);

        try
        {
            NetworkAccessList accessList = new(proxyOptions);

            File.Exists(blacklistPath).Should().BeTrue();
            File.Exists(proxyPath).Should().BeTrue();

            string blacklistContent = File.ReadAllText(blacklistPath);
            blacklistContent.Should().Contain("# Nalix Connection Guard - Blacklisted IPs/Networks");

            string proxyContent = File.ReadAllText(proxyPath);
            proxyContent.Should().Contain("# Nalix Connection Guard - Trusted Proxies");
        }
        finally
        {
            if (File.Exists(blacklistPath)) File.Delete(blacklistPath);
            if (File.Exists(proxyPath)) File.Delete(proxyPath);
        }
    }

    [Fact]
    public void Reload_WhenFileChanges_UpdatesState()
    {
        using ConfigurationOptionsScope<ConnectionBlacklistStoreOptions> blacklistScope = new();
        ConnectionBlacklistStoreOptions blacklistOptions = blacklistScope.Options;
        blacklistOptions.Enabled = true;
        blacklistOptions.StoreFileName = "blacklist_reload_test.txt";
        blacklistOptions.MaxBlacklistedIps = 10;

        using ConfigurationOptionsScope<TrustedProxyOptions> proxyScope = new();
        TrustedProxyOptions proxyOptions = proxyScope.Options;
        proxyOptions.StoreFileName = "trusted_proxies_reload_test.txt";
        proxyOptions.MaxTrustedProxies = 10;

        string blacklistPath = Path.Combine(Directories.ConfigurationDirectory, blacklistOptions.StoreFileName);
        string proxyPath = Path.Combine(Directories.ConfigurationDirectory, proxyOptions.StoreFileName);

        if (File.Exists(blacklistPath)) File.Delete(blacklistPath);
        if (File.Exists(proxyPath)) File.Delete(proxyPath);

        try
        {
            File.WriteAllLines(blacklistPath, new[] { "1.2.3.4" });
            File.WriteAllLines(proxyPath, new[] { "10.0.0.0/24" });

            NetworkAccessList accessList = new(proxyOptions);

            accessList.IsBlacklisted(IPAddress.Parse("1.2.3.4")).Should().BeTrue();
            accessList.IsBlacklisted(IPAddress.Parse("5.6.7.8")).Should().BeFalse();
            accessList.IsTrustedProxy(IPAddress.Parse("10.0.0.5")).Should().BeTrue();
            accessList.IsTrustedProxy(IPAddress.Parse("192.168.1.1")).Should().BeFalse();

            File.WriteAllLines(blacklistPath, new[] { "5.6.7.8" });
            File.WriteAllLines(proxyPath, new[] { "192.168.1.0/24" });

            accessList.Reload();

            accessList.IsBlacklisted(IPAddress.Parse("1.2.3.4")).Should().BeFalse();
            accessList.IsBlacklisted(IPAddress.Parse("5.6.7.8")).Should().BeTrue();
            accessList.IsTrustedProxy(IPAddress.Parse("10.0.0.5")).Should().BeFalse();
            accessList.IsTrustedProxy(IPAddress.Parse("192.168.1.1")).Should().BeTrue();
        }
        finally
        {
            if (File.Exists(blacklistPath)) File.Delete(blacklistPath);
            if (File.Exists(proxyPath)) File.Delete(proxyPath);
        }
    }
}
