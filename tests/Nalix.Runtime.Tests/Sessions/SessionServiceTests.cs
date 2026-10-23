// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Threading.Tasks;
using FluentAssertions;
using Nalix.Abstractions;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Sessions;
using Nalix.Runtime.Sessions;
using NSubstitute;
using Xunit;

namespace Nalix.Runtime.Tests.Sessions;

public sealed class SessionServiceTests
{
    private sealed class CustomPolicy : ISessionPersistencePolicy
    {
        public bool ShouldPersist(IConnection connection)
        {
            return connection.Attributes.ContainsKey(AttributeKey.FromName("should_save"));
        }
    }

    [Fact]
    public async Task SaveSessionAsync_WithCustomPolicy_RespectsCustomLogic()
    {
        // Arrange
        var mockConnection = Substitute.For<IConnection>();
        var attributes = Nalix.Framework.Memory.Objects.ObjectMap<AttributeKey, object>.Rent();
        mockConnection.Attributes.Returns(attributes);
        mockConnection.IsDisposed.Returns(false);

        var policy = new CustomPolicy();
        var store = new InMemorySessionStore();
        var service = new SessionService(store: store, policy: policy);

        // Act & Assert (Should not persist because "should_save" is missing)
        await service.SaveSessionAsync(mockConnection);
        var report1 = service.GenerateReport();
        report1.Should().Contain("Total Stores Rejected   : 1");

        // Act & Assert (Should persist because "should_save" is present)
        attributes[AttributeKey.FromName("should_save")] = true;
        await service.SaveSessionAsync(mockConnection);
        var report2 = service.GenerateReport();
        report2.Should().Contain("Total Stores Succeeded  : 1");

        attributes.Return();
    }

    [Fact]
    public async Task GenerateReport_AfterRejectedStore_ContainsCorrectMetrics()
    {
        var store = new InMemorySessionStore();
        var service = new SessionService(store: store);

        // Initial state check
        string initialReport = service.GenerateReport();
        initialReport.Should().Contain("Total Stores Attempted  : 0");
        initialReport.Should().Contain("Total Stores Succeeded  : 0");
        initialReport.Should().Contain("Total Stores Failed     : 0");
        initialReport.Should().Contain("Total Stores Rejected   : 0");
        initialReport.Should().Contain("Total Consumes Attempted: 0");
        initialReport.Should().Contain("Total Consumes Succeeded: 0");
        initialReport.Should().Contain("Total Consumes Failed   : 0");

        // Store with connection disposed policy violation (rejection)
        var connection = Substitute.For<IConnection>();
        connection.IsDisposed.Returns(true);
        await service.SaveSessionAsync(connection);

        string reportAfterDisposed = service.GenerateReport();
        reportAfterDisposed.Should().Contain("Total Stores Attempted  : 1");
        reportAfterDisposed.Should().Contain("Total Stores Rejected   : 1");

        // JSON report testing
        using var ms = new System.IO.MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(ms))
        {
            service.WriteReportData(writer);
        }
        string json = System.Text.Encoding.UTF8.GetString(ms.ToArray());
        json.Should().Contain("\"Type\":\"SessionService\"");
        json.Should().Contain("\"TotalStoresAttempted\":1");
        json.Should().Contain("\"TotalStoresRejectedByPolicy\":1");
        json.Should().Contain("\"Store\":");
        json.Should().Contain("\"Type\":\"InMemorySessionStore\"");
    }
}
