// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text;
using System;
using Nalix.Environment.Datagrams.Recovery;
using Nalix.Environment.Datagrams.Reliability;
using Nalix.Environment.Datagrams.Wire;

namespace Nalix.Environment.Datagrams;

public sealed partial class DatagramEngine
{
    private const int MaxCloseReasonBytes = 256;

    private bool _closeRemote;
    private bool _disposed;

    /// <summary>
    /// Releases every buffer, fails pending ACK receipts and disposes the protection. Raises the close event
    /// with <see cref="DatagramCloseCode.NoError"/> if the session had not closed yet.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_state != DatagramEngineState.Closed)
        {
            if (_state < DatagramEngineState.Draining)
            {
                _closeCode = DatagramCloseCode.NoError;
                _closeRemote = false;
            }

            this.Terminate();
        }

        _disposed = true;

        _recovery.TakeAll(_scratchPackets);
        foreach (SentPacket p in _scratchPackets)
        {
            foreach (OutMessage m in p.Messages)
            {
                _sender.Fail(m);
            }
        }

        _scratchPackets.Clear();
        _receiver.Dispose();
        _protection.Dispose();
    }

    /// <summary>
    /// Closes the session: a CLOSE frame is sent by the next <see cref="Poll"/>, then the session drains for
    /// three probe timeouts.
    /// </summary>
    /// <param name="code">The close code; <see cref="DatagramCloseCode.IdleTimeout"/> is not allowed.</param>
    /// <param name="reason">An optional reason, at most 256 UTF-8 bytes are sent.</param>
    /// <param name="now">The current time in microseconds.</param>
    /// <param name="applicationCode">The application code for <see cref="DatagramCloseCode.ApplicationError"/>.</param>
    public void Close(DatagramCloseCode code, string? reason, long now, ulong applicationCode = 0)
    {
        if (code == DatagramCloseCode.IdleTimeout)
        {
            throw new ArgumentException("IdleTimeout is local only.", nameof(code));
        }

        this.CloseLocal(code, applicationCode, reason ?? string.Empty, now);
        this.RaiseClosed();
    }

    private DatagramCloseCode ProcessClose(ReadOnlySpan<byte> payload, ref int offset, long now)
    {
        if (!VarInt.TryRead(payload, ref offset, out ulong code) || code > (ulong)DatagramCloseCode.ApplicationError)
        {
            code = (ulong)DatagramCloseCode.ProtocolViolation;
        }

        ulong applicationCode = 0;
        string reason = string.Empty;

        if ((DatagramCloseCode)code == DatagramCloseCode.ApplicationError && !VarInt.TryRead(payload, ref offset, out applicationCode))
        {
            applicationCode = 0;
        }

        if (VarInt.TryRead(payload, ref offset, out ulong reasonLength) && reasonLength <= (ulong)(payload.Length - offset))
        {
            reason = Encoding.UTF8.GetString(payload.Slice(offset, (int)reasonLength));
            offset += (int)reasonLength;
        }

        // The peer closed: drain without sending anything.
        _state = DatagramEngineState.Draining;
        _drainEnd = now + (3 * _rtt.ProbeTimeout(_maxAckDelay));
        _closeCode = (DatagramCloseCode)code;
        _closeApplicationCode = applicationCode;
        _closeReason = reason;
        _closeRemote = true;
        _sender.Close();
        return DatagramCloseCode.NoError;
    }

    private void CloseLocal(DatagramCloseCode code, ulong applicationCode, string reason, long now)
    {
        if (_state >= DatagramEngineState.Draining)
        {
            return;
        }

        _state = DatagramEngineState.Draining;
        _closeFramePending = true;
        _closeCode = code;
        _closeApplicationCode = applicationCode;
        _closeReason = reason;
        _closeRemote = false;
        _drainEnd = now + (3 * _rtt.ProbeTimeout(_maxAckDelay));
        _sender.Close();
    }

    /// <summary>Writes the CLOSE datagram queued by <see cref="CloseLocal"/>.</summary>
    private int WriteCloseDatagram(Span<byte> destination)
    {
        _closeFramePending = false;
        Span<byte> plaintext = _plaintext.AsSpan(0, destination.Length - DatagramHeader.Size - _protection.TagSize);

        // At most 64 UTF-16 code units, which encode to at most 256 UTF-8 bytes.
        ReadOnlySpan<char> reason = _closeReason.AsSpan(0, Math.Min(_closeReason.Length, MaxCloseReasonBytes / 4));
        int offset = 0;
        plaintext[offset++] = (byte)FrameType.Close;
        offset += VarInt.Write(plaintext[offset..], (ulong)_closeCode);
        if (_closeCode == DatagramCloseCode.ApplicationError)
        {
            offset += VarInt.Write(plaintext[offset..], Math.Min(_closeApplicationCode, VarInt.MaxValue));
        }

        int reasonLength = Encoding.UTF8.GetByteCount(reason);
        offset += VarInt.Write(plaintext[offset..], (ulong)reasonLength);
        offset += Encoding.UTF8.GetBytes(reason, plaintext[offset..]);

        int length = this.Seal(destination, plaintext[..offset], out _);
        plaintext.Clear();
        return length;
    }

    /// <summary>Moves to <see cref="DatagramEngineState.Closed"/> and raises the close event.</summary>
    private void Terminate()
    {
        _state = DatagramEngineState.Closed;
        _sender.Close();
        this.RaiseClosed();
    }

    private void RaiseClosed()
    {
        if (_closedRaised || _state < DatagramEngineState.Draining)
        {
            return;
        }

        _closedRaised = true;
        _handler.OnClosed(_closeCode, _closeApplicationCode, _closeReason, _closeRemote);
    }

    private void ReturnPacket(SentPacket p)
    {
        p.Reset();
        _packetPool.Push(p);
    }

    #region Timers

    /// <summary>
    /// Runs the timers that are due: idle timeout, draining, loss detection, probe timeout, keepalive, path
    /// validation retries, key discard and fragment expiry.
    /// </summary>
    /// <param name="now">The current time in microseconds.</param>
    public void OnTimer(long now)
    {
        if (_state == DatagramEngineState.Closed)
        {
            return;
        }

        if (_state == DatagramEngineState.Draining)
        {
            if (now >= _drainEnd)
            {
                this.Terminate();
            }

            return;
        }

        if (now >= _lastReceiveTime + this.IdleTimeout())
        {
            // Idle timeout closes silently.
            _closeCode = DatagramCloseCode.IdleTimeout;
            _closeRemote = false;
            this.Terminate();
            return;
        }

        if (now >= _discardPreviousKeyAt)
        {
            _protection.DiscardPreviousReceiveKey();
            _discardPreviousKeyAt = long.MaxValue;
        }

        if (_recovery.LossTime <= now)
        {
            this.DetectLost(now);
        }
        else if (this.LossOrProbeTime() <= now)
        {
            // Probe timeout: send ack-eliciting probes even if the congestion window is full.
            _ptoCount++;
            _probesPending = 2;
        }

        if (now >= _lastAckElicitingSent + (_idleTimeout / 3))
        {
            _pingPending = true;
        }

        _path.OnTimer(now);
        if (_mtuDiscovery.OnTimer(now, _rtt.HasSample, _rtt.ProbeTimeout(_maxAckDelay)))
        {
            _pingPending = true; // An RTT sample is needed before probing.
        }

        if (now >= _nextFragmentSweep)
        {
            _receiver.EvictExpired();
            _nextFragmentSweep = now + FragmentSweepInterval;
        }

        if (_pacingBlockedUntil <= now)
        {
            _pacingBlockedUntil = long.MaxValue;
        }
    }

    private long IdleTimeout() => Math.Max(_idleTimeout, 3 * _rtt.ProbeTimeout(_maxAckDelay));

    #endregion Timers
}
