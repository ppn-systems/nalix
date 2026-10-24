// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using FluentAssertions;
using Nalix.Abstractions;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Security;
using Nalix.Runtime.Sessions;
using Nalix.Runtime.Tests.TestSupport;
using Xunit;

namespace Nalix.Runtime.Tests.Sessions;

public sealed class DefaultSessionPersistencePolicyTests
{
    [Fact]
    public void ShouldPersist_WhenHandshakeEstablishedWithSecret_ReturnsTrueUnderDefaultOptions()
    {
        DefaultSessionPersistencePolicy policy = new();

        byte[] secretBytes = new byte[32];
        secretBytes[0] = 0x77;
        using FakeConnection connection = new()
        {
            Secret = new Bytes32(secretBytes),
            Level = PermissionLevel.ESTABLISHED
        };
        ConnectionRuntimeStateAccessor.SetHandshakeEstablished(connection, true);
        AttributeKey sessionTokenKey = new(0x8888UL);
        connection.Attributes[sessionTokenKey] = 12345UL;

        bool shouldPersist = policy.ShouldPersist(connection);

        shouldPersist.Should().BeTrue("a connection with an established cryptographic handshake (non-zero Secret) must be persisted under default options");
    }
}
