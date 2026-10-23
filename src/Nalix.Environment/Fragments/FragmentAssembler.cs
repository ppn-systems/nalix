// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using Nalix.Environment.Memory;

namespace Nalix.Environment.Fragments;

/// <summary>
/// Reassembly engine: collects each received chunk and returns a complete reassembly result
/// when the last chunk arrives.
///
/// <para>
/// <b>Shared contract</b> — this instance is embedded into <c>FrameReader</c> (SDK)
/// and <c>SocketConnection</c> (Server). Each connection holds its own instance.
/// </para>
///
/// <para><b>Thread safety:</b> designed for a single-threaded receive loop.
/// No locking required as TCP ensures order and only a single goroutine reads from the socket.</para>
///
/// <para><b>Ownership after reassembly:</b>
/// <see cref="Add"/> returns a <see cref="FragmentAssemblyResult"/> that transfers ownership of the
/// pooled <see cref="BufferLease"/> already used during accumulation.
/// <b>The caller must Dispose the returned lease</b> after handling.</para>
///
/// <para><b>Chunks must arrive in order</b> (TCP guarantees this) and not be duplicated.
/// If you need out-of-order support, expand <c>StreamState</c> with a bitmap per slot.</para>
/// </summary>
public sealed class FragmentAssembler : IDisposable
{
    #region Constants

    /// <summary>
    /// Eviction check interval in number of processed chunks. Call <see cref="EvictExpired"/> every N chunks.
    /// </summary>
    public const int EvictInterval = 64;

    /// <summary>
    /// Default cap for initial stream buffer pre-allocation when <c>ChunkIndex == 0</c> is small.
    /// Prevents untrusted <see cref="FragmentHeader.TotalChunks"/> from amplifying a small first chunk
    /// into a multi-megabyte allocation before real data arrives.
    /// </summary>
    private const int DefaultInitialStreamCapacity = 4096;

    #endregion Constants

