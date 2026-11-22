// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
#if DEBUG
using System.Diagnostics;
#endif

namespace Nalix.Abstractions;

/// <summary>
/// A read-only view of an <see cref="IBufferLease"/> that the caller still owns.
/// </summary>
/// <remarks>
/// <para>
/// The view cannot release the lease: it has no <c>Dispose</c>. Because it is a
/// <see langword="ref struct"/>, the compiler also rejects storing it in a field, capturing it in a
/// lambda or keeping it across an <see langword="await"/>, so it cannot outlive the call it was
/// passed to. A method that needs the buffer for longer calls <see cref="Retain"/> and becomes an
/// owner of the returned lease.
/// </para>
/// <para>
/// The view remembers the <see cref="IBufferLease.Generation"/> of the lease it was created from.
/// <see cref="IsAlive"/> reports whether the lease has been released since; debug builds assert it
/// on every access.
/// </para>
/// </remarks>
public readonly ref struct BorrowedLease
{
    private readonly IBufferLease? _lease;
    private readonly int _generation;

    /// <summary>
    /// Creates a borrowed view of <paramref name="lease"/>.
    /// </summary>
    /// <param name="lease">The lease to view. The caller keeps ownership.</param>
    /// <exception cref="ArgumentNullException"><paramref name="lease"/> is <see langword="null"/>.</exception>
    public BorrowedLease(IBufferLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);

        _lease = lease;
        _generation = lease.Generation;
    }

    /// <summary>
    /// Gets a value indicating whether this is the default view, which refers to no lease.
    /// </summary>
    public bool IsDefault => _lease is null;

    /// <summary>
    /// Gets a value indicating whether the lease has not been released since this view was created.
    /// </summary>
    public bool IsAlive => _lease is not null && _lease.Generation == _generation;

    /// <summary>Gets the valid payload length.</summary>
    /// <exception cref="InvalidOperationException">This is the default view.</exception>
    public int Length => this.Lease.Length;

    /// <summary>Gets a value indicating whether the frame arrived encrypted on the wire.</summary>
    /// <exception cref="InvalidOperationException">This is the default view.</exception>
    public bool EncryptedOnWire => this.Lease.EncryptedOnWire;

    /// <summary>Gets a read-only span over the valid payload.</summary>
    /// <exception cref="InvalidOperationException">This is the default view.</exception>
    public ReadOnlySpan<byte> Span => this.Lease.Span;

    /// <summary>
    /// Takes an additional reference to the lease and returns it. The caller owns the returned
    /// lease and must dispose it exactly once.
    /// </summary>
    /// <exception cref="InvalidOperationException">This is the default view.</exception>
    /// <exception cref="ObjectDisposedException">The lease has already released its buffer.</exception>
    public IBufferLease Retain()
    {
        IBufferLease lease = this.Lease;
        lease.Retain();
        return lease;
    }

    private IBufferLease Lease
    {
        get
        {
            IBufferLease lease = _lease ?? throw new InvalidOperationException("The borrowed lease view is not initialized.");
#if DEBUG
            if (lease.Generation != _generation)
            {
                Debug.Fail("BorrowedLease used after its lease was released. The buffer may already belong to another owner.");
            }
#endif
            return lease;
        }
    }
}
