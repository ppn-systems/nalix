// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Networking;
using Xunit;

namespace Nalix.Network.Tests.Connections;

public sealed class NetworkEndpointInterfaceTests
{
    [Fact]
    public void Properties_WhenReadThroughInterface_ReturnImplementationValues()
    {
        INetworkEndpoint endpoint = new TestEndpoint("10.0.0.5", 27015, HasPort: true, IsIPv6: false);

        Assert.Equal("10.0.0.5", endpoint.Address);
        Assert.Equal(27015, endpoint.Port);
        Assert.True(endpoint.HasPort);
        Assert.False(endpoint.IsIPv6);
    }

    private sealed record TestEndpoint(string Address, int Port = 0, bool HasPort = false, bool IsIPv6 = false) : INetworkEndpoint
    {
    }
}
