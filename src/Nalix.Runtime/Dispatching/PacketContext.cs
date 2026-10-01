// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Injection;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Framework.Memory.Objects;

namespace Nalix.Runtime.Dispatching;

/// Carries a packet, its connection, and the metadata needed while a handler is
/// executing. Instances are pooled so dispatch can reuse context objects without
/// allocating on every packet.
[DebuggerDisplay("IsInitialized={_isInitialized}")]
public sealed class PacketContext<TPacket> : IPacketContext<TPacket>, IPoolable, IDisposable, IAsyncDisposable
    where TPacket : IPacket
{
    #region Static

    private static readonly ObjectPoolManager s_pool = ObjectPoolManager.Shared;

    #endregion Static

    #region Fields

    private readonly PacketScope _scope = new();
    private int _state;
    private bool _ownsPacket;
    private bool _isInitialized;

    #endregion Fields

    #region Properties

    /// <inheritdoc/>
    public bool IsReliable
    {
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        get;
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private set;
    }

    /// <inheritdoc/>
    public bool EncryptedOnWire
    {
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        get;
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private set;
    }

    /// <inheritdoc/>
    public bool SkipOutbound
    {
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        get;
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal set;
    }

    /// <inheritdoc/>
    public TPacket Packet
    {
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        get;
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private set;
    }

    /// <inheritdoc/>
    public IConnection Connection
    {
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        get;
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private set;
    }

    /// <inheritdoc/>
    public PacketMetadata Attributes
    {
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        get; private set;
    }

    /// <inheritdoc/>
    public IPacketSender Sender
    {
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        get;
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal set;
    }

    /// <inheritdoc/>
    public IPacketScope Scope
    {
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        get;
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private set;
    }

    /// <inheritdoc/>
    public CancellationToken CancellationToken
    {
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        get;
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal set;
    }

    #endregion Properties

    #region Constructor

    static PacketContext()
    {
        _ = s_pool.SetMaxCapacity<PacketContext<TPacket>>(128);
        _ = s_pool.Prealloc<PacketContext<TPacket>>(128);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PacketContext{TPacket}"/> class for pooling.
    /// </summary>
    /// <remarks>
    /// The constructor leaves the context in the pooled state with placeholder
    /// values so the object pool can hand it out later through <see cref="Initialize"/>.
    /// </remarks>
    public PacketContext()
    {
        _state = (int)PacketContextState.Pooled;

        this.Sender = new PacketSender();
        this.Scope = default!;
        this.Packet = default!;
        this.IsReliable = false;
        this.EncryptedOnWire = false;
        this.Connection = default!;
        this.Attributes = default!;
    }

    #endregion Constructor

    #region Methods

    /// <summary>
    /// Initializes the context with the specified packet, connection, and metadata.
    /// </summary>
    /// <param name="packet">The packet to process.</param>
    /// <param name="connection">The connection associated with the packet.</param>
    /// <param name="descriptor">The metadata describing the packet.</param>
    /// <param name="reliable">Whether the packet was received over a reliable transport.</param>
    /// <param name="encryptedOnWire">Whether the inbound frame arrived encrypted on the wire.</param>
    /// <param name="ownsPacket">Indicates whether the context owns the packet and is responsible for its disposal.</param>
    /// <param name="token">The cancellation token for the context.</param>
    /// <param name="scope">An optional existing scope to attach (e.g. from a parent context during bridging).</param>
    /// <remarks>
    /// This method marks the pooled instance as in use before populating the
    /// packet-specific fields so the dispatcher can return it safely later.
    /// </remarks>
    /// <exception cref="InternalErrorException"></exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Initialize(
        TPacket packet, IConnection connection, PacketMetadata descriptor,
        bool reliable, bool encryptedOnWire, bool ownsPacket = true, CancellationToken token = default,
        Nalix.Abstractions.Injection.IPacketScope? scope = null)
    {
        _ = Interlocked.Exchange(ref _state, (int)PacketContextState.InUse);

        ArgumentNullException.ThrowIfNull(packet);
        ArgumentNullException.ThrowIfNull(connection);

        this.Packet = packet;
        this.IsReliable = reliable;
        this.EncryptedOnWire = encryptedOnWire;
        this.Connection = connection;
        this.Attributes = descriptor;
        this.CancellationToken = token;
        this.Scope = scope ?? _scope;

        if (this.Scope is PacketScope packetScope)
        {
            packetScope.AttachConnection(connection);
        }

        this.Sender.Initialize(this);

        _isInitialized = true;
        _ownsPacket = ownsPacket;
    }

    #endregion Methods

    #region IDisposable

    /// <summary>
    /// Returns the context to the object pool once dispatch has finished with it.
    /// </summary>
    /// <remarks>
    /// The state transition prevents double-return and makes the pool handoff idempotent
    /// if multiple cleanup paths race to dispose the same context.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal void Return()
    {
        if (Interlocked.Exchange(
            ref _state, (int)PacketContextState.Returned) != (int)PacketContextState.InUse)
        {
            return;
        }

        s_pool.Return(this);
    }

    /// <summary>
    /// Resets the context so it can be safely reused by the object pool.
    /// </summary>
    /// <remarks>
    /// The sender is returned to the shared pool first, then all packet-specific
    /// references are cleared so the next renter sees a clean context.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void ResetForPool()
    {
        if (_isInitialized)
        {
            if (_ownsPacket && this.Packet is IDisposable disposablePacket)
            {
                disposablePacket.Dispose();
            }

            if (_ownsPacket && this.Scope is IDisposable disposableScope)
            {
                disposableScope.Dispose();
                if (ReferenceEquals(this.Scope, _scope))
                {
                    _scope.ResetForPool();
                }
                else if (this.Scope is PacketScope pooledScope)
                {
                    s_pool.Return(pooledScope);
                }
            }

            this.Scope = default!;
            this.Packet = default!;
            this.IsReliable = false;
            this.EncryptedOnWire = false;
            this.SkipOutbound = false;
            this.Attributes = default!;
            this.Connection = default!;
            this.CancellationToken = default;

            this.Sender.ResetForPool();

            _isInitialized = false;
        }

        _ = Interlocked.Exchange(ref _state, (int)PacketContextState.Pooled);
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Dispose() => this.Return();

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public async ValueTask DisposeAsync()
    {
        if (_isInitialized)
        {
            if (_ownsPacket && this.Scope is not null)
            {
                if (this.Scope is IAsyncDisposable asyncScope)
                {
                    await asyncScope.DisposeAsync().ConfigureAwait(false);
                }
                else if (this.Scope is IDisposable disposableScope)
                {
                    disposableScope.Dispose();
                }

                if (ReferenceEquals(this.Scope, _scope))
                {
                    _scope.ResetForPool();
                }
                else if (this.Scope is PacketScope pooledScope)
                {
                    s_pool.Return(pooledScope);
                }

                this.Scope = default!;
            }
        }

        this.Return();
    }

    #endregion IDisposable
}
