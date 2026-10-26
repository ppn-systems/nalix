// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Codec.Pooling;
using Nalix.Codec.ProtocolFrames;
using Nalix.Runtime.Dispatching;
using Nalix.Runtime.Internal;

namespace Nalix.Runtime.Extensions;

/// <summary>
/// Provides extension methods for the <see cref="IConnection"/> interface to support connection management operations.
/// </summary>
public static partial class ConnectionExtensions
{
    /// <summary>
    /// Gets or creates the runtime specific state for the connection.
    /// </summary>
    internal static RuntimeConnectionState GetRuntimeState(this IConnection connection)
    {
        if (connection.Attributes.TryGetValue(ConnectionAttributes.RuntimeState, out object? obj) && obj is RuntimeConnectionState state)
        {
            return state;
        }

        return GetOrAddState(connection, ConnectionAttributes.RuntimeState, new RuntimeConnectionState());
    }

    /// <summary>
    /// Gets or creates the sequence state for the connection.
    /// </summary>
    internal static ConnectionSequenceState GetSequenceState(this IConnection connection)
    {
        if (connection.Attributes.TryGetValue(ConnectionAttributes.SequenceState, out object? obj) && obj is ConnectionSequenceState state)
        {
            return state;
        }

        return GetOrAddState(connection, ConnectionAttributes.SequenceState, new ConnectionSequenceState());
    }

    // Adds the state only when the key is still free: two threads that both missed it above share the first one stored,
    // so neither loses what the other wrote into it (for example the handshake slot).
    private static T GetOrAddState<T>(IConnection connection, AttributeKey key, T candidate)
        where T : class
    {
        while (true)
        {
            if (connection.Attributes.TryGetValue(key, out object? existing))
            {
                if (existing is T typed)
                {
                    return typed;
                }

                connection.Attributes[key] = candidate;
                return candidate;
            }

            try
            {
                connection.Attributes.Add(key, candidate);
                return candidate;
            }
            catch (ArgumentException)
            {
                // Another thread stored the state first; read it on the next pass.
            }
        }
    }

    /// <summary>
    /// Gets the connection hub that owns this connection from its attributes.
    /// </summary>
    /// <param name="connection">The connection to get the hub for.</param>
    /// <returns>The owning <see cref="IConnectionHub"/>, or <c>null</c> if not found.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static IConnectionHub? GetHub(this IConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.Attributes.TryGetValue(ConnectionAttributes.OwnerHub, out object? obj) ? (IConnectionHub)obj : null;
    }

    /// <summary>
    /// Sends a control directive asynchronously over the connection.
    /// </summary>
    /// <param name="sender">The connection to send the directive on.</param>
    /// <param name="controlType">The type of control message to send.</param>
    /// <param name="reason">The reason code associated with the control message.</param>
    /// <param name="action">The suggested action for the recipient.</param>
    /// <param name="options">Optional directive metadata and payload arguments.</param>
    /// <returns>A task representing the asynchronous send operation.</returns>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    public static async Task SendAsync(this IPacketSender sender, ControlType controlType, ProtocolReason reason, ProtocolAdvice action, ControlDirectiveOptions options = default)
    {
        ArgumentNullException.ThrowIfNull(sender);

        using PacketScope<Directive> lease = PacketFactory<Directive>.Acquire();
        Directive directive = lease.Value;

        directive.Initialize(
            controlType, reason, action,
            sequenceId: options.SequenceId,
            controlFlags: options.Flags,
            arg0: options.Arg0,
            arg1: options.Arg1,
            arg2: options.Arg2);

        await sender.SendAsync(directive).ConfigureAwait(false);
    }

    /// <summary>
    /// Serializes and sends a packet over the specified transport asynchronously, applying the necessary framing (encryption/compression).
    /// </summary>
    /// <typeparam name="TPacket">The type of the packet.</typeparam>
    /// <param name="connection">The connection to send the packet on.</param>
    /// <param name="packet">The packet to send.</param>
    /// <param name="transport">The transport to use (defaults to TCP).</param>
    /// <param name="enableEncrypt">Whether to encrypt the packet (defaults to true).</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task representing the asynchronous send operation.</returns>
    public static ValueTask SendAsync<TPacket>(this IConnection connection, TPacket packet, NetworkTransport transport = NetworkTransport.TCP, bool enableEncrypt = true, CancellationToken ct = default)
        where TPacket : IPacket
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (packet == null)
        {
            throw new ArgumentNullException(nameof(packet));
        }

        IConnection.ITransport activeTransport = transport switch
        {
            NetworkTransport.UDP => connection.UDP ?? throw new InvalidOperationException("UDP companion transport is not created on this connection."),
            NetworkTransport.TCP => connection.TCP,
            NetworkTransport.WEBSOCKET => connection.TCP,
            _ => connection.TCP
        };

        return PacketSender.SEND_CORE_ASYNC(connection, activeTransport, packet, enableEncrypt, ct);
    }
}


/// <summary>
/// Optional metadata and payload arguments for a control directive.
/// </summary>
/// <param name="Flags">Optional control flags to include with the message.</param>
/// <param name="SequenceId">
/// Correlation id to map this directive to a prior request (0 = server-initiated / no correlation).
/// </param>
/// <param name="Arg0">Optional argument 0 for the directive.</param>
/// <param name="Arg1">Optional argument 1 for the directive.</param>
/// <param name="Arg2">Optional argument 2 for the directive.</param>
public readonly record struct ControlDirectiveOptions(ControlFlags Flags = ControlFlags.NONE, ushort SequenceId = 0, uint Arg0 = 0, uint Arg1 = 0, ushort Arg2 = 0);