    #region Inner state per stream

    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed",
        Justification = "AccumLease is disposed by StreamState.Dispose or transferred to FragmentAssemblyResult and nulled on successful reassembly.")]
    private sealed class StreamState : IDisposable
    {
        internal BufferLease AccumLease; // Buffer for accumulating payload
        internal int WrittenBytes;       // Number of bytes written
        internal int ReceivedCount;      // Number of chunks received
        internal ushort TotalChunks;     // Total number of chunks expected
        internal long LastActivityMs;    // Timestamp (ms), for eviction

        internal StreamState(ushort totalChunks, int estimatedBytes)
        {
            TotalChunks = totalChunks;
            AccumLease = BufferLease.Rent(estimatedBytes);
            WrittenBytes = 0;
            ReceivedCount = 0;
            LastActivityMs = System.Environment.TickCount64;
        }

        public void Dispose() => Interlocked.Exchange(ref AccumLease!, null!)?.Dispose();
    }

    #endregion Inner state per stream

    #region Fields

    private bool _disposed;
    private int _chunksSinceLastSweep;

    // Use regular Dictionary since this is for a single-threaded per-connection receive loop.
    // Avoid ConcurrentDictionary overhead, which is unnecessary here.
    private readonly Dictionary<ushort, StreamState> _streams = [];
    private List<ushort>? _toEvict;

    #endregion Fields

    #region Configuration properties

    /// <summary>
    /// Streams currently being reassembled, indexed by StreamId.
    /// </summary>
    public int OpenStreamCount => _streams.Count;

    /// <summary>
    /// The maximum time (ms) a stream can exist without receiving a new chunk before being evicted.
    /// Default: 30,000 ms (30 seconds).
    /// </summary>
    public long StreamTimeoutMs { get; init; } = 30_000;

    /// <summary>
    /// The maximum size (in bytes) of a stream being reassembled.
    /// Streams exceeding this threshold are immediately discarded. Default: 16 MB.
    /// </summary>
    public int MaxStreamBytes { get; init; } = 16 * 1024 * 1024;

    /// <summary>
    /// The maximum number of concurrent reassembly streams allowed per connection.
    /// Streams exceeding this limit are rejected immediately. Default: 64.
    /// </summary>
    /// <remarks>
    /// SEC-34 fix: Without this limit, an attacker could open thousands of streams
    /// (each with a different streamId), each allocating a buffer, to exhaust memory.
    /// </remarks>
    public int MaxOpenStreams { get; init; } = 64;

    #endregion Configuration properties

    #region APIs

    /// <summary>
    /// Adds a chunk to the assembler.
    /// </summary>
    /// <param name="header">Header of the received chunk.</param>
    /// <param name="chunkBody">
    /// The chunk body — without the 7-byte <see cref="FragmentHeader"/>.
    /// Data is <b>copied</b> immediately, so the caller may release the span after calling.
    /// </param>
    /// <param name="streamEvicted"></param>
    /// <returns>
    /// A complete <see cref="FragmentAssemblyResult"/> when the stream is finished; otherwise <see langword="null"/>
    /// while waiting for more chunks or when the stream was evicted as part of normal policy enforcement.
    /// </returns>
    /// <exception cref="ObjectDisposedException">Thrown when the assembler has already been disposed.</exception>
    /// <exception cref="InvalidDataException">Thrown when the fragment header is invalid, inconsistent, or arrives out of order.</exception>
    public FragmentAssemblyResult? Add(in FragmentHeader header, ReadOnlySpan<byte> chunkBody, out bool streamEvicted)
    {
        streamEvicted = false;

        ObjectDisposedException.ThrowIf(_disposed, nameof(FragmentAssembler));

        // ── Validate ──────────────────────────────────────────────────────
        if (header.StreamId == 0
         || header.TotalChunks == 0
         || header.ChunkIndex >= header.TotalChunks)
        {
            throw new InvalidDataException(
                $"Invalid fragment header. StreamId={header.StreamId}, ChunkIndex={header.ChunkIndex}, TotalChunks={header.TotalChunks}.");
        }

        long now = System.Environment.TickCount64;

        if (++_chunksSinceLastSweep >= EvictInterval)
        {
            _chunksSinceLastSweep = 0;
            _ = this.EvictExpired();
        }

        // ── Retrieve or create StreamState ────────────────────────────────
        if (!_streams.TryGetValue(header.StreamId, out StreamState? state))
        {
            if (header.ChunkIndex != 0)
            {
                throw new InvalidDataException(
                    $"Fragment stream {header.StreamId} started with chunk index {header.ChunkIndex} instead of 0.");
            }

            // SEC-34: Reject new streams when at capacity to prevent memory exhaustion.
            if (_streams.Count >= this.MaxOpenStreams && this.EvictExpired() == 0)
            {
                streamEvicted = true;
                return null;
            }

            if (chunkBody.Length > this.MaxStreamBytes)
            {
                streamEvicted = true;
                return null;
            }

            // Estimate buffer size: firstChunk * totalChunks, capped at MaxStreamBytes.
            // Bound initial pre-allocation to at most max(chunkBody.Length * 2, 4096) so a small
            // first chunk with a large TotalChunks cannot trigger a 16 MB upfront allocation (CWE-789).
            long fullEstimate = Math.Min((long)chunkBody.Length * header.TotalChunks, this.MaxStreamBytes);
            long boundedInitial = Math.Min(fullEstimate, Math.Max((long)chunkBody.Length * 2, DefaultInitialStreamCapacity));
            int estimate = (int)Math.Min(boundedInitial, this.MaxStreamBytes);

            state = new StreamState(header.TotalChunks, Math.Max(estimate, 256));
            _streams[header.StreamId] = state;
        }

        // ── Check timeout ────────────────────────────────────────────────
        if (now - state.LastActivityMs > this.StreamTimeoutMs)
        {
            streamEvicted = true;
            this.EVICT(header.StreamId, state);
            return null;
        }

        // ── Check TotalChunks consistency ────────────────────────────────
        // Prevent forgery: TotalChunks must be the same across all chunks of a stream
        if (state.TotalChunks != header.TotalChunks)
        {
            streamEvicted = true;
            this.EVICT(header.StreamId, state);
            throw new InvalidDataException(
                $"Fragment stream {header.StreamId} changed TotalChunks from {state.TotalChunks} to {header.TotalChunks}.");
        }

        // ── Enforce ordered delivery ─────────────────────────────────────
        if (header.ChunkIndex != state.ReceivedCount)
        {
            streamEvicted = true;
            this.EVICT(header.StreamId, state);
            throw new InvalidDataException(
                $"Fragment stream {header.StreamId} received out-of-order chunk. Expected index {state.ReceivedCount}, got {header.ChunkIndex}.");
        }

        // ── Check overflow ───────────────────────────────────────────────
        if ((long)state.WrittenBytes + chunkBody.Length > this.MaxStreamBytes)
        {
            streamEvicted = true;
            this.EVICT(header.StreamId, state);
            return null;
        }

        state.LastActivityMs = now;

        // ── Grow buffer if needed ────────────────────────────────────────
        if (state.WrittenBytes + chunkBody.Length > state.AccumLease.Capacity)
        {
            int doubled = (int)Math.Min((long)state.AccumLease.Capacity * 2, this.MaxStreamBytes);
            int newCap = Math.Max(
                doubled,
                state.WrittenBytes + chunkBody.Length);

            BufferLease newLease = BufferLease.Rent(newCap);
            state.AccumLease.Span[..state.WrittenBytes].CopyTo(newLease.SpanFull);
            newLease.CommitLength(state.WrittenBytes);
            state.AccumLease.Dispose();
            state.AccumLease = newLease;
        }

        // ── Append chunk body ────────────────────────────────────────────
        // TCP guarantees in-order delivery so end-appending is correct
        chunkBody.CopyTo(state.AccumLease.SpanFull[state.WrittenBytes..]);
        state.WrittenBytes += chunkBody.Length;
        state.AccumLease.CommitLength(state.WrittenBytes);
        state.ReceivedCount += 1;

        // ── Check if stream is complete ──────────────────────────────────
        if (state.ReceivedCount < state.TotalChunks)
        {
            return null; // Still waiting for more chunks
        }

        // ── Stream done: hand over buffer to caller (zero-copy) ──────────
        BufferLease finalLease = state.AccumLease;
        state.AccumLease = null!;
        _ = _streams.Remove(header.StreamId);
        // No need to call state.Dispose() because AccumLease is now null

        return new FragmentAssemblyResult(finalLease);
    }

    /// <summary>
    /// Evicts all streams that have not received a chunk within <see cref="StreamTimeoutMs"/>.
    /// Call periodically from the receive loop (e.g. every N packets).
    /// </summary>
    /// <returns>Number of streams evicted.</returns>
    public int EvictExpired()
    {
        if (_streams.Count == 0)
        {
            return 0;
        }

        long now = System.Environment.TickCount64;
        int evicted = 0;

        foreach ((ushort streamId, StreamState? state) in _streams)
        {
            if (now - state.LastActivityMs > this.StreamTimeoutMs)
            {
                _toEvict ??= [];
                _toEvict.Add(streamId);
            }
        }

        if (_toEvict is { Count: > 0 })
        {
            foreach (ushort id in _toEvict)
            {
                if (_streams.Remove(id, out StreamState? state))
                {
                    state.Dispose();
                    evicted++;
                }
            }
            _toEvict.Clear();
        }

        return evicted;
    }

    /// <summary>
    /// Cancels and disposes all streams in progress.
    /// Call when connection is closed to avoid memory leaks.
    /// </summary>
    /// <remarks>
    /// This method is idempotent and safely disposes every buffered in-progress stream.
    /// </remarks>
    public void Clear()
    {
        foreach (KeyValuePair<ushort, StreamState> kv in _streams)
        {
            kv.Value.Dispose();
        }

        _streams.Clear();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        this.Clear();
    }

    /// <summary>
    /// Quick check to determine if the payload is a fragmented chunk using the magic byte.
    /// </summary>
    /// <remarks>
    /// Parsing failures are swallowed and reported as <see langword="false"/> instead of escaping to the caller.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static bool IsFragmentedFrame(ReadOnlySpan<byte> payload, [NotNullWhen(true)] out FragmentHeader header)
    {
        header = default;
        if (payload.Length < FragmentHeader.WireSize)
        {
            return false;
        }

        if (payload[0] != FragmentHeader.Magic)
        {
            return false;
        }

        // Second validation: try to read header and check logical consistency
        try
        {
            header = FragmentHeader.ReadFrom(payload);

            // Additional safety checks
            if (header.StreamId == 0)
            {
                return false;
            }

            if (header.TotalChunks == 0 || header.ChunkIndex >= header.TotalChunks)
            {
                return false;
            }

            return true;
        }
        catch (InvalidDataException)
        {
            // If parsing fails -> treat as normal packet
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            // If parsing fails -> treat as normal packet
            return false;
        }
        catch (IndexOutOfRangeException)
        {
            // If parsing fails -> treat as normal packet
            return false;
        }
    }

    #endregion APIs

    #region Private methods

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EVICT(ushort streamId, StreamState state)
    {
        _ = _streams.Remove(streamId);
        state.Dispose();
    }

    #endregion Private methods
}
