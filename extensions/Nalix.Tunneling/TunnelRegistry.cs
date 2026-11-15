// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Concurrent;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Primitives;
using Nalix.Environment.Random;

namespace Nalix.Tunneling;

/// <summary>
/// Tracks pending reverse tunnel requests matching consumer connections to provider data connections.
/// </summary>
public sealed class TunnelRegistry
{
    private readonly ConcurrentDictionary<Bytes32, PendingEntry> _pendingTunnels = new();
    private long _nextCleanupAt;

    /// <summary>
    /// Registers a pending tunnel request for a connection.
    /// </summary>
    /// <returns>A tuple containing the Task to await the Provider's connection, and the security Token.</returns>
    public (Task<IConnection> Task, Bytes32 Token) Register()
    {
        // Opportunistic cleanup: prevent memory leaks from abandoned tunnel requests
        _ = this.CleanupStale(TimeSpan.FromSeconds(10));

        TaskCompletionSource<IConnection> tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Generate cryptographically secure random token without allocations
        Span<byte> tokenBytes = stackalloc byte[32];
        Csprng.Fill(tokenBytes);

        Bytes32 token = new(tokenBytes);

        PendingEntry entry = new(tcs, System.Environment.TickCount64);

        if (!_pendingTunnels.TryAdd(token, entry))
        {
            throw new InvalidOperationException("Failed to generate a unique tunnel token.");
        }

        return (tcs.Task, token);
    }

    /// <summary>
    /// Resolves a pending tunnel request with the active data connection opened by the Provider.
    /// </summary>
    public bool Resolve(Bytes32 token, IConnection providerDataConnection)
    {
        ArgumentNullException.ThrowIfNull(providerDataConnection);

        if (_pendingTunnels.TryRemove(token, out PendingEntry? entry))
        {
            return entry.Tcs.TrySetResult(providerDataConnection);
        }

        return false;
    }

    /// <summary>
    /// Removes a pending tunnel request without resolving it.
    /// </summary>
    /// <returns><see langword="true"/> if the request was pending and has been removed.</returns>
    public bool Remove(Bytes32 token) => _pendingTunnels.TryRemove(token, out _);

    /// <summary>
    /// Gets the number of pending tunnel requests.
    /// </summary>
    public int PendingCount => _pendingTunnels.Count;

    /// <summary>
    /// Removes and cancels all pending tunnel requests that have exceeded the specified timeout.
    /// </summary>
    public int CleanupStale(TimeSpan timeout)
    {
        long now = System.Environment.TickCount64;
        long timeoutMs = (long)timeout.TotalMilliseconds;

        // Gate: skip scan if called faster than the timeout window allows
        if (now < Volatile.Read(ref _nextCleanupAt) && timeoutMs >= 1000)
        {
            return 0;
        }

        long cutoff = now - timeoutMs;
        int cleaned = 0;

        foreach (KeyValuePair<Bytes32, PendingEntry> entry in _pendingTunnels)
        {
            if (entry.Value.Timestamp < cutoff)
            {
                if (_pendingTunnels.TryRemove(entry.Key, out PendingEntry? removed))
                {
                    _ = removed.Tcs.TrySetCanceled();
                    cleaned++;
                }
            }
        }

        // Next cleanup gate: scan at most once per timeout window (min 5s for large timeouts)
        Volatile.Write(ref _nextCleanupAt, now + Math.Max(timeoutMs, 5000));

        return cleaned;
    }

    private sealed class PendingEntry(TaskCompletionSource<IConnection> tcs, long timestamp)
    {
        public TaskCompletionSource<IConnection> Tcs { get; } = tcs;
        public long Timestamp { get; } = timestamp;
    }
}
