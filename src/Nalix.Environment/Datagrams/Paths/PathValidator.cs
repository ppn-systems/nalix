// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using System;
using Nalix.Environment.Datagrams.Wire;
using Nalix.Environment.Random;

namespace Nalix.Environment.Datagrams.Paths;

/// <summary>
/// The peer address on the server and its validation after NAT rebinding or migration (§10 of the NDP draft):
/// an authenticated datagram with the largest packet number so far from a new address starts a PATH_CHALLENGE;
/// the address is adopted after the matching PATH_RESPONSE, and until then at most 3x the bytes received from
/// it are sent to it.
/// </summary>
internal sealed class PathValidator
{
    public const int ChallengeSize = 8;
    private const int MaxAttempts = 3;

    private readonly byte[] _challenge = new byte[ChallengeSize];
    private SocketAddress? _candidate;
    private long _bytesReceived;
    private long _bytesSent;
    private int _attempts;

    /// <summary>Gets the validated peer address, or <see langword="null"/> before the first datagram.</summary>
    public SocketAddress? Active { get; private set; }

    /// <summary>Gets the time the next challenge is due, or <see cref="long.MaxValue"/>.</summary>
    public long NextChallengeTime { get; private set; } = long.MaxValue;

    public void OnDatagram(SocketAddress from, int size, bool largestSoFar, long now)
    {
        if (this.Active is null)
        {
            this.Active = Copy(from);
            return;
        }

        if (this.Active.Equals(from))
        {
            return;
        }

        if (_candidate is not null && _candidate.Equals(from))
        {
            _bytesReceived += size;
            if (this.NextChallengeTime == long.MaxValue && _attempts < MaxAttempts)
            {
                this.NextChallengeTime = now; // Unblocked: more bytes arrived from the address.
            }

            return;
        }

        if (!largestSoFar)
        {
            return; // A reordered datagram from an old address is not a migration.
        }

        _candidate = Copy(from);
        _bytesReceived = size;
        _bytesSent = 0;
        _attempts = 0;
        this.NextChallengeTime = now;
        Csprng.Fill(_challenge);
    }

    /// <summary>
    /// Writes a PATH_CHALLENGE frame when one is due and the amplification limit allows a datagram of
    /// <paramref name="datagramSize"/> bytes.
    /// </summary>
    /// <returns>The address to send it to, or <see langword="null"/>.</returns>
    public SocketAddress? TryWriteChallenge(Span<byte> frame, int datagramSize, long now, long probeTimeout)
    {
        if (_candidate is null || _attempts >= MaxAttempts || now < this.NextChallengeTime)
        {
            return null;
        }

        if (_bytesSent + datagramSize > 3 * _bytesReceived)
        {
            this.NextChallengeTime = long.MaxValue; // Wait for more bytes from the address.
            return null;
        }

        frame[0] = (byte)FrameType.PathChallenge;
        _challenge.CopyTo(frame[1..]);
        _bytesSent += datagramSize;
        _attempts++;
        this.NextChallengeTime = now + (2 * probeTimeout);
        return _candidate;
    }

    /// <returns><see langword="true"/> when the response validated the candidate, which is now active.</returns>
    public bool OnResponse(ReadOnlySpan<byte> data)
    {
        if (_candidate is null || !data.SequenceEqual(_challenge))
        {
            return false;
        }

        this.Active = _candidate;
        this.Abandon();
        return true;
    }

    public void OnTimer(long now)
    {
        if (_candidate is not null && _attempts >= MaxAttempts && now >= this.NextChallengeTime)
        {
            this.Abandon(); // Validation failed; keep the current path.
        }
    }

    private void Abandon()
    {
        _candidate = null;
        this.NextChallengeTime = long.MaxValue;
    }

    private static SocketAddress Copy(SocketAddress address)
    {
        SocketAddress copy = new(address.Family, address.Size);
        address.Buffer.Span[..address.Size].CopyTo(copy.Buffer.Span);
        return copy;
    }
}
