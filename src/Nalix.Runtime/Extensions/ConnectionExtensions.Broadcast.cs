// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Codec.Options;
using Nalix.Codec.Transforms;
using Nalix.Environment.Configuration;
using Nalix.Environment.Memory;
using Nalix.Runtime.Internal.Pipelines;

namespace Nalix.Runtime.Extensions;

/// <summary>
/// Provides extension methods for <see cref="IConnectionHub"/> to support packet-based broadcast and multicast.
/// </summary>
public static partial class ConnectionExtensions
{
    /// <summary>
    /// Broadcasts a packet to all active connections.
    /// The packet is serialized once, and the compression/encryption pipeline is applied per-connection.
    /// </summary>
    /// <param name="hub">The connection hub.</param>
    /// <param name="packet">The packet to broadcast.</param>
    /// <param name="transport">The network transport protocol to use.</param>
    /// <param name="enableEncrypt">Whether to encrypt the packet (defaults to true).</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous broadcast operation.</returns>
    public static async Task BroadcastAsync(
        this IConnectionBroadcaster hub, IPacket packet,
        NetworkTransport transport = NetworkTransport.TCP,
        bool enableEncrypt = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(packet);

        // [SECURITY] An encrypted broadcast serializes plaintext: scrub it when the lease is released.
        BufferLease rawLease = PacketPipeline.Serialize(packet, zeroOnDispose: enableEncrypt);
        try
        {
            bool enableCompress = s_options.Enabled;
            int minSizeToCompress = s_options.MinSizeToCompress;

            // PRE-COMPRESSION (O(1) C)
            int payloadSize = rawLease.Length - PacketConstants.HeaderSize;
            if (enableCompress && payloadSize >= minSizeToCompress)
            {
                IBufferLease temp = FrameCompression.CompressFrame(rawLease);
                rawLease.Dispose();
                rawLease = (BufferLease)temp;
                rawLease.ZeroOnDispose = enableEncrypt; // [SECURITY] compressed plaintext
                enableCompress = false;
            }

            BroadcastState state = new(
                rawLease,
                transport,
                enableEncrypt,
                enableCompress,
                minSizeToCompress);

            PacketSenderAction sender = new();

            await hub.BroadcastAsync(state, sender, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            rawLease.Dispose();
        }
    }

    /// <summary>
    /// Multicasts a packet to a specific connection group.
    /// The packet is serialized once, and the compression/encryption pipeline is applied per-connection.
    /// </summary>
    /// <param name="hub">The connection hub.</param>
    /// <param name="groupProvider">The connection group provider to use for resolving group members.</param>
    /// <param name="groupName">The name of the group to receive the message.</param>
    /// <param name="packet">The packet to multicast.</param>
    /// <param name="transport">The network transport protocol to use.</param>
    /// <param name="enableEncrypt">Whether to encrypt the packet (defaults to true).</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous multicast operation.</returns>
    public static async Task MulticastAsync(
        this IConnectionBroadcaster hub,
        IConnectionGroupRegistry groupProvider,
        string groupName,
        IPacket packet, NetworkTransport transport = NetworkTransport.TCP,
        bool enableEncrypt = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(groupProvider);
        ArgumentException.ThrowIfNullOrEmpty(groupName);
        ArgumentNullException.ThrowIfNull(packet);

        // [SECURITY] An encrypted broadcast serializes plaintext: scrub it when the lease is released.
        BufferLease rawLease = PacketPipeline.Serialize(packet, zeroOnDispose: enableEncrypt);
        try
        {
            bool enableCompress = s_options.Enabled;
            int minSizeToCompress = s_options.MinSizeToCompress;

            // PRE-COMPRESSION (O(1) C)
            int payloadSize = rawLease.Length - PacketConstants.HeaderSize;
            if (enableCompress && payloadSize >= minSizeToCompress)
            {
                IBufferLease temp = FrameCompression.CompressFrame(rawLease);
                rawLease.Dispose();
                rawLease = (BufferLease)temp;
                rawLease.ZeroOnDispose = enableEncrypt; // [SECURITY] compressed plaintext
                enableCompress = false;
            }

            BroadcastState state = new(
                rawLease,
                transport,
                enableEncrypt,
                enableCompress,
                minSizeToCompress);

            PacketSenderAction sender = new();

            await hub.MulticastAsync(groupProvider, groupName, state, sender, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            rawLease.Dispose();
        }
    }

    /// <summary>
    /// Multicasts a packet to a specific connection group, excluding a specific connection.
    /// The packet payload is automatically compressed (if enabled and large enough) and encrypted per connection.
    /// </summary>
    /// <param name="hub">The connection broadcaster.</param>
    /// <param name="groupProvider">The connection group provider to use for resolving group members.</param>
    /// <param name="groupName">The name of the group to receive the message.</param>
    /// <param name="excludedConnection">The connection to exclude from the broadcast.</param>
    /// <param name="packet">The packet to multicast.</param>
    /// <param name="transport">The network transport protocol to use.</param>
    /// <param name="enableEncrypt">Whether to encrypt the packet (defaults to true).</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous multicast operation.</returns>
    public static async Task MulticastExceptAsync(
        this IConnectionBroadcaster hub,
        IConnectionGroupRegistry groupProvider,
        string groupName,
        IConnection excludedConnection,
        IPacket packet, NetworkTransport transport = NetworkTransport.TCP,
        bool enableEncrypt = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(groupProvider);
        ArgumentException.ThrowIfNullOrEmpty(groupName);
        ArgumentNullException.ThrowIfNull(excludedConnection);
        ArgumentNullException.ThrowIfNull(packet);

        // [SECURITY] An encrypted broadcast serializes plaintext: scrub it when the lease is released.
        BufferLease rawLease = PacketPipeline.Serialize(packet, zeroOnDispose: enableEncrypt);
        try
        {
            bool enableCompress = s_options.Enabled;
            int minSizeToCompress = s_options.MinSizeToCompress;

            // PRE-COMPRESSION (O(1) C)
            int payloadSize = rawLease.Length - PacketConstants.HeaderSize;
            if (enableCompress && payloadSize >= minSizeToCompress)
            {
                IBufferLease temp = FrameCompression.CompressFrame(rawLease);
                rawLease.Dispose();
                rawLease = (BufferLease)temp;
                rawLease.ZeroOnDispose = enableEncrypt; // [SECURITY] compressed plaintext
                enableCompress = false;
            }

            BroadcastState state = new(
                rawLease,
                transport,
                enableEncrypt,
                enableCompress,
                minSizeToCompress);

            PacketSenderAction sender = new();

            await hub.MulticastExceptAsync(groupProvider, groupName, excludedConnection, state, sender, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            rawLease.Dispose();
        }
    }

    #region Options

    private static readonly CompressionOptions s_options = ConfigurationManager.Instance.Get<CompressionOptions>();

    #endregion Options

    #region Nested Types

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1815:Override equals and operator equals on value types")]
    private readonly struct BroadcastState
    {
        public readonly IBufferLease RawLease;
        public readonly NetworkTransport Transport;
        public readonly bool EnableEncrypt;
        public readonly bool EnableCompress;
        public readonly int MinSizeToCompress;

        public BroadcastState(
            IBufferLease rawLease,
            NetworkTransport transport,
            bool enableEncrypt,
            bool enableCompress,
            int minSizeToCompress)
        {
            RawLease = rawLease;
            Transport = transport;
            EnableEncrypt = enableEncrypt;
            EnableCompress = enableCompress;
            MinSizeToCompress = minSizeToCompress;
        }
    }

    private readonly struct PacketSenderAction : IConnectionSender<BroadcastState>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ValueTask SendAsync(IConnection connection, ref BroadcastState state, CancellationToken ct)
        {
            IConnection.ITransport? targetTransport =
                state.Transport == NetworkTransport.UDP ? connection.UDP : connection.TCP;

            if (targetTransport is null)
            {
                return default;
            }

            return PacketPipeline.ProcessAndSendAsync(
                connection,
                targetTransport,
                state.RawLease,
                state.EnableEncrypt,
                state.EnableCompress,
                state.MinSizeToCompress,
                ct);
        }
    }

    #endregion Nested Types
}
