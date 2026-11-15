// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using Nalix.Network.Options;
using Xunit;

namespace Nalix.Network.Tests.Options;

public sealed class ProxyProtocolOptionsTests
{
    [Fact]
    public void Validate_WhenDisabledWithDefaults_DoesNotThrow()
    {
        ProxyProtocolOptions options = new();

        options.Validate();

        Assert.False(options.Enabled);
        Assert.False(options.RequireTrustedProxy);
    }

    [Fact]
    public void Validate_WhenEnabledWithoutRequireTrustedProxy_ThrowsArgumentException()
    {
        ProxyProtocolOptions options = new()
        {
            Enabled = true,
            RequireTrustedProxy = false
        };

        _ = Assert.Throws<ArgumentException>(options.Validate);
    }

    [Fact]
    public void Validate_WhenEnabledWithRequireTrustedProxy_DoesNotThrow()
    {
        ProxyProtocolOptions options = new()
        {
            Enabled = true,
            RequireTrustedProxy = true
        };

        options.Validate();
    }

    [Fact]
    public void Validate_WhenDisabledWithRequireTrustedProxy_DoesNotThrow()
    {
        ProxyProtocolOptions options = new()
        {
            Enabled = false,
            RequireTrustedProxy = true
        };

        options.Validate();
    }
}
