// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Environment.Configuration;
using Nalix.Network.Options;
using Xunit;

[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly, MaxParallelThreads = 1)]

namespace Nalix.Network.Tests;

internal static class TestAssemblySetup
{
    internal static void EnsureHighLimits()
    {
        ConnectionQuotaOptions quota = ConfigurationManager.Instance.Get<ConnectionQuotaOptions>();
        quota.MaxConnectionsPerIpAddress = 10000;
        quota.MaxConnectionsPerWindow = 10000000;
    }
}
