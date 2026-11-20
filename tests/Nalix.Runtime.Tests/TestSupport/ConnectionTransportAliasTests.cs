// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Networking;
using Xunit;

namespace Nalix.Runtime.Tests.TestSupport;

/// <summary>
/// <see cref="IConnection.Transport"/> has a default implementation that forwards to the obsolete
/// <c>TCP</c> member, so implementations written before <c>Transport</c> existed keep working.
/// <see cref="FakeConnection"/> implements only <c>TCP</c>, like such an implementation.
/// </summary>
public sealed class ConnectionTransportAliasTests
{
    [Fact]
    public void Transport_WhenImplementationOnlyDefinesTcp_ReturnsTcpTransport()
    {
        FakeConnection fake = new();
        IConnection connection = fake;

        Assert.Same(fake.FakeTcp, connection.Transport);
    }
}
