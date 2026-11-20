// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions.Networking;

namespace Nalix.Traversal.Reflector;

/// <summary>
/// Raw UDP relay for the Reflector. It owns its own socket, so the relay does not depend on any Nalix
/// UDP session code: datagrams are opaque, start with the 8-byte reflector token, and are forwarded
/// unchanged to the other peer of the session.
/// </summary>
internal sealed class ReflectorRelay : IListener
{
    /// <summary>Largest UDP payload over IPv4.</summary>
    private const int MaxDatagramSize = 65_507;

    private readonly ReflectorManager _manager;
    private readonly ushort _port;
    private readonly object _gate = new();

    private Socket? _socket;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;
    private long _relayed;
    private long _dropped;

    public ReflectorRelay(ReflectorManager manager, ushort port)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _port = port;
    }

    /// <summary>
    /// Gets the local endpoint of the relay socket once it is active.
    /// </summary>
    public EndPoint? LocalEndPoint => _socket?.LocalEndPoint;

    /// <inheritdoc/>
    public void Activate(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_socket is not null)
            {
                return;
            }

            Socket socket = CreateSocket();
            try
            {
                socket.Bind(new IPEndPoint(socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, _port));
            }
            catch
            {
                socket.Dispose();
                throw;
            }

            _socket = socket;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _receiveLoop = Task.Run(() => this.ReceiveLoopAsync(socket, _cts.Token), CancellationToken.None);
        }
    }

    private static Socket CreateSocket()
    {
        if (Socket.OSSupportsIPv6)
        {
            try
            {
                return new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp) { DualMode = true };
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressFamilyNotSupported or SocketError.ProtocolNotSupported)
            {
                // IPv6 is compiled in but disabled on this host: fall back to IPv4 only.
            }
        }

        return new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    }

    /// <inheritdoc/>
    public void Deactivate(CancellationToken cancellationToken = default)
    {
        Socket? socket;
        CancellationTokenSource? cts;
        Task? loop;

        lock (_gate)
        {
            socket = _socket;
            cts = _cts;
            loop = _receiveLoop;
            _socket = null;
            _cts = null;
            _receiveLoop = null;
        }

        if (socket is null)
        {
            return;
        }

        cts?.Cancel();
        socket.Dispose();

        try
        {
            loop?.Wait(TimeSpan.FromSeconds(5), cancellationToken);
        }
        catch (Exception ex) when (ex is AggregateException or OperationCanceledException)
        {
            // The loop ends with an exception because its socket was closed.
        }

        cts?.Dispose();
    }

    /// <inheritdoc/>
    public void Dispose() => this.Deactivate();

    /// <inheritdoc/>
    public string GenerateReport()
        => $"ReflectorRelay port={_port} relayed={Interlocked.Read(ref _relayed)} dropped={Interlocked.Read(ref _dropped)}";

    /// <inheritdoc/>
    public void WriteReportData(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStartObject();
        writer.WriteNumber("port", _port);
        writer.WriteNumber("relayed", Interlocked.Read(ref _relayed));
        writer.WriteNumber("dropped", Interlocked.Read(ref _dropped));
        writer.WriteEndObject();
    }

    private async Task ReceiveLoopAsync(Socket socket, CancellationToken cancellationToken)
    {
        byte[] buffer = GC.AllocateUninitializedArray<byte>(MaxDatagramSize, pinned: true);
        SocketAddress sender = new(socket.AddressFamily);

        while (!cancellationToken.IsCancellationRequested)
        {
            int received;
            try
            {
                received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, sender, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.MessageSize)
            {
                // ICMP port unreachable from an earlier send, or an oversized datagram: keep receiving.
                continue;
            }

            if (this.TryRelay(buffer.AsSpan(0, received), sender, out SocketAddress? target))
            {
                try
                {
                    _ = socket.SendTo(buffer.AsSpan(0, received), SocketFlags.None, target);
                    _ = Interlocked.Increment(ref _relayed);
                }
                catch (SocketException)
                {
                    _ = Interlocked.Increment(ref _dropped);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
            else
            {
                _ = Interlocked.Increment(ref _dropped);
            }
        }
    }

    /// <summary>
    /// Decides where a datagram goes. The first sender of a session becomes peer A, the next distinct
    /// sender becomes peer B; a third address is rejected. Possessing the 64-bit CSPRNG token is the
    /// only proof required, as before.
    /// </summary>
    /// <param name="datagram">The received datagram, starting with the reflector token.</param>
    /// <param name="sender">The sender address. It is copied when it is stored.</param>
    /// <param name="target">The address to forward to.</param>
    /// <returns><see langword="true"/> when the datagram must be forwarded to <paramref name="target"/>.</returns>
    internal bool TryRelay(ReadOnlySpan<byte> datagram, SocketAddress sender, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SocketAddress? target)
    {
        target = null;

        if (datagram.Length < sizeof(ulong))
        {
            return false;
        }

        ulong token = BinaryPrimitives.ReadUInt64LittleEndian(datagram);
        if (!_manager.TryGetSession(token, out ReflectorSession? session))
        {
            return false;
        }

        if (!session.TryRoute(sender, out target))
        {
            return false;
        }

        // Byte-level bandwidth limit for the session.
        return session.Bucket.TryConsume(datagram.Length);
    }
}
