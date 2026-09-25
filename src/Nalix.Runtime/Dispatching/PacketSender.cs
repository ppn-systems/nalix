// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Primitives;
using Nalix.Codec.Options;
using Nalix.Environment.Configuration;
using Nalix.Environment.Memory;
using Nalix.Runtime.Internal.Pipelines;

#if DEBUG
using Nalix.Abstractions.Diagnostics;
#endif

namespace Nalix.Runtime.Dispatching;

/// <summary>
/// Default packet sender that serializes a packet, optionally compresses it,
/// optionally encrypts it, and then forwards the final buffer to the connection.
/// </summary>
public sealed class PacketSender : IPacketSender
{
    #region Fields

    private CancellationToken _token;
    private IConnection? _connection;
    private PacketMetadata _attributes;
    private bool _isReliable;
    private ushort _requestSequenceId;

    private static readonly CompressionOptions s_options = ConfigurationManager.Instance.Get<CompressionOptions>();

    #endregion Fields

    #region Constructor

    /// <summary>
    /// Initializes a new instance of the <see cref="PacketSender"/> class.
    /// </summary>
    internal PacketSender()
    {
    }

    #endregion Constructor

    #region APIs

    /// <inheritdoc/>
    public void Initialize<TPacket>(IPacketContext<TPacket> context) where TPacket : IPacket
    {
        ArgumentNullException.ThrowIfNull(context);
        _connection = context.Connection;
        _attributes = context.Attributes;
        _token = context.CancellationToken;
        _isReliable = context.IsReliable;
        _requestSequenceId = context.Packet.Header.SequenceId;
    }

    /// <inheritdoc/>
    public void ResetForPool()
    {
        _token = default;
        _connection = null;
        _attributes = default;
        _isReliable = false;
        _requestSequenceId = 0;
    }

    /// <inheritdoc/>
    public ValueTask SendAsync(IPacket packet, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(packet);

        IConnection connection = this.GET_CONNECTION_OR_THROW();
        IConnection.ITransport transport = GetTransport(connection, _attributes, _isReliable);

        bool needEncrypt = _attributes.Encryption?.IsEncrypted ?? false;
        CancellationToken safeToken = ct == default ? _token : ct;

        return SEND_CORE_ASYNC(connection, transport, packet, needEncrypt, safeToken);
    }

    /// <inheritdoc/>
    public ValueTask SendAsync(IPacket packet, bool forceEncrypt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(packet);

        IConnection connection = this.GET_CONNECTION_OR_THROW();
        IConnection.ITransport transport = GetTransport(connection, _attributes, _isReliable);

        CancellationToken safeToken = ct == default ? _token : ct;

        return SEND_CORE_ASYNC(connection, transport, packet, forceEncrypt, safeToken);
    }

    /// <inheritdoc/>
    public ValueTask ReplyAsync(IPacket response, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(response);

        PacketHeader header = response.Header;
        header.SequenceId = _requestSequenceId;
        response.Header = header;

        return this.SendAsync(response, ct);
    }

    #endregion APIs

    #region Private Methods

    internal static async ValueTask SEND_CORE_ASYNC(IConnection connection, IConnection.ITransport transport, IPacket packet, bool needEncrypt, CancellationToken ct)
    {
        int packetLength = packet.Length;

#if DEBUG
        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Debug))
        {
            DiagnosticsEvents.Write(
                DiagnosticsEvents.Internal.Debug,
                new DiagnosticLog(
                    "RT.PacketSender:SendCoreAsync",
                    $"send-core-async packet={packet.GetType().Name} length={packetLength} encrypt={needEncrypt}"));
        }
#endif

        // Serialize into a pooled buffer first so the subsequent compression/encryption
        // branches can reuse the same payload without reserializing the packet.
        BufferLease rawLease = PacketPipeline.Serialize(packet, zeroOnDispose: needEncrypt);

        try
        {
            await PacketPipeline.ProcessAndSendAsync(
                connection,
                transport,
                rawLease,
                needEncrypt,
                s_options.Enabled,
                s_options.MinSizeToCompress,
                ct,
                cloneLease: false).ConfigureAwait(false);
        }
        finally
        {
            // The raw serialization buffer is always returned.
            rawLease.Dispose();
        }
    }

    private static IConnection.ITransport GetTransport(IConnection connection, PacketMetadata attributes, bool isReliable)
    {
        // Prioritize the transport specified on the handler attribute.
        if (attributes.Transport?.TransportType is { } t)
        {
            return t switch
            {
                NetworkTransport.UDP => connection.UDP ?? throw new InvalidOperationException("UDP companion transport is not created on this connection."),
                NetworkTransport.TCP or NetworkTransport.WEBSOCKET or _ => connection.TCP
            };
        }

        // If the incoming request was received over unreliable transport (UDP), reply on UDP!
        if (!isReliable && connection.UDP is not null)
        {
            return connection.UDP;
        }

        return connection.TCP;
    }

    private IConnection GET_CONNECTION_OR_THROW()
        => _connection ?? throw new InternalErrorException($"{nameof(PacketSender)} must be initialized before sending.");

    #endregion Private Methods
}
