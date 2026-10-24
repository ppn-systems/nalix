// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Nalix.Abstractions;
using Nalix.Abstractions.Exceptions;
using Nalix.Environment.Configuration;
using Nalix.Network.Options;

namespace Nalix.Network.Internal.Pooling;

/// <summary>
/// A pooled wrapper around <see cref="SocketAsyncEventArgs"/> tailored for UDP datagram reception.
/// Pre-allocates a pinned byte array sized precisely to the MTU (MaxUdpDatagramSize) to avoid GC pressure.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
[DebuggerDisplay("HasBuffer={Buffer != null}, RemoteEndPoint={RemoteEndPoint}")]
internal sealed class PooledUdpReceiveEventArgs : SocketAsyncEventArgs, IPoolable
{
    private readonly byte[] _buffer;

    public PooledUdpReceiveEventArgs()
    {
        // Obtain MaxUdpDatagramSize from global config, fallback to 1400 bytes (typical MTU avoid-fragmentation threshold).
        int bufferSize = 1400;
        try
        {
            bufferSize = ConfigurationManager.Instance.Get<NetworkSocketOptions>().MaxUdpDatagramSize;
        }
        catch (Exception ex) when (
            ex is InvalidOperationException ||
            ex is NullReferenceException ||
            ex is TypeInitializationException ||
            ex is InternalErrorException)
        {
            // Fallback cleanly if ConfigurationManager is uninitialized in tests.
        }

        // Pre-allocate buffer once per SAEA instance.
        // Use pinned array to prevent GC from moving the buffer during async I/O.
#if NET6_0_OR_GREATER
        _buffer = GC.AllocateUninitializedArray<byte>(bufferSize, pinned: true);
#else
        _buffer = new byte[bufferSize];
#endif

        this.SetBuffer(_buffer, 0, _buffer.Length);
    }

    /// <summary>
    /// Resets the internal state before returning to the pool.
    /// Clears the remote endpoint and user token but maintains the pre-allocated buffer payload.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ResetForPool()
    {
        this.UserToken = null;
        this.RemoteEndPoint = new IPEndPoint(IPAddress.Any, 0);
        // Do NOT clear the buffer, just reset the view if necessary, but SetBuffer remains identical.
        this.SetBuffer(this.Offset, _buffer.Length);
    }
}
