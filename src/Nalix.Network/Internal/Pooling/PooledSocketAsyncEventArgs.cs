// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Nalix.Abstractions;

namespace Nalix.Network.Internal.Pooling;

/// <summary>
/// A pooled wrapper around <see cref="SocketAsyncEventArgs"/> that resets every
/// socket-specific field before the instance is returned to the pool.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
[DebuggerDisplay("HasSocket={AcceptSocket != null}, HasContext={Context != null}")]
internal sealed class PooledSocketAsyncEventArgs : SocketAsyncEventArgs, IPoolable
{
    /// <summary>
    /// The pooled accept context associated with this event args.
    /// The accept context is stored here so the completion path can recover the
    /// owning state without a closure or external lookup.
    /// </summary>
    public PooledAcceptContext? Context { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the persistent completion handler 
    /// has already been bound to this instance.
    /// </summary>
    public bool IsHandlerBound { get; set; }

    /// <summary>
    /// Resets the internal state before returning to the pool.
    /// This clears the associated context, user token, accepted socket, remote
    /// endpoint, and buffer window so the next accept starts from a clean slate.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ResetForPool()
    {
        this.Context = null;
        this.UserToken = null;
        this.AcceptSocket = null;
        this.RemoteEndPoint = null;
        this.IsHandlerBound = false;
        this.SetBuffer(null, 0, 0);

        // Note: we DO NOT clear the Completed event here because we want to 
        // reuse the same delegate for the entire life of this pooled instance
        // to avoid delegate churn.
    }
}
