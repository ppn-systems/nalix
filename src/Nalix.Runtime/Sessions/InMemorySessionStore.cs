// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions;
using Nalix.Abstractions.Concurrency;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Networking.Sessions;
using Nalix.Environment.Time;
using Nalix.Framework.Tasks;

namespace Nalix.Runtime.Sessions;

/// <summary>
/// An in-memory implementation of <see cref="ISessionStore"/> backed by a <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// Suitable for single-node deployments. For distributed scenarios, replace with a Redis-backed store.
/// </summary>
[Worker(
    $"{TaskNaming.Tags.Service}.{TaskNaming.Tags.Cleanup}.sessions",
    TaskNaming.Tags.Cleanup,
    Tag = TaskNaming.Tags.Cleanup, IdType = 1, RetainForMs = 0)]
public sealed class InMemorySessionStore : ISessionStore, IWorker, IReportable
{
    // Bucketing granularity for the expiry index. Sessions expiring within the
    // same minute share a bucket, so the scavenger only visits buckets that are
    // actually due instead of scanning every live session every pass.
    private const long BucketSpanMilliseconds = 60_000;

    /// <summary>
    /// Default upper bound on active sessions retained in memory.
    /// </summary>
    public const int DefaultMaxSessions = 65_536;

    private readonly ConcurrentDictionary<ulong, SessionEntry> _store = new();

    // expiryBucket (ExpiresAtUnixMilliseconds / BucketSpanMilliseconds) -> session tokens due in that bucket.
    // A stale entry (session consumed/removed, or re-stored with a later expiry) is harmless: the bucket
    // is only ever a hint, the real expiry is re-checked against _store before anything is removed.
    private readonly ConcurrentDictionary<long, ConcurrentDictionary<ulong, byte>> _expiryBuckets = new();

    private int _maxSessions = DefaultMaxSessions;
    private long _totalStored;
    private long _totalConsumed;
    private long _totalExpired;

    /// <summary>
    /// Gets or sets the maximum number of active sessions retained in memory before capacity eviction occurs.
    /// </summary>
    public int MaxSessions
    {
        get => Volatile.Read(ref _maxSessions);
        set => Volatile.Write(ref _maxSessions, Math.Max(1, value));
    }

    private static long BucketOf(long expiresAtUnixMilliseconds) => expiresAtUnixMilliseconds / BucketSpanMilliseconds;

    private void IndexExpiry(ulong token, long expiresAtUnixMilliseconds)
        => _expiryBuckets.GetOrAdd(BucketOf(expiresAtUnixMilliseconds), static _ => new ConcurrentDictionary<ulong, byte>())[token] = 1;

    /// <summary>
    /// Executes the scavenging loop. This method is intended to be called by a <see cref="ITaskManager"/> worker.
    /// </summary>
    public async ValueTask ExecuteAsync(IWorkerContext context, CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromMinutes(1));

        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            context?.Beat();

