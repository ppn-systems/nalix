// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Threading.Tasks;
using Nalix.Abstractions.Networking.Sessions;
using Nalix.Environment.Time;
using Nalix.Runtime.Sessions;
using Xunit;

namespace Nalix.Runtime.Tests.Sessions;

[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
public sealed class InMemorySessionStoreEvictionTests
{
    /// <summary>
    /// At capacity the store must make room by dropping the session that is closest to expiring, not whichever
    /// entry the dictionary happens to enumerate first (which lets a flood of cheap sessions push out old, valid ones).
    /// </summary>
    [Fact]
    public async Task StoreAsync_WhenAtCapacity_EvictsSessionWithEarliestExpiry()
    {
        InMemorySessionStore store = new() { MaxSessions = 2 };
        long now = Clock.UnixMillisecondsNow();

        // Token 2 is the OLDEST session; token 1 is newer. Dictionary enumeration yields token 1 first.
        await store.StoreAsync(new SessionEntry(
            new SessionSnapshot { SessionToken = 2, CreatedAtUnixMilliseconds = now - 20_000, ExpiresAtUnixMilliseconds = now + 40_000 }, 2));
        await store.StoreAsync(new SessionEntry(
            new SessionSnapshot { SessionToken = 1, CreatedAtUnixMilliseconds = now - 10_000, ExpiresAtUnixMilliseconds = now + 50_000 }, 1));
        await store.StoreAsync(new SessionEntry(
            new SessionSnapshot { SessionToken = 3, CreatedAtUnixMilliseconds = now, ExpiresAtUnixMilliseconds = now + 60_000 }, 3));

        FieldInfo storeField = typeof(InMemorySessionStore).GetField("_store", BindingFlags.NonPublic | BindingFlags.Instance)!;
        IDictionary<ulong, SessionEntry> entries = (IDictionary<ulong, SessionEntry>)storeField.GetValue(store)!;

        Assert.False(entries.ContainsKey(2), "the session closest to expiry should have been evicted");
        Assert.True(entries.ContainsKey(1));
        Assert.True(entries.ContainsKey(3));
    }
}
