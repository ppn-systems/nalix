// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions;
using Nalix.Abstractions.Identity;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Sessions;
using Nalix.Abstractions.Primitives;
using Nalix.Framework.Memory.Objects;
using Nalix.Runtime.Sessions;
using Xunit;

namespace Nalix.Runtime.Tests.Sessions;

public sealed class InMemorySessionStoreConsumptionTests : IDisposable
{
    private readonly InMemorySessionStore _store;

    public InMemorySessionStoreConsumptionTests()
    {
        _store = new InMemorySessionStore();
    }

    [Fact]
    public async Task ConsumeAsync_AfterStoreAsync_ReturnsStoredEntry()
    {
        ulong token = (ulong)123456789UL;
        SessionSnapshot snapshot = new()
        {
            SessionToken = token,
            ExpiresAtUnixMilliseconds = long.MaxValue
        };
        SessionEntry entry = new(snapshot, (ulong)1UL);

        await _store.StoreAsync(entry);
        using SessionScope scope = await _store.ConsumeAsync(token);
        SessionEntry? retrieved = scope.Value;

        Assert.NotNull(retrieved);
        Assert.Same(entry, retrieved);
        Assert.Equal(token, retrieved!.Snapshot.SessionToken);
    }

    [Fact]
    public async Task ConsumeAsync_WhenExpired_ReturnsNull()
    {
        ulong token = (ulong)999UL;
        SessionSnapshot snapshot = new()
        {
            SessionToken = token,
            ExpiresAtUnixMilliseconds = 0 // Already expired
        };
        SessionEntry entry = new(snapshot, (ulong)1UL);

        await _store.StoreAsync(entry);
        
        // This should trigger lazy expiration
        using SessionScope scope = await _store.ConsumeAsync(token);
        SessionEntry? consumed = scope.Value;

        Assert.Null(consumed);
    }

