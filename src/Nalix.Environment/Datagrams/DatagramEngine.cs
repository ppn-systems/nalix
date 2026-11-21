// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Threading.Tasks;
using System;
using Nalix.Abstractions.Networking.Datagrams;
using Nalix.Environment.Datagrams.Congestion;
using Nalix.Environment.Datagrams.Paths;
using Nalix.Environment.Datagrams.Recovery;
using Nalix.Environment.Datagrams.Reliability;
using Nalix.Environment.Datagrams.Wire;
using Nalix.Environment.Options;

namespace Nalix.Environment.Datagrams;

/// <summary>
/// Protocol engine of one Nalix Datagram Protocol (NDP v1) session, without sockets ("sans-IO"): the owner
/// feeds received datagrams to <see cref="Receive"/>, sends what <see cref="Poll"/> produces, and calls
/// <see cref="OnTimer"/> at <see cref="NextTimer"/>. Times are microseconds from any monotonic clock
/// (<see cref="NowMicros"/>).
/// </summary>
/// <remarks>
/// <para>
/// The engine implements packet numbers, AEAD protection through <see cref="IDatagramProtection"/>, ACK ranges,
/// loss detection and NewReno congestion control with pacing (RFC 9002), DPLPMTUD (RFC 8899), keepalive, idle
/// timeout, close and draining, path validation, key update, and the delivery methods of
/// <see cref="DeliveryMethod"/> on independent channels with fragmentation and advertised receive budgets.
/// </para>
/// <para>Not thread-safe: the owner serializes all calls. The engine owns and disposes the protection.</para>
/// </remarks>
public sealed partial class DatagramEngine : IDisposable
{
    /// <summary>Smallest valid datagram: 13-byte header, one frame byte, 16-byte tag.</summary>
    public const int MinimumDatagramSize = DatagramHeader.Size + 1 + TagSizeBound;

    /// <summary>Datagrams that fail to decrypt before the session is closed (RFC 9001 §6.6, ChaCha20-Poly1305).</summary>
    private const long IntegrityLimit = 1L << 36;

    private const long FragmentSweepInterval = 1_000_000;

    private readonly DatagramEngineOptions _options;
    private readonly IDatagramProtection _protection;
    private readonly IDatagramEngineHandler _handler;
    private readonly ulong _connectionId;
    private readonly long _maxAckDelay;
    private readonly long _idleTimeout;

    private readonly ReplayWindow _replay = new();
    private readonly AckTracker _ackTracker = new();
    private readonly RttEstimator _rtt = new();
    private readonly ICongestionController _congestion;
    private readonly Pacer _pacer = new();
    private readonly ReliableSender _sender;
    private readonly ReliableReceiver _receiver;

    private readonly LossRecovery _recovery = new();
    private readonly Stack<SentPacket> _packetPool = new();
    private readonly List<SentPacket> _scratchPackets = [];
    private readonly List<ReliableReceiver.Delivery> _deliveries = [];
    private readonly byte[] _plaintext = new byte[MaxDatagramSize];
    private readonly byte[] _pathResponse = new byte[8];

    private DatagramEngineState _state;
    private ulong _nextPacketNumber;
    private int _ptoCount;
    private int _probesPending;
    private long _lastAckElicitingSent;
    private long _lastReceiveTime;
    private long _pacingBlockedUntil = long.MaxValue;
    private bool _pingPending;
    private bool _pathResponsePending;
    private bool _limitsPending;
    private DatagramPeerLimits _advertisedLimits;

    // Key update.
    private long _packetsInPhase;
    private long _phaseStart;
    private bool _phaseAcked;
    private bool _receiveKeyPhase;
    private long _discardPreviousKeyAt = long.MaxValue;
    private long _failedDecryptions;

    private readonly MtuDiscovery _mtuDiscovery;
    private readonly PathValidator _path = new();

    // Close.
    private bool _closeFramePending;
    private DatagramCloseCode _closeCode;
    private ulong _closeApplicationCode;
    private string _closeReason = string.Empty;
    private long _drainEnd = long.MaxValue;
    private bool _closedRaised;
    private long _nextFragmentSweep;

