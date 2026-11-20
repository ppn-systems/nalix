// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nalix.Abstractions.Networking.Datagrams;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Serialization;
using Nalix.Codec.DataFrames;

namespace Nalix.Codec.ProtocolFrames;

/// <summary>
/// Datagram session ticket, exchanged over an established stream session: the client sends a
/// <see cref="DatagramTicketStage.REQUEST"/>, the server answers with a <see cref="DatagramTicketStage.RESPONSE"/>
/// that carries the ticket, or with a <see cref="Reason"/>.
/// </summary>
[Packet]
[GenerateFormatter]
[ExcludeFromCodeCoverage]
[SerializePackable(SerializeLayout.Explicit)]
[DebuggerDisplay("DATAGRAM_TICKET Stage={Stage}, ConnectionId={ConnectionId}, Port={Port}, Reason={Reason}")]
public sealed partial class DatagramTicket : PacketBase<DatagramTicket>, IFixedSizeSerializable, IPacketValidatable, IPacketStaticOpcode
{
    /// <inheritdoc/>
    public static ushort StaticOpCode => (ushort)ProtocolOpCode.DATAGRAM_TICKET;

    /// <summary>Gets or sets the stage.</summary>
    [SerializeOrder(0)]
    public DatagramTicketStage Stage { get; set; }

    /// <summary>Gets or sets the reason a request was refused (responses only).</summary>
    [SerializeOrder(1)]
    public ProtocolReason Reason { get; set; }

    /// <summary>Gets or sets the datagram connection identifier.</summary>
    [SerializeOrder(2)]
    public ulong ConnectionId { get; set; }

    /// <summary>Gets or sets the low 8 bytes of the ticket nonce.</summary>
    [SerializeOrder(3)]
    public ulong NonceLow { get; set; }

    /// <summary>Gets or sets the high 8 bytes of the ticket nonce.</summary>
    [SerializeOrder(4)]
    public ulong NonceHigh { get; set; }

    /// <summary>Gets or sets the UDP port to send to.</summary>
    [SerializeOrder(5)]
    public ushort Port { get; set; }

    /// <summary>Gets or sets the seconds within which the first datagram must arrive.</summary>
    [SerializeOrder(6)]
    public ushort ExpiresInSeconds { get; set; }

    /// <summary>Gets or sets the idle timeout of the session in milliseconds.</summary>
    [SerializeOrder(7)]
    public int IdleTimeoutMs { get; set; }

    /// <summary>Gets or sets the largest message either side accepts.</summary>
    [SerializeOrder(8)]
    public int MaxMessageSize { get; set; }

    /// <summary>Gets or sets the number of channels.</summary>
    [SerializeOrder(9)]
    public byte ChannelCount { get; set; }

    /// <summary>Gets or sets the total bytes the server buffers for ordering and reassembly.</summary>
    [SerializeOrder(10)]
    public long ReceiveBufferBytes { get; set; }

    /// <summary>Gets or sets the bytes the server buffers per channel.</summary>
    [SerializeOrder(11)]
    public long ChannelReceiveBufferBytes { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="DatagramTicket"/> packet.
    /// </summary>
    public DatagramTicket() => this.ResetForPool();

    /// <summary>Gets the nonce as one 128-bit value.</summary>
    [SerializeIgnore]
    public UInt128 Nonce => new(this.NonceHigh, this.NonceLow);

    /// <summary>Initializes a request.</summary>
    public void InitializeRequest()
    {
        this.ResetForPool();
        this.Stage = DatagramTicketStage.REQUEST;
    }

    /// <summary>Initializes a response that carries <paramref name="ticket"/>.</summary>
    public void InitializeResponse(in DatagramTicketInfo ticket)
    {
        this.ResetForPool();
        this.Stage = DatagramTicketStage.RESPONSE;
        this.ConnectionId = ticket.ConnectionId;
        this.NonceLow = (ulong)ticket.Nonce;
        this.NonceHigh = (ulong)(ticket.Nonce >> 64);
        this.Port = ticket.Port;
        this.ExpiresInSeconds = ticket.ExpiresInSeconds;
        this.IdleTimeoutMs = ticket.IdleTimeoutMs;
        this.MaxMessageSize = ticket.MaxMessageSize;
        this.ChannelCount = ticket.ChannelCount;
        this.ReceiveBufferBytes = ticket.ReceiveBufferBytes;
        this.ChannelReceiveBufferBytes = ticket.ChannelReceiveBufferBytes;
    }

    /// <summary>Initializes a refusal.</summary>
    public void InitializeRefusal(ProtocolReason reason)
    {
        this.ResetForPool();
        this.Stage = DatagramTicketStage.RESPONSE;
        this.Reason = reason;
    }

    /// <summary>Gets the ticket values of a response.</summary>
    public DatagramTicketInfo ToTicketInfo() => new(
        this.ConnectionId, this.Nonce, this.Port, this.ExpiresInSeconds, this.IdleTimeoutMs,
        this.MaxMessageSize, this.ChannelCount, this.ReceiveBufferBytes, this.ChannelReceiveBufferBytes);

    /// <inheritdoc/>
    public override void ResetForPool()
    {
        base.ResetForPool();

        this.Stage = DatagramTicketStage.NONE;
        this.Reason = ProtocolReason.NONE;
        this.ConnectionId = 0;
        this.NonceLow = 0;
        this.NonceHigh = 0;
        this.Port = 0;
        this.ExpiresInSeconds = 0;
        this.IdleTimeoutMs = 0;
        this.MaxMessageSize = 0;
        this.ChannelCount = 0;
        this.ReceiveBufferBytes = 0;
        this.ChannelReceiveBufferBytes = 0;
        this.Priority = PacketPriority.URGENT;
        this.Flags = PacketFlags.SYSTEM;
    }

    /// <inheritdoc/>
    public bool Validate([NotNullWhen(false)] out string? failureReason)
    {
        bool isValid = this.Stage switch
        {
            DatagramTicketStage.REQUEST => true,
            DatagramTicketStage.RESPONSE => this.Reason != ProtocolReason.NONE ||
                (this.ConnectionId != 0 && this.Port != 0 && this.ChannelCount is > 0 and <= 64 &&
                 this.IdleTimeoutMs > 0 && this.MaxMessageSize > 0 && this.ReceiveBufferBytes > 0 && this.ChannelReceiveBufferBytes > 0),
            DatagramTicketStage.NONE or _ => false,
        };

        failureReason = isValid ? null : $"Invalid fields for datagram ticket stage {this.Stage}.";
        return isValid;
    }
}

/// <summary>
/// Stage of a <see cref="DatagramTicket"/> exchange.
/// </summary>
public enum DatagramTicketStage : byte
{
    /// <summary>No stage assigned.</summary>
    NONE = 0x00,

    /// <summary>The client asks for a ticket.</summary>
    REQUEST = 0x01,

    /// <summary>The server answers.</summary>
    RESPONSE = 0x02,
}