    [Fact]
    public async Task GenerateReport_AfterStoreConsumeAndExpiry_ContainsCorrectMetrics()
    {
        var store = new InMemorySessionStore();
        
        // Assert initial state of report
        string initialReport = store.GenerateReport();
        Assert.Contains("Active Sessions : 0", initialReport);
        Assert.Contains("Total Stored    : 0", initialReport);
        Assert.Contains("Total Consumed  : 0", initialReport);
        Assert.Contains("Total Expired   : 0", initialReport);

        // Store an entry
        ulong token = 123456789UL;
        SessionSnapshot snapshot = new()
        {
            SessionToken = token,
            ExpiresAtUnixMilliseconds = long.MaxValue
        };
        SessionEntry entry = new(snapshot, 1UL);
        await store.StoreAsync(entry);

        string reportAfterStore = store.GenerateReport();
        Assert.Contains("Active Sessions : 1", reportAfterStore);
        Assert.Contains("Total Stored    : 1", reportAfterStore);

        // Consume entry
        using SessionScope scope = await store.ConsumeAsync(token);
        SessionEntry? consumed = scope.Value;
        Assert.NotNull(consumed);

        string reportAfterConsume = store.GenerateReport();
        Assert.Contains("Active Sessions : 0", reportAfterConsume);
        Assert.Contains("Total Consumed  : 1", reportAfterConsume);

        // Expired consume
        ulong expiredToken = 999UL;
        SessionSnapshot expiredSnapshot = new()
        {
            SessionToken = expiredToken,
            ExpiresAtUnixMilliseconds = 0 // Already expired
        };
        SessionEntry expiredEntry = new(expiredSnapshot, 1UL);
        await store.StoreAsync(expiredEntry);

        using SessionScope expiredScope = await store.ConsumeAsync(expiredToken);
        SessionEntry? expiredConsumed = expiredScope.Value;
        Assert.Null(expiredConsumed);

        string reportAfterExpired = store.GenerateReport();
        Assert.Contains("Total Expired   : 1", reportAfterExpired);
        
        // JSON report testing
        using var ms = new System.IO.MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(ms))
        {
            store.WriteReportData(writer);
        }
        string json = System.Text.Encoding.UTF8.GetString(ms.ToArray());
        Assert.Contains("\"Type\":\"InMemorySessionStore\"", json);
        Assert.Contains("\"ActiveSessions\":0", json);
        Assert.Contains("\"TotalStored\":2", json);
        Assert.Contains("\"TotalConsumed\":1", json);
        Assert.Contains("\"TotalExpired\":1", json);
    }

    /// <summary>
    /// Area 6 (session store concurrency exactness): 64 threads racing to
    /// <see cref="InMemorySessionStore.ConsumeAsync"/> the SAME token must yield exactly one
    /// winner (SEC-33's atomic TryRemove) — never zero (a lost race stranding the entry
    /// forever) and never more than one (a double-consume of pooled resources).
    /// </summary>
    [Fact]
    [Trait("Category", "Stress")]
    public async Task ConsumeAsync_WithConcurrentRaceOnSameToken_YieldsExactlyOneWinner()
    {
        const int seed = 20260704;
        const int threadCount = 64;
        ulong token = 555_555UL;

        SessionSnapshot snapshot = new()
        {
            SessionToken = token,
            ExpiresAtUnixMilliseconds = long.MaxValue
        };
        await _store.StoreAsync(new SessionEntry(snapshot, 1UL));

        System.Random rng = new(seed);
        int winners = 0;
        using System.Threading.Barrier barrier = new(threadCount);
        Thread[] threads = new Thread[threadCount];

        for (int i = 0; i < threadCount; i++)
        {
            int delayTicks = rng.Next(0, 5);
            threads[i] = new Thread(() =>
            {
                for (int spin = 0; spin < delayTicks; spin++)
                {
                    Thread.SpinWait(1);
                }
                barrier.SignalAndWait();
                using SessionScope scope = _store.ConsumeAsync(token).AsTask().GetAwaiter().GetResult();
                if (scope.IsValid)
                {
                    _ = Interlocked.Increment(ref winners);
                }
            });
            threads[i].Start();
        }

        foreach (Thread thread in threads)
        {
            thread.Join();
        }

        Assert.True(winners == 1, $"seed={seed}: exactly 1 of {threadCount} concurrent consumers racing on the same token must win, got {winners}");
    }

    /// <summary>
    /// Area 6: a losing overwrite race in <see cref="InMemorySessionStore.StoreAsync"/> must return
    /// the displaced entry's pooled resources exactly once — 32 threads storing distinct
    /// <see cref="SessionEntry"/> instances under the SAME token must leave the store holding exactly
    /// one of them, with all others having had <c>Return()</c> called (Secret zeroized, Attributes null).
    /// </summary>
    [Fact]
    [Trait("Category", "Stress")]
    public async Task StoreAsync_WithConcurrentOverwriteOnSameToken_LeavesExactlyOneSurvivor()
    {
        const int seed = 20260704;
        const int threadCount = 32;
        ulong token = 777_777UL;

        SessionEntry[] entries = new SessionEntry[threadCount];
        for (int i = 0; i < threadCount; i++)
        {
            entries[i] = new SessionEntry(
                new SessionSnapshot
                {
                    SessionToken = token,
                    ExpiresAtUnixMilliseconds = long.MaxValue,
                    Attributes = ObjectMap<AttributeKey, object>.Rent()
                },
                (ulong)i);
        }

        System.Random rng = new(seed);
        using System.Threading.Barrier barrier = new(threadCount);
        Thread[] threads = new Thread[threadCount];

        for (int i = 0; i < threadCount; i++)
        {
            int idx = i;
            int delayTicks = rng.Next(0, 5);
            threads[i] = new Thread(() =>
            {
                for (int spin = 0; spin < delayTicks; spin++)
                {
                    Thread.SpinWait(1);
                }
                barrier.SignalAndWait();
                _store.StoreAsync(entries[idx]).AsTask().GetAwaiter().GetResult();
            });
            threads[i].Start();
        }

        foreach (Thread thread in threads)
        {
            thread.Join();
        }

        using SessionScope scope = await _store.ConsumeAsync(token);
        SessionEntry? survivor = scope.Value;
        Assert.NotNull(survivor);

        int survivingCount = 0;
        foreach (SessionEntry entry in entries)
        {
            bool isReturned = entry.Snapshot.Attributes is null;
            if (ReferenceEquals(entry, survivor))
            {
                Assert.False(isReturned, $"seed={seed}: the surviving entry must not have been returned to the pool");
                survivingCount++;
            }
            else
            {
                Assert.True(isReturned, $"seed={seed}: entry {entry.ConnectionId} lost the overwrite race and must have had Return() called exactly once");
            }
        }

        Assert.Equal(1, survivingCount);
    }

    /// <summary>
    /// Session-resume replay protection: <c>SessionHandlers.HandleAsync</c> pairs the 30-second
    /// HMAC time-bucket proof (see <c>SessionResumeProofTests</c>) with an atomic
    /// consume-once token (<see cref="InMemorySessionStore.ConsumeAsync"/>/SEC-33 TryRemove).
    /// A resume request replayed with the SAME session token — even with a proof that is still
    /// valid within the same 30-second bucket — must be rejected on the second attempt because
    /// the token itself is single-use: the first successful consume removes it from the store.
    /// </summary>
    [Fact]
    public async Task ConsumeAsync_WhenTokenReplayed_RejectsSecondAttempt()
    {
        ulong token = 424_242UL;
        SessionSnapshot snapshot = new()
        {
            SessionToken = token,
            ExpiresAtUnixMilliseconds = long.MaxValue
        };
        await _store.StoreAsync(new SessionEntry(snapshot, 1UL));

        using SessionScope first = await _store.ConsumeAsync(token);
        Assert.True(first.IsValid, "the first resume attempt with a fresh token must succeed");

        // Replay: same token, same (still-valid) time bucket — must be rejected because the
        // token was already consumed, regardless of proof/time-bucket validity.
        using SessionScope replay = await _store.ConsumeAsync(token);
        Assert.False(replay.IsValid, "a replayed resume token must be rejected on the second attempt");
    }

    [Fact]
    public async Task ConsumeAsync_WhenPredicateRejects_DoesNotConsumeToken()
    {
        ulong token = 626_262UL;
        SessionSnapshot snapshot = new()
        {
            SessionToken = token,
            ExpiresAtUnixMilliseconds = long.MaxValue
        };
        SessionEntry entry = new(snapshot, 1UL);
        await _store.StoreAsync(entry);

        using SessionScope rejected = await _store.ConsumeAsync(token, _ => false);
        Assert.False(rejected.IsValid);

        using SessionScope accepted = await _store.ConsumeAsync(token, _ => true);
        Assert.True(accepted.IsValid);
        Assert.Same(entry, accepted.Value);

        using SessionScope replay = await _store.ConsumeAsync(token, _ => true);
        Assert.False(replay.IsValid);
    }

    public void Dispose()
    {
    }
}