    /// <summary>
    /// Initializes the engine of one side of a session.
    /// </summary>
    /// <param name="options">This side's settings; channel count and idle timeout must match the peer's.</param>
    /// <param name="peerLimits">The receive budgets the peer advertised in the ticket.</param>
    /// <param name="protection">The AEAD protection of the session; the engine disposes it.</param>
    /// <param name="connectionId">The connection identifier from the ticket.</param>
    /// <param name="isServer"><see langword="true"/> on the server.</param>
    /// <param name="handler">Receives delivered messages and the close event.</param>
    /// <param name="now">The current time in microseconds.</param>
    public DatagramEngine(
        DatagramEngineOptions options, DatagramPeerLimits peerLimits, IDatagramProtection protection,
        ulong connectionId, bool isServer, IDatagramEngineHandler handler, long now)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(protection);
        ArgumentNullException.ThrowIfNull(handler);
        options.Validate();

        _options = options;
        _protection = protection;
        _handler = handler;
        _connectionId = connectionId;
        _maxAckDelay = options.MaxAckDelayMs * 1000L;
        _idleTimeout = options.IdleTimeoutMs * 1000L;
        _mtuDiscovery = new MtuDiscovery(options.EnableMtuProbing);
        _congestion = new NewRenoController(_mtuDiscovery.Mtu);
        _sender = new ReliableSender(options, peerLimits);
        _receiver = new ReliableReceiver(options);
        _advertisedLimits = DatagramPeerLimits.From(options);

        _lastReceiveTime = now;
        _lastAckElicitingSent = now;
        _phaseStart = now;
        _nextFragmentSweep = now + FragmentSweepInterval;

