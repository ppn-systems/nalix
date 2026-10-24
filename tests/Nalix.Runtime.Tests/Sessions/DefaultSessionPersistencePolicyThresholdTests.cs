// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using FluentAssertions;
using Nalix.Abstractions;
using Nalix.Abstractions.Networking;
using Nalix.Runtime.Sessions;
using NSubstitute;
using Xunit;

namespace Nalix.Runtime.Tests.Sessions;

public sealed class DefaultSessionPersistencePolicyThresholdTests
{
    [Fact]
    public void ShouldPersist_WhenHandshakeNotEstablished_ReturnsFalse()
    {
        // Arrange
        var mockConnection = Substitute.For<IConnection>();
        var attributes = Nalix.Framework.Memory.Objects.ObjectMap<AttributeKey, object>.Rent();
        mockConnection.Attributes.Returns(attributes);

        var policy = new DefaultSessionPersistencePolicy();

        // Act & Assert
        policy.ShouldPersist(mockConnection).Should().BeFalse();

        attributes.Return();
    }

    [Fact]
    public void ShouldPersist_WhenHandshakeEstablishedAndEnoughAttributes_ReturnsTrue()
    {
        // Arrange
        var mockConnection = Substitute.For<IConnection>();
        var attributes = Nalix.Framework.Memory.Objects.ObjectMap<AttributeKey, object>.Rent();
        
        var runtimeState = new Nalix.Runtime.Internal.RuntimeConnectionState();
        runtimeState.HandshakeEstablished = true;
        attributes[ConnectionAttributes.RuntimeState] = runtimeState;

        for (int i = 0; i < 20; i++)
        {
            attributes[AttributeKey.FromName($"key_{i}")] = i;
        }
        mockConnection.Attributes.Returns(attributes);

        var policy = new DefaultSessionPersistencePolicy();

        // Act & Assert
        policy.ShouldPersist(mockConnection).Should().BeTrue();

        attributes.Return();
    }

    /// <summary>
    /// Area 6 boundary: the policy check is <c>Count &lt;= MinAttributesForPersistence</c> — a count
    /// exactly at the configured threshold (including the RuntimeState internal flag) must still be
    /// rejected; the count must exceed it. The threshold is read from options rather than hardcoded,
    /// since the default is tunable (it moved from 20 to 10 in #333).
    /// </summary>
    [Fact]
    public void ShouldPersist_WhenAttributeCountExactlyAtThreshold_ReturnsFalse()
    {
        var mockConnection = Substitute.For<IConnection>();
        var attributes = Nalix.Framework.Memory.Objects.ObjectMap<AttributeKey, object>.Rent();

        var runtimeState = new Nalix.Runtime.Internal.RuntimeConnectionState();
        runtimeState.HandshakeEstablished = true;
        attributes[ConnectionAttributes.RuntimeState] = runtimeState;

        int threshold = Nalix.Environment.Configuration.ConfigurationManager.Instance
            .Get<Nalix.Runtime.Options.SessionStoreOptions>().MinAttributesForPersistence;

        // (threshold - 1) extra keys + 1 RuntimeState key = exactly threshold total.
        for (int i = 0; i < threshold - 1; i++)
        {
            attributes[AttributeKey.FromName($"key_{i}")] = i;
        }
        mockConnection.Attributes.Returns(attributes);
        attributes.Count.Should().Be(threshold);

        var policy = new DefaultSessionPersistencePolicy();

        policy.ShouldPersist(mockConnection).Should().BeFalse(
            $"Count <= MinAttributesForPersistence ({threshold} <= {threshold}) must reject, not just Count < MinAttributesForPersistence");

        // One more attribute crosses the threshold and must be accepted.
        attributes[AttributeKey.FromName("key_over")] = 0;
        policy.ShouldPersist(mockConnection).Should().BeTrue();

        attributes.Return();
    }
}

