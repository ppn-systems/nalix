// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;

namespace Nalix.Abstractions;

/// <summary>
/// Extension methods for <see cref="IBufferLease"/>.
/// </summary>
public static class BufferLeaseExtensions
{
    /// <summary>
    /// Creates a read-only view of the lease that cannot release it or outlive the current call.
    /// </summary>
    /// <param name="lease">The lease to view. The caller keeps ownership.</param>
    /// <exception cref="ArgumentNullException"><paramref name="lease"/> is <see langword="null"/>.</exception>
    public static LeaseRef AsRef([Borrowed] this IBufferLease lease) => new(lease);
}