            try
            {
                await SCAVENGE_ASYNC(this, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
            {
                // Background cleanup errors should not crash the scavenger worker
            }
        }

        static async ValueTask SCAVENGE_ASYNC(InMemorySessionStore self, CancellationToken cancellationToken)
        {
            long now = Clock.UnixMillisecondsNow();
            long dueBucket = BucketOf(now);
            int count = 0;

            foreach (long bucketKey in self._expiryBuckets.Keys)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (bucketKey > dueBucket || !self._expiryBuckets.TryRemove(bucketKey, out ConcurrentDictionary<ulong, byte>? tokens))
                {
                    continue;
                }

                foreach (ulong token in tokens.Keys)
                {
                    // Bucket membership is only a hint (a session re-stored with a later
                    // expiry may still sit in an earlier bucket) — re-check the live
                    // expiry and re-index it if it hasn't actually expired yet.
                    if (self._store.TryGetValue(token, out SessionEntry? entry))
                    {
                        if (entry.Snapshot.ExpiresAtUnixMilliseconds > now)
                        {
                            self.IndexExpiry(token, entry.Snapshot.ExpiresAtUnixMilliseconds);
                        }
                        else if (((ICollection<KeyValuePair<ulong, SessionEntry>>)self._store).Remove(new KeyValuePair<ulong, SessionEntry>(token, entry)))
                        {
                            _ = Interlocked.Increment(ref self._totalExpired);
                            entry.Return();
                        }
                    }

                    if (++count % 1000 == 0)
                    {
                        await Task.Yield();
                    }
                }
            }
        }
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask StoreAsync(SessionEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();

        ulong token = entry.Snapshot.SessionToken;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int maxSessions = this.MaxSessions;
            if (_store.Count >= maxSessions && !_store.ContainsKey(token))
            {
                this.EvictForCapacity(maxSessions);
            }

            if (_store.TryAdd(token, entry))
            {
                _ = Interlocked.Increment(ref _totalStored);
                this.IndexExpiry(token, entry.Snapshot.ExpiresAtUnixMilliseconds);
                return ValueTask.CompletedTask;
            }

            if (!_store.TryGetValue(token, out SessionEntry? current))
            {
                continue;
            }

            // Already stored this exact reference for the token.
            if (ReferenceEquals(current, entry))
            {
                return ValueTask.CompletedTask;
            }

            if (_store.TryUpdate(token, entry, current))
            {
                _ = Interlocked.Increment(ref _totalStored);
                this.IndexExpiry(token, entry.Snapshot.ExpiresAtUnixMilliseconds);
                current.Return();
                return ValueTask.CompletedTask;
            }
        }
    }

    private void EvictForCapacity(int maxSessions)
    {
        long now = Clock.UnixMillisecondsNow();

        foreach (KeyValuePair<ulong, SessionEntry> kvp in _store)
        {
            if (kvp.Value.Snapshot.ExpiresAtUnixMilliseconds <= now &&
                ((ICollection<KeyValuePair<ulong, SessionEntry>>)_store).Remove(kvp))
            {
                _ = Interlocked.Increment(ref _totalExpired);
                kvp.Value.Return();
                if (_store.Count < maxSessions)
                {
                    return;
                }
            }
        }

        foreach (KeyValuePair<ulong, SessionEntry> kvp in _store)
        {
            if (_store.Count < maxSessions)
            {
                return;
            }

            if (((ICollection<KeyValuePair<ulong, SessionEntry>>)_store).Remove(kvp))
            {
                _ = Interlocked.Increment(ref _totalExpired);
                kvp.Value.Return();
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// SEC-33 fix: Uses <c>ConcurrentDictionary.TryRemove</c> for atomic
    /// retrieve-and-remove. Only one concurrent caller can successfully consume a given token.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<SessionScope> ConsumeAsync(ulong sessionToken, Func<SessionEntry, bool>? predicate = null, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_store.TryGetValue(sessionToken, out SessionEntry? entry))
            {
                return ValueTask.FromResult(new SessionScope(null));
            }

            if (entry.Snapshot.ExpiresAtUnixMilliseconds <= Clock.UnixMillisecondsNow())
            {
                if (((ICollection<KeyValuePair<ulong, SessionEntry>>)_store).Remove(new KeyValuePair<ulong, SessionEntry>(sessionToken, entry)))
                {
                    _ = Interlocked.Increment(ref _totalExpired);
                    entry.Return();
                    return ValueTask.FromResult(new SessionScope(null));
                }

                continue;
            }

            if (predicate is not null && !predicate(entry))
            {
                return ValueTask.FromResult(new SessionScope(null));
            }

            if (((ICollection<KeyValuePair<ulong, SessionEntry>>)_store).Remove(new KeyValuePair<ulong, SessionEntry>(sessionToken, entry)))
            {
                _ = Interlocked.Increment(ref _totalConsumed);
                return ValueTask.FromResult(new SessionScope(entry));
            }
        }
    }

    /// <inheritdoc />
    public string GenerateReport()
    {
        System.Text.StringBuilder sb = new();
        _ = sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"InMemorySessionStore Status:");
        _ = sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"  Active Sessions : {_store.Count}");
        _ = sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"  Total Stored    : {Volatile.Read(ref _totalStored)}");
        _ = sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"  Total Consumed  : {Volatile.Read(ref _totalConsumed)}");
        _ = sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"  Total Expired   : {Volatile.Read(ref _totalExpired)}");
        return sb.ToString();
    }

#if NET10_0_OR_GREATER
    /// <inheritdoc />
    public void WriteReportData(System.Text.Json.Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStartObject();
        writer.WriteString("Type", "InMemorySessionStore");
        writer.WriteNumber("ActiveSessions", _store.Count);
        writer.WriteNumber("TotalStored", Volatile.Read(ref _totalStored));
        writer.WriteNumber("TotalConsumed", Volatile.Read(ref _totalConsumed));
        writer.WriteNumber("TotalExpired", Volatile.Read(ref _totalExpired));
        writer.WriteEndObject();
    }
#endif
}
