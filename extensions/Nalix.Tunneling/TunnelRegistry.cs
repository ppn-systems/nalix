// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Concurrent;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Primitives;
using Nalix.Codec.Security.Hashing;
using Nalix.Environment.Random;

namespace Nalix.Tunneling;

/// <summary>
/// Tracks pending reverse tunnel requests matching consumer connections to provider data connections.
/// Pending entries are keyed by the public tunnel identifier <c>Keccak256(token)</c>, so observing a
/// tunnel identifier on the wire does not reveal the token.
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
        Bytes32 tunnelId = ComputeTunnelId(token);

        PendingEntry entry = new(tcs, token, System.Environment.TickCount64);

        if (!_pendingTunnels.TryAdd(tunnelId, entry))
        {
            throw new InvalidOperationException("Failed to generate a unique tunnel token.");
        }

        return (tcs.Task, token);
    }

    /// <summary>
    /// Computes the public tunnel identifier for a token: <c>Keccak256(token)</c>.
    /// </summary>
    public static Bytes32 ComputeTunnelId(Bytes32 token) => Keccak256.HashDataToFixed(token.AsSpan());

    /// <summary>
    /// Gets the token of a pending tunnel request without removing it.
    /// </summary>
    public bool TryGetToken(Bytes32 tunnelId, out Bytes32 token)
    {
        if (_pendingTunnels.TryGetValue(tunnelId, out PendingEntry? entry))
        {
            token = entry.Token;
            return true;
        }

        token = default;
        return false;
    }

    /// <summary>
    /// Resolves a pending tunnel request with the active data connection opened by the Provider.
    /// </summary>
    public bool Resolve(Bytes32 tunnelId, IConnection providerDataConnection)
    {
        ArgumentNullException.ThrowIfNull(providerDataConnection);

        if (_pendingTunnels.TryRemove(tunnelId, out PendingEntry? entry))
        {
            return entry.Tcs.TrySetResult(providerDataConnection);
        }

        return false;
    }

    /// <summary>
    /// Removes a pending tunnel request without resolving it.
    /// </summary>
    /// <returns><see langword="true"/> if the request was pending and has been removed.</returns>
    public bool Remove(Bytes32 token) => _pendingTunnels.TryRemove(ComputeTunnelId(token), out _);

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

    private sealed class PendingEntry(TaskCompletionSource<IConnection> tcs, Bytes32 token, long timestamp)
    {
        public TaskCompletionSource<IConnection> Tcs { get; } = tcs;
        public Bytes32 Token { get; } = token;
        public long Timestamp { get; } = timestamp;
    }
}
