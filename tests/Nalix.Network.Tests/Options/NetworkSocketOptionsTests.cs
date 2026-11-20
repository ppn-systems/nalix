// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics.CodeAnalysis;
using FluentAssertions;
using Nalix.Network.Options;
using Xunit;

namespace Nalix.Network.Tests.Options;

[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
public sealed class NetworkSocketOptionsTests
{
    [Fact]
    public void ReusePort_WhenDefault_IsFalse()
    {
        new NetworkSocketOptions().ReusePort.Should().BeFalse("port sharing lets another local process of the same user share the listener port");
    }
}
