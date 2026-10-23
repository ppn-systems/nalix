// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Network.Options;
using Xunit;

namespace Nalix.Network.Tests.Options;

public sealed class ConnectionQuotaOptionsTests
{
    [Fact]
    public void Validate_WithDefaultValues_AcceptsDefaults()
    {
        ConnectionQuotaOptions options = new();

        options.Validate();

        Assert.Equal(10, options.MaxConnectionsPerIpAddress);
        Assert.Equal(10, options.MaxConnectionsPerWindow);
    }
}
