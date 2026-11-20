// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;

namespace Nalix.Abstractions;

/// <summary>
/// Defines a reference-counted lease over a pooled byte buffer.
/// Provides access to the valid payload region, underlying capacity,
/// and methods for safe sharing/disposal.
/// </summary>
public interface IBufferLease : IDisposable
{
    /// <summary>Gets the valid payload length within the buffer.</summary>
    int Length { get; }

    /// <summary>
    /// Gets or sets a value indicating whether this frame arrived encrypted on the wire.
    /// Set once by the inbound cipher transforms and preserved across subsequent transforms
    /// (e.g. decompression) so dispatch can audit the frame's on-wire state even after the
    /// header's transient <c>ENCRYPTED</c> flag has been cleared.
    /// </summary>
    bool EncryptedOnWire { get; set; }

    /// <summary>Gets the capacity of the underlying buffer.</summary>
    int Capacity { get; }

    /// <summary>Gets a writable span covering the valid payload.</summary>
    Span<byte> Span { get; }

    /// <summary>Gets a writable span covering the entire capacity.</summary>
    Span<byte> SpanFull { get; }

    /// <summary>Gets a read-only view of the valid payload.</summary>
    ReadOnlyMemory<byte> Memory { get; }

    /// <summary>
    /// Increases the reference count so multiple consumers can hold this lease safely.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown when the underlying buffer has already been released.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the internal reference count becomes invalid.</exception>
    void Retain();

    /// <summary>
    /// Sets the valid payload length (must be between 0 and Capacity).
    /// </summary>
    /// <param name="length">
    /// The number of valid payload bytes to commit.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="length"/> is negative or greater than <see cref="Capacity"/>.</exception>
    void CommitLength(int length);

    /// <summary>
    /// Attempts to detach the underlying array, transferring ownership to the caller.
    /// </summary>
    /// <param name="buffer">
    /// On success, receives the detached backing buffer.
    /// </param>
    /// <param name="start">
    /// On success, receives the starting offset of the valid payload.
    /// </param>
    /// <param name="length">
    /// On success, receives the length of the valid payload.
    /// </param>
    bool ReleaseOwnership(out byte[]? buffer, out int start, out int length);
}
