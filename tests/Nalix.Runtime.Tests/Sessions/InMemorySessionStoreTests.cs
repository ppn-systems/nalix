// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Threading.Tasks;
using FluentAssertions;
using Nalix.Abstractions.Networking.Sessions;
using Nalix.Environment.Time;
using Nalix.Runtime.Sessions;
using Xunit;

namespace Nalix.Runtime.Tests.Sessions;

[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
public sealed class InMemorySessionStoreTests
{
    [Fact]
    public async Task StoreAsync_WhenExceedingMaxSessions_EnforcesCapacityBound()
    {
        InMemorySessionStore store = new();

        PropertyInfo? maxSessionsProp = typeof(InMemorySessionStore).GetProperty("MaxSessions", BindingFlags.Public | BindingFlags.Instance);
        maxSessionsProp.Should().NotBeNull("InMemorySessionStore must expose a configurable MaxSessions capacity cap");
        maxSessionsProp!.SetValue(store, 4);

        long expiresAt = Clock.UnixMillisecondsNow() + 60_000L;
        for (ulong i = 1; i <= 10; i++)
        {
            SessionSnapshot snapshot = new()
            {
                SessionToken = i,
                ExpiresAtUnixMilliseconds = expiresAt
            };
            await store.StoreAsync(new SessionEntry(snapshot, i));
        }

        FieldInfo storeField = typeof(InMemorySessionStore).GetField("_store", BindingFlags.NonPublic | BindingFlags.Instance)!;
        System.Collections.ICollection dict = (System.Collections.ICollection)storeField.GetValue(store)!;
        dict.Count.Should().BeLessThanOrEqualTo(4, "active sessions in InMemorySessionStore must not exceed MaxSessions");
    }
}
