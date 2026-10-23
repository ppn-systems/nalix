// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Environment.Configuration;
using Nalix.Network.Options;
using Xunit;

[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly, MaxParallelThreads = 1)]

namespace Nalix.SDK.Tests;

internal static class TestAssemblySetup
{
    /// <summary>
    /// Override connection limits after Bootstrap has switched to server.ini.
    /// Call this from each test class constructor.
    /// </summary>
    internal static void EnsureHighLimits()
    {
        // Update cached config object in-memory only.
        // Do NOT write to INI or flush — FileSystemWatcher would trigger reload.
        ConnectionQuotaOptions quota = ConfigurationManager.Instance.Get<ConnectionQuotaOptions>();
        quota.MaxConnectionsPerIpAddress = 10000;
        quota.MaxConnectionsPerWindow = 10000000;

        NetworkCallbackOptions callbacks = ConfigurationManager.Instance.Get<NetworkCallbackOptions>();
        callbacks.MaxPerConnectionPendingPackets = 1024;
    }
}