        // The client opens the session with a PING; the server only answers authenticated datagrams.
        _pingPending = !isServer;
    }

    /// <summary>Gets the current lifecycle state.</summary>
    public DatagramEngineState State => _state;

    /// <summary>Gets the connection identifier.</summary>
    public ulong ConnectionId => _connectionId;

    /// <summary>
    /// Reads the connection identifier of a datagram without authenticating it, to route it to its session.
    /// Fails for datagrams too short to be valid or with an unknown version or reserved bits set.
    /// </summary>
    /// <param name="datagram">The UDP payload.</param>
    /// <param name="connectionId">The connection identifier from the cleartext header.</param>
    /// <returns><see langword="true"/> when the header is well-formed.</returns>
    public static bool TryReadConnectionId(ReadOnlySpan<byte> datagram, out ulong connectionId)
    {
        if (datagram.Length < MinimumDatagramSize || !DatagramHeader.TryRead(datagram, out DatagramHeader header))
        {
            connectionId = 0;
            return false;
        }

        connectionId = header.ConnectionId;
        return true;
    }

    /// <summary>
    /// Gets a monotonic time in microseconds for the <c>now</c> arguments.
    /// </summary>
    public static long NowMicros() => (long)(Stopwatch.GetTimestamp() * (1_000_000.0 / Stopwatch.Frequency));

    #region Sending API

    /// <summary>
    /// Queues a message. Messages larger than <see cref="MaxUnfragmentedMessageSize"/> are fragmented and need a
    /// reliable ordered or unordered method.
    /// </summary>
    /// <param name="message">The message (a Nalix frame); it is copied.</param>
    /// <param name="method">The delivery method.</param>
    /// <param name="channel">The channel, below <see cref="DatagramEngineOptions.ChannelCount"/>.</param>
    /// <param name="priority">Higher values are sent first; levels above 7 are treated as 7.</param>
    /// <param name="now">The current time in microseconds.</param>
    /// <returns>
    /// <see langword="false"/> when the session is closing, the message is too large for its method, or the
    /// channel's send queue is full.
    /// </returns>
    public bool TrySend(ReadOnlySpan<byte> message, DeliveryMethod method, byte channel, byte priority, long now)
    {
        this.ValidateSend(method, channel);
        return _state < DatagramEngineState.Draining && _sender.TryEnqueue(message, method, channel, priority, now, receipt: null);
    }

    /// <summary>
    /// Queues a reliable message and returns a task that completes with <see langword="true"/> when every fragment
    /// was acknowledged, or <see langword="false"/> when the session closed first or a newer
    /// <see cref="DeliveryMethod.ReliableSequenced"/> message replaced it.
    /// </summary>
    /// <returns>The receipt, or <see langword="null"/> when the message was not queued (see <see cref="TrySend"/>).</returns>
    /// <exception cref="ArgumentException"><paramref name="method"/> is not reliable.</exception>
    public Task<bool>? TrySendWithReceipt(ReadOnlySpan<byte> message, DeliveryMethod method, byte channel, byte priority, long now)
    {
        this.ValidateSend(method, channel);
        if (!method.IsReliable())
        {
            throw new ArgumentException("ACK receipts need a reliable delivery method.", nameof(method));
        }

        TaskCompletionSource<bool> receipt = new(TaskCreationOptions.RunContinuationsAsynchronously);
        return _state < DatagramEngineState.Draining && _sender.TryEnqueue(message, method, channel, priority, now, receipt)
            ? receipt.Task
            : null;
    }

    /// <summary>
    /// Raises this side's advertised receive budgets; a LIMITS frame tells the peer. Budgets never shrink.
    /// </summary>
    public void RaiseReceiveLimits(DatagramPeerLimits limits)
    {
        _advertisedLimits = new DatagramPeerLimits(
            Math.Max(_advertisedLimits.ReceiveBufferBytes, limits.ReceiveBufferBytes),
            Math.Max(_advertisedLimits.ChannelReceiveBufferBytes, limits.ChannelReceiveBufferBytes));
        _limitsPending = true;
    }

    #endregion Sending API

    #region Poll

    /// <summary>
    /// Largest datagram <see cref="Poll"/> produces (the largest MTU probe).
    /// </summary>
    public const int MaxDatagramSize = 1472;

    /// <summary>
    /// Produces the next datagram to send, if any. Call repeatedly until it returns 0 after every
    /// <see cref="Receive"/>, <see cref="TrySend"/>, <see cref="Close"/> and <see cref="OnTimer"/>.
    /// </summary>
    /// <param name="destination">Receives the datagram; at least <see cref="MaxDatagramSize"/> bytes.</param>
    /// <param name="now">The current time in microseconds.</param>
    /// <param name="path">
    /// The address to send to when it is not the active path (a path challenge on the server); otherwise
    /// <see langword="null"/>.
    /// </param>
    /// <returns>The datagram length, or 0 when there is nothing to send now.</returns>
    public int Poll(Span<byte> destination, long now, out SocketAddress? path)
    {
        path = null;
        if (_state == DatagramEngineState.Closed)
        {
            return 0;
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, MaxDatagramSize, nameof(destination));

        if (_state == DatagramEngineState.Draining)
        {
            return _closeFramePending ? this.WriteCloseDatagram(destination[..DatagramEngineOptions.MinimumMtu]) : 0;
        }

        int challengeSize = DatagramHeader.Size + 1 + PathValidator.ChallengeSize + _protection.TagSize;
        Span<byte> challenge = _plaintext.AsSpan(0, 1 + PathValidator.ChallengeSize);
        if (_path.TryWriteChallenge(challenge, challengeSize, now, _rtt.ProbeTimeout(_maxAckDelay)) is { } challengeTarget)
        {
            int length = this.Seal(destination, challenge, out _);
            challenge.Clear();
            this.DatagramsSent++;
            path = challengeTarget;
            return length;
        }

        this.MaybeUpdateKey(now);

        bool probe = _probesPending > 0;
        int mtu = _mtuDiscovery.Mtu;
        bool windowOpen = _congestion.BytesInFlight + mtu <= _congestion.Window;
        bool canSendData = probe || windowOpen;
        if (canSendData && !probe)
        {
            long next = _pacer.NextSendTime(mtu, _congestion.Window, _rtt.Smoothed, mtu, now);
            if (next > now)
            {
                canSendData = false;
                _pacingBlockedUntil = next;
            }
        }

        int probeSize = canSendData && !probe && windowOpen ? _mtuDiscovery.PendingProbeSize : 0;
        bool mtuProbeDue = probeSize > 0;
        bool wantsData = _pingPending || _pathResponsePending || _limitsPending || probe || mtuProbeDue || _sender.MayHaveSendable;

        int capacity = (mtuProbeDue ? probeSize : mtu) - DatagramHeader.Size - _protection.TagSize;
        Span<byte> plaintext = _plaintext.AsSpan(0, capacity);
        SentPacket packet = _packetPool.Count > 0 ? _packetPool.Pop() : new SentPacket();
        int offset = 0;
        bool ackEliciting = false;

        bool ackDue = _ackTracker.AckPending && _ackTracker.AckDeadline <= now;
        bool ackWritten = false;
        if (_ackTracker.HasRanges && (ackDue || _pathResponsePending || (canSendData && wantsData)))
        {
            int written = _ackTracker.WriteAckFrame(plaintext, now, out ulong largestAcknowledged);
            if (written > 0)
            {
                offset += written;
                packet.LargestAckSent = (long)largestAcknowledged;
                ackWritten = true;
            }
        }

        // A PATH_RESPONSE is a probing frame: it goes out at once, past congestion control and pacing (RFC 9000
        // §8.2.2). After a NAT rebinding or a handover the peer sends its ACKs to the old address until this
        // answer validates the new one, so this side's window stays full; held back by it, the answer only left
        // with a backed-off probe timeout, after the peer had given up on its challenge, and the session stalled.
        if (_pathResponsePending && capacity - offset >= 9)
        {
            plaintext[offset++] = (byte)FrameType.PathResponse;
            _pathResponse.CopyTo(plaintext[offset..]);
            offset += 8;
            _pathResponsePending = false;
            ackEliciting = true;
        }

        if (canSendData)
        {
            if (_limitsPending && capacity - offset >= 17)
            {
                plaintext[offset++] = (byte)FrameType.Limits;
                offset += VarInt.Write(plaintext[offset..], (ulong)_advertisedLimits.ReceiveBufferBytes);
                offset += VarInt.Write(plaintext[offset..], (ulong)_advertisedLimits.ChannelReceiveBufferBytes);
                _limitsPending = false;
                ackEliciting = true;
            }

            if (mtuProbeDue)
            {
                // PING padded to the candidate size.
                plaintext[offset++] = (byte)FrameType.Ping;
                plaintext[offset..].Clear();
                offset = capacity;
                packet.IsMtuProbe = true;
                ackEliciting = true;
                _mtuDiscovery.OnProbeSent();
            }
            else
            {
                if (_sender.WriteMessages(plaintext[offset..], now, packet.Messages, out int messageBytes))
                {
                    ackEliciting = true;
                }

                offset += messageBytes;

                if (!ackEliciting && (_pingPending || probe))
                {
                    plaintext[offset++] = (byte)FrameType.Ping;
                    ackEliciting = true;
                }
            }

            if (ackEliciting)
            {
                _pingPending = false;
            }
        }

        // An ACK written for data that then had nothing to send (wantsData is conservative: a released flow-control
        // budget sets it with an empty queue) goes out only once it is due; until then the next datagram carries it.
        // Sent at once it was an ACK-only datagram ahead of the handler's reply on most requests.
        if (ackWritten && !ackEliciting && !ackDue)
        {
            plaintext[..offset].Clear();
            offset = 0;
        }
        else if (ackWritten)
        {
            _ackTracker.OnAckSent();
        }

        if (offset == 0)
        {
            this.ReturnPacket(packet);
            return 0;
        }

        bool keyPhase = _protection.SendKeyPhase;
        int datagramLength = this.Seal(destination, plaintext[..offset], out ulong packetNumber);
        plaintext[..offset].Clear();

        packet.PacketNumber = packetNumber;
        packet.TimeSent = now;
        packet.Size = datagramLength;
        packet.AckEliciting = ackEliciting;
        packet.InFlight = ackEliciting;
        packet.KeyPhase = keyPhase;
        _recovery.Add(packet);

        if (ackEliciting)
        {
            _congestion.OnPacketSent(datagramLength);
            _pacer.OnSent(datagramLength);
            _lastAckElicitingSent = now;
            if (probe)
            {
                _probesPending--;
            }
        }

        _packetsInPhase++;
        this.DatagramsSent++;
        return datagramLength;
    }

    private int Seal(Span<byte> destination, ReadOnlySpan<byte> plaintext, out ulong packetNumber)
    {
        packetNumber = _nextPacketNumber++;
        new DatagramHeader(_protection.SendKeyPhase, _connectionId, (uint)packetNumber).WriteTo(destination);
        _protection.Seal(packetNumber, destination[..DatagramHeader.Size], plaintext, destination[DatagramHeader.Size..]);
        return DatagramHeader.Size + plaintext.Length + _protection.TagSize;
    }

    private void MaybeUpdateKey(long now)
    {
        // A new update starts only after a packet of the current phase was acknowledged, so the peer
        // has seen the current phase and never falls two generations behind.
        if (_phaseAcked && (_packetsInPhase >= _options.KeyUpdatePacketInterval || now - _phaseStart >= _options.KeyUpdateIntervalMs * 1000))
        {
            _protection.UpdateSendKey();
            _packetsInPhase = 0;
            _phaseStart = now;
            _phaseAcked = false;
        }
    }

    #endregion Poll

    #region Receive

    /// <summary>
    /// Processes one received datagram.
    /// </summary>
    /// <param name="datagram">The UDP payload.</param>
    /// <param name="from">The sender address on the server (copied when kept); <see langword="null"/> on the client.</param>
    /// <param name="now">The current time in microseconds.</param>
    /// <returns><see langword="true"/> when the datagram authenticated and was processed.</returns>
    public bool Receive(ReadOnlySpan<byte> datagram, SocketAddress? from, long now)
    {
        if (_state >= DatagramEngineState.Draining || datagram.Length < MinimumDatagramSize || datagram.Length > MaxDatagramSize ||
            !DatagramHeader.TryRead(datagram, out DatagramHeader header) || header.ConnectionId != _connectionId)
        {
            this.DatagramsDropped++;
            return false;
        }

        ulong packetNumber = DatagramHeader.DecodePacketNumber(header.TruncatedPacketNumber, _replay.Largest);
        if (!_replay.IsNew(packetNumber))
        {
            this.DatagramsDropped++;
            return false;
        }

        int plaintextLength = datagram.Length - DatagramHeader.Size - _protection.TagSize;
        Span<byte> plaintext = _plaintext.AsSpan(0, plaintextLength);

        if (!_protection.TryOpen(packetNumber, header.KeyPhase, datagram[..DatagramHeader.Size], datagram[DatagramHeader.Size..], plaintext))
        {
            this.DatagramsDropped++;
            if (++_failedDecryptions >= IntegrityLimit)
            {
                this.CloseLocal(DatagramCloseCode.AeadLimitReached, 0, string.Empty, now);
                this.RaiseClosed();
            }

            return false;
        }

        bool largest = _replay.Largest < 0 || packetNumber > (ulong)_replay.Largest;
        _replay.Mark(packetNumber);
        _lastReceiveTime = now;
        this.DatagramsReceived++;

        if (header.KeyPhase != _receiveKeyPhase)
        {
            // The peer updated its key; the previous key stays for late packets for a while.
            _receiveKeyPhase = header.KeyPhase;
            _discardPreviousKeyAt = now + (3 * _rtt.ProbeTimeout(_maxAckDelay));
        }

        if (_state == DatagramEngineState.Pending)
        {
            _state = DatagramEngineState.Established;
            _mtuDiscovery.Schedule(now, _rtt.Smoothed);
        }

        if (from is not null)
        {
            _path.OnDatagram(from, datagram.Length, largest, now);
        }

        DatagramCloseCode error = this.ProcessFrames(plaintext, now, out bool ackEliciting);
        plaintext.Clear();

        if (_state < DatagramEngineState.Draining)
        {
            _ackTracker.OnPacketReceived(packetNumber, ackEliciting, now, _maxAckDelay);

            if (error != DatagramCloseCode.NoError)
            {
                this.CloseLocal(error, 0, string.Empty, now);
            }
        }

        this.DispatchDeliveries();
        this.RaiseClosed();
        return true;
    }

    private DatagramCloseCode ProcessFrames(ReadOnlySpan<byte> payload, long now, out bool ackEliciting)
    {
        ackEliciting = false;
        int offset = 0;

        while (offset < payload.Length)
        {
            FrameType type = (FrameType)payload[offset++];
            switch (type)
            {
                case FrameType.Padding:
                    while (offset < payload.Length && payload[offset] == 0)
                    {
                        offset++;
                    }

                    break;

                case FrameType.Ping:
                case FrameType.NewTicketHint:
                    ackEliciting = true;
                    break;

                case FrameType.Ack:
                    {
                        DatagramCloseCode result = this.ProcessAck(payload, ref offset, now);
                        if (result != DatagramCloseCode.NoError)
                        {
                            return result;
                        }

                        break;
                    }

                case FrameType.Message:
                    {
                        ackEliciting = true;
                        DatagramCloseCode result = this.ProcessMessage(payload, ref offset);
                        if (result != DatagramCloseCode.NoError)
                        {
                            return result;
                        }

                        break;
                    }

                case FrameType.PathChallenge:
                    ackEliciting = true;
                    if (payload.Length - offset < 8)
                    {
                        return DatagramCloseCode.FrameEncodingError;
                    }

                    payload.Slice(offset, 8).CopyTo(_pathResponse);
                    _pathResponsePending = true;
                    offset += 8;
                    break;

                case FrameType.PathResponse:
                    ackEliciting = true;
                    if (payload.Length - offset < 8)
                    {
                        return DatagramCloseCode.FrameEncodingError;
                    }

                    if (_path.OnResponse(payload.Slice(offset, 8)))
                    {
                        _congestion.Reset(); // New path: start from the initial window.
                    }

                    offset += 8;
                    break;

                case FrameType.Close:
                    return this.ProcessClose(payload, ref offset, now);

                case FrameType.Limits:
                    {
                        ackEliciting = true;
                        if (!VarInt.TryRead(payload, ref offset, out ulong total) || !VarInt.TryRead(payload, ref offset, out ulong perChannel))
                        {
                            return DatagramCloseCode.FrameEncodingError;
                        }

                        _sender.RaisePeerLimits(new DatagramPeerLimits((long)Math.Min(total, long.MaxValue), (long)Math.Min(perChannel, long.MaxValue)));
                        break;
                    }

                default:
                    return DatagramCloseCode.FrameEncodingError;
            }
        }

        return DatagramCloseCode.NoError;
    }

    private DatagramCloseCode ProcessMessage(ReadOnlySpan<byte> payload, ref int offset)
    {
        if (payload.Length - offset < 2)
        {
            return DatagramCloseCode.FrameEncodingError;
        }

        byte channel = payload[offset++];
        byte info = payload[offset++];
        if ((info & 0b1111_1000) != 0 || info > (byte)DeliveryMethod.ReliableSequenced)
        {
            return DatagramCloseCode.ProtocolViolation;
        }

        DeliveryMethod method = (DeliveryMethod)info;
        ulong reliableIndex = 0;
        ulong orderIndex = 0;

        if ((method == DeliveryMethod.ReliableUnordered && !VarInt.TryRead(payload, ref offset, out reliableIndex)) ||
            (method.HasOrderIndex() && !VarInt.TryRead(payload, ref offset, out orderIndex)) ||
            !VarInt.TryRead(payload, ref offset, out ulong length) ||
            length > (ulong)(payload.Length - offset))
        {
            return DatagramCloseCode.FrameEncodingError;
        }

        ReadOnlySpan<byte> message = payload.Slice(offset, (int)length);
        offset += (int)length;
        return _receiver.OnMessage(channel, method, reliableIndex, orderIndex, message, _deliveries);
    }

    #endregion Receive

    #region Acknowledgements and loss

    private DatagramCloseCode ProcessAck(ReadOnlySpan<byte> payload, ref int offset, long now)
    {
        List<SentPacket> acked = _scratchPackets;
        DatagramCloseCode result = _recovery.OnAckFrame(payload, ref offset, now, _nextPacketNumber, _rtt, _maxAckDelay, acked);
        if (result != DatagramCloseCode.NoError || acked.Count == 0)
        {
            acked.Clear();
            return result;
        }

        foreach (SentPacket p in acked)
        {
            this.OnPacketAcked(p, now);
        }

        acked.Clear();
        _ptoCount = 0;
        this.DetectLost(now);
        return DatagramCloseCode.NoError;
    }

    private void OnPacketAcked(SentPacket p, long now)
    {
        if (p.InFlight)
        {
            _congestion.OnPacketAcked(p.Size, p.TimeSent);
        }

        if (p.KeyPhase == _protection.SendKeyPhase)
        {
            _phaseAcked = true;
        }

        if (p.LargestAckSent >= 0)
        {
            _ackTracker.OnAckAcknowledged((ulong)p.LargestAckSent);
        }

        if (p.IsMtuProbe)
        {
            _mtuDiscovery.OnProbeAcked(p.Size, now, _rtt.Smoothed);
            _congestion.OnMtuChanged(_mtuDiscovery.Mtu);
        }
        else if (p.Size > DatagramEngineOptions.MinimumMtu)
        {
            _mtuDiscovery.OnLargePacketAcked();
        }

        foreach (OutMessage m in p.Messages)
        {
            _sender.OnAcked(m);
        }

        this.ReturnPacket(p);
    }

    private void DetectLost(long now)
    {
        _recovery.DetectLost(now, _rtt, _scratchPackets, out bool contiguous);
        if (_scratchPackets.Count == 0)
        {
            return;
        }

        long lostBytes = 0;
        long largestLostTime = long.MinValue;

        foreach (SentPacket p in _scratchPackets)
        {
            // An ACK-only datagram is never acknowledged on its own: the peer ACKs it only along with later
            // ack-eliciting ones, so the time threshold "loses" it whenever it is reordered. It carries nothing
            // to repair and nothing in flight, so it does not count as a loss.
            if (p.AckEliciting)
            {
                this.DatagramsLost++;
            }

            if (p.IsMtuProbe)
            {
                // A lost probe is not a congestion signal.
                _congestion.OnPacketDiscarded(p.Size);
                _mtuDiscovery.OnProbeLost(now, _rtt.Smoothed);
            }
            else if (p.InFlight)
            {
                lostBytes += p.Size;
                largestLostTime = Math.Max(largestLostTime, p.TimeSent);

                if (p.Size > DatagramEngineOptions.MinimumMtu && _mtuDiscovery.OnLargePacketLost(now))
                {
                    _congestion.OnMtuChanged(_mtuDiscovery.Mtu);
                }
            }

            foreach (OutMessage m in p.Messages)
            {
                _sender.OnLost(m);
            }

            p.Messages.Clear();
        }

        if (lostBytes > 0)
        {
            bool persistent = contiguous && LossRecovery.IsPersistentCongestion(_scratchPackets, _rtt, _maxAckDelay);
            _congestion.OnPacketsLost(lostBytes, largestLostTime, persistent, now);
        }

        foreach (SentPacket p in _scratchPackets)
        {
            this.ReturnPacket(p);
        }

        _scratchPackets.Clear();
    }

    private long LossOrProbeTime() => _recovery.NextTimeout(_rtt, _maxAckDelay, _ptoCount, _lastAckElicitingSent);

    #endregion Acknowledgements and loss

    private void ValidateSend(DeliveryMethod method, byte channel)
    {
        if ((uint)method > (uint)DeliveryMethod.ReliableSequenced)
        {
            throw new ArgumentOutOfRangeException(nameof(method));
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(channel, _options.ChannelCount, nameof(channel));
    }

    private void DispatchDeliveries()
    {
        if (_deliveries.Count == 0)
        {
            return;
        }

        // Handed over after the datagram is processed, so a handler that sends does not reenter frame parsing.
        foreach (ReliableReceiver.Delivery delivery in _deliveries)
        {
            if (_state == DatagramEngineState.Closed)
            {
                delivery.Message.Dispose();
            }
            else
            {
                _handler.OnMessage(delivery.Message, delivery.Method, delivery.Channel);
            }
        }

        _deliveries.Clear();
    }

}
