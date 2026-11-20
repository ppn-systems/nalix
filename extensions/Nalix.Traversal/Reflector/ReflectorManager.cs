// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using Nalix.Abstractions.Networking;
using Nalix.Environment.Configuration;
using Nalix.Environment.Random;
using Nalix.Traversal.Internal;
using Nalix.Traversal.Options;

namespace Nalix.Traversal.Reflector;

/// <summary>
/// Represents an active Reflector Session between two peers.
/// </summary>
public sealed class ReflectorSession
{
    public ulong Token { get; }
    public ulong PeerAId { get; }
    public ulong PeerBId { get; }

    private SocketAddress? _peerA;
    private SocketAddress? _peerB;

    /// <summary>
    /// Gets the UDP address learned for the first peer that sent a datagram, if any.
    /// </summary>
    public IPEndPoint? PeerAEndPoint => ToEndPoint(_peerA);

    /// <summary>
    /// Gets the UDP address learned for the second peer that sent a datagram, if any.
    /// </summary>
    public IPEndPoint? PeerBEndPoint => ToEndPoint(_peerB);

    /// <summary>
    /// Byte-level bandwidth limiter for this Reflector session.
    /// Default: 500 KB/s burst, 200 KB/s fill rate.
    /// </summary>
    internal TokenBucket Bucket { get; }

    private readonly IConnection _requester;
    private readonly ReflectorManager _manager;

    public ReflectorSession(ulong token, ulong peerAId, ulong peerBId, ReflectorManager manager, IConnection requester, long capacity, long fillRate)
    {
        this.Token = token;
        this.PeerAId = peerAId;
        this.PeerBId = peerBId;

        _requester = requester;
        _manager = manager;
        this.Bucket = new TokenBucket(capacity, fillRate);

        // Hook up event to clean up session when the TCP connection dies
        _requester.ConnectionClosed += this.OnConnectionClosed;
    }

    public void OnConnectionClosed(object? sender, IConnectionEventArgs args) => _manager.RemoveSession(this.Token);

    /// <summary>
    /// Learns the peer addresses and returns the address of the other peer. Called only from the relay's
    /// single receive loop.
    /// </summary>
    internal bool TryRoute(SocketAddress sender, [NotNullWhen(true)] out SocketAddress? target)
    {
        if (_peerA is null || _peerA.Equals(sender))
        {
            _peerA ??= Copy(sender);
            target = _peerB;
        }
        else if (_peerB is null || _peerB.Equals(sender))
        {
            _peerB ??= Copy(sender);
            target = _peerA;
        }
        else
        {
            // Token leaked to a third party: drop.
            target = null;
        }

        return target is not null;
    }

    private static SocketAddress Copy(SocketAddress address)
    {
        SocketAddress copy = new(address.Family, address.Size);
        address.Buffer.Span[..address.Size].CopyTo(copy.Buffer.Span);
        return copy;
    }

    private static IPEndPoint? ToEndPoint(SocketAddress? address)
    {
        if (address is null)
        {
            return null;
        }

        IPAddress any = address.Family == System.Net.Sockets.AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any;
        IPEndPoint endPoint = (IPEndPoint)new IPEndPoint(any, 0).Create(address);
        return endPoint.Address.IsIPv4MappedToIPv6 ? new IPEndPoint(endPoint.Address.MapToIPv4(), endPoint.Port) : endPoint;
    }

    public void Dispose() => _requester.ConnectionClosed -= this.OnConnectionClosed;
}

/// <summary>
/// Manages Reflector sessions and maps ReflectorTokens to connected endpoints.
/// </summary>
public sealed class ReflectorManager
{
    private static readonly ReflectorOptions s_options = ConfigurationManager.Instance.Get<ReflectorOptions>();

    private readonly ConcurrentDictionary<ulong, ReflectorSession> _sessions = new();

    public ulong CreateSession(ulong peerA, ulong peerB, IConnection requester)
    {
        ulong token;
        do
        {
            token = Csprng.NextUInt64();
        } while (token == 0 || _sessions.ContainsKey(token)); // Ensure token is not 0 and not colliding

        _sessions[token] = new ReflectorSession(token, peerA, peerB, this, requester, s_options.BandwidthBurstCapacity, s_options.BandwidthFillRate);
        return token;
    }

    /// <summary>
    /// Tries to get the active Reflector Session by its token.
    /// </summary>
    public bool TryGetSession(ulong token, [MaybeNullWhen(false)] out ReflectorSession session) => _sessions.TryGetValue(token, out session);

    /// <summary>
    /// Removes a Reflector Session.
    /// </summary>
    public void RemoveSession(ulong token)
    {
        if (_sessions.TryRemove(token, out ReflectorSession? session))
        {
            session.Dispose();
        }
    }
}
