// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions;
using Nalix.Abstractions.Diagnostics;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Security;
using Nalix.Environment.Configuration;
using Nalix.Environment.Sequencing;
using Nalix.Network.Connections;
using Nalix.Network.Options;

namespace Nalix.Network.Internal.Transport;

/// <summary>
/// A high-performance UDP transport implementation utilizing <see cref="Socket"/> directly.
/// Designed for zero-allocation transmission and efficient endpoint-bound communication.
/// </summary>
[SkipLocalsInit]
[DebuggerNonUserCode]
[EditorBrowsable(EditorBrowsableState.Never)]
internal sealed class SocketUdpTransport : IConnection.ITransport, IDisposable
{
    #region Static Factory

    private static readonly NetworkSocketOptions s_options = ConfigurationManager.Instance.Get<NetworkSocketOptions>();

    private readonly ISequenceCounter _sendSequence = new SequenceCounter();
    private readonly ISequenceCounter _receiveSequence = new SequenceCounter();

    #endregion Static Factory

    #region Properties

    /// <inheritdoc/>
    public TransportFraming Framing => TransportFraming.None;

    /// <inheritdoc/>
    public ISequenceCounter SendSequence => _sendSequence;

    /// <inheritdoc/>
    public ISequenceCounter ReceiveSequence => _receiveSequence;

    /// <inheritdoc/>
    public uint NextSendSequence() => _sendSequence.Next();

    /// <inheritdoc/>
    public uint NextReceiveSequence() => _receiveSequence.Next();

    /// <inheritdoc/>
    public uint CurrentSendSequence => _sendSequence.Current();

    /// <inheritdoc/>
    public uint CurrentReceiveSequence => _receiveSequence.Current();

    /// <summary>
    /// Gets the total number of bytes sent by this UDP transport instance.
    /// </summary>
    public long BytesSent => Volatile.Read(ref _bytesSent);

    /// <summary>
    /// Gets the total number of bytes received by this UDP transport instance.
    /// </summary>
    public long BytesReceived => Volatile.Read(ref _bytesReceived);

    #endregion Properties

    #region APIs

    /// <summary>
    /// Ensures that the connection has a valid UDP transport bound to the specified socket and remote endpoint.
    /// Reuses the existing transport if it is already bound to the same socket and endpoint.
    /// If the socket or endpoint changes, the previous transport is disposed and a new one is created.
    /// Transports are never pooled: a caller that kept a reference to a disposed transport must not be able to
    /// send to the endpoint of a later binding.
    /// </summary>
    public static void EnsureUDP(Connection connection, IPEndPoint remoteEndPoint, Socket socket)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(remoteEndPoint);

        SocketUdpTransport? current = connection.UdpTransport;

        if (current is not null)
        {
            if (ReferenceEquals(current._socket, socket) && current._endPoint is IPEndPoint ep && ep.Equals(remoteEndPoint))
            {
                return;
            }

            // A replacement is required. Clear connection reference first to prevent double-dispose or race usage.
            connection.SetUdpTransport(null!);

            try
            {
                current.Dispose();
            }
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
            {
                // Suppress non-fatal errors during cleanup
            }
        }

        SocketUdpTransport transport = new();

        transport.SetSocket(socket);

        IPEndPoint epTarget = remoteEndPoint;
        transport.Initialize(ref epTarget);
        connection.SetUdpTransport(transport);
    }

    #endregion APIs

    #region Fields

    private Socket? _socket;
    private EndPoint? _endPoint;

    /// <summary>
    /// Indicates whether this transport instance owns the lifecycle of its <see cref="_socket"/>.
    /// If <c>false</c>, the socket is provided by a listener and should not be disposed here.
    /// </summary>
    private bool _ownsSocket;

    private long _bytesSent;
    private long _bytesReceived;

    #endregion Fields

    #region Lifecycle

    /// <summary>
    /// Sets an external socket to be used for transmission.
    /// Used when the transport should share the listener's socket.
    /// </summary>
    internal void SetSocket(Socket socket)
    {
        ArgumentNullException.ThrowIfNull(socket);

        if (_socket != null && _ownsSocket)
        {
            _socket.Dispose();
        }

        _socket = socket;
        _ownsSocket = false;
    }

    /// <summary>
    /// Initializes the transport with a target endpoint.
    /// If no socket is currently set, a new one is created.
    /// </summary>
    public void Initialize(ref IPEndPoint iPEndPoint)
    {
        _endPoint = iPEndPoint;
        AddressFamily af = iPEndPoint.AddressFamily;

        if (_socket == null)
        {
            _socket = new Socket(af, SocketType.Dgram, ProtocolType.Udp);
            _ownsSocket = true;

            if (af == AddressFamily.InterNetworkV6)
            {
                try
                {
                    _socket.DualMode = true;
                }
                catch (Exception ex) when (ex is SocketException or NotSupportedException or ObjectDisposedException or InvalidOperationException)
                {
                    if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Debug))
                    {
                        string exceptionType = ex.GetType().Name;
                        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Debug))
                        {
                            DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Debug, new DiagnosticLog("NW.SocketUdpTransport:Initialize", $"dualmode-not-applied exception-type={exceptionType}", ex));
                        }
                        ;
                    }
                }
            }

            _socket.SendBufferSize = s_options.BufferSize;
            _socket.ReceiveBufferSize = s_options.BufferSize;

            try
            {
                _socket.DontFragment = true;
            }
            catch (SocketException ex)
            {
                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Debug))
                {
                    System.Net.Sockets.SocketError socketError = ex.SocketErrorCode;
                    if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Debug))
                    {
                        DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Debug, new DiagnosticLog("NW.SocketUdpTransport:Initialize", $"dontfragment-not-applied socket-error={socketError}", ex));
                    }
                    ;
                }
            }
            catch (NotSupportedException ex)
            {
                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Debug))
                {
                    string exceptionType = ex.GetType().Name;
                    if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Debug))
                    {
                        DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Debug, new DiagnosticLog("NW.SocketUdpTransport:Initialize", $"dontfragment-not-supported exception-type={exceptionType}", ex));
                    }
                    ;
                }
            }
            catch (ObjectDisposedException ex)
            {
                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Debug))
                {
                    string exceptionType = ex.GetType().Name;
                    if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Debug))
                    {
                        DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Debug, new DiagnosticLog("NW.SocketUdpTransport:Initialize", $"dontfragment-object-disposed exception-type={exceptionType}", ex));
                    }
                    ;
                }
            }
            catch (InvalidOperationException ex)
            {
                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Debug))
                {
                    string exceptionType = ex.GetType().Name;
                    if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Debug))
                    {
                        DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Debug, new DiagnosticLog("NW.SocketUdpTransport:Initialize", $"dontfragment-invalid-op exception-type={exceptionType}", ex));
                    }
                    ;
                }
            }

            if (OperatingSystem.IsWindows())
            {
                try
                {
                    const int SIO_UDP_CONNRESET = -1744830452;
                    _ = _socket.IOControl(SIO_UDP_CONNRESET, [0, 0, 0, 0], null);
                }
                catch (Exception ex) when (ex is SocketException or NotSupportedException or ObjectDisposedException)
                {
                    if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Debug))
                    {
                        string exceptionType = ex.GetType().Name;
                        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Debug))
                        {
                            DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Debug, new DiagnosticLog("NW.SocketUdpTransport:Initialize", $"udp-connreset-ioctl-not-applied exception-type={exceptionType}", ex));
                        }
                        ;
                    }
                }
            }
        }
    }

    #endregion Lifecycle

    #region Transmission

    /// <inheritdoc/>
    public void Send(ReadOnlySpan<byte> message)
    {
        // Snapshot the target once. Dispose may run concurrently; re-reading the field could observe a torn state.
        EndPoint? endPoint = _endPoint;
        Socket? socket = _socket;
        if (message.IsEmpty || endPoint == null || socket == null)
        {
            return;
        }

        if (message.Length > s_options.MaxUdpDatagramSize)
        {
            Throw.UdpPayloadTooLargeNow();
        }

        try
        {
            int sent = socket.SendTo(message, SocketFlags.None, endPoint);
            if (sent != message.Length)
            {
                Throw.UdpPartialSendNow();
            }
            _ = Interlocked.Add(ref _bytesSent, sent);
        }
        catch (Exception ex) when (ex is not NetworkException)
        {
            Throw.UdpSendFailedNow();
        }
    }

    /// <inheritdoc/>
    public async ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
    {
        // Same single snapshot as Send (see comment there).
        EndPoint? endPoint = _endPoint;
        Socket? socket = _socket;
        if (message.IsEmpty || endPoint == null || socket == null)
        {
            return;
        }

        if (message.Length > s_options.MaxUdpDatagramSize)
        {
            Throw.UdpPayloadTooLargeNow();
        }

        try
        {
            int sentBytes = await socket.SendToAsync(message, SocketFlags.None, endPoint, cancellationToken).ConfigureAwait(false);
            if (sentBytes != message.Length)
            {
                Throw.UdpPartialSendNow();
            }
            _ = Interlocked.Add(ref _bytesSent, sentBytes);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            Throw.UdpSendFailedNow();
        }
    }

    /// <inheritdoc/>
    public void BeginReceive(CancellationToken cancellationToken = default)
    {
        // Outbound transport does not handle incoming packets directly.
        // Reception is managed by UdpListenerBase or similar listener logic.
    }

    /// <inheritdoc/>
    public void UseFraming(TransportFraming framing)
    {
        // TODO: Review framing behavior. WebSocket currently relies on its built-in message framing.
    }

    /// <summary>
    /// Records bytes received from the listener.
    /// </summary>
    internal void RecordBytesReceived(long bytes) => Interlocked.Add(ref _bytesReceived, bytes);

    #endregion Transmission

    #region Disposal

    /// <summary>
    /// Clears the target and releases an owned socket. After this call the instance never sends again, so any
    /// caller still holding it becomes a no-op instead of reaching another connection's endpoint.
    /// </summary>
    public void Dispose()
    {
        _endPoint = null;

        if (_ownsSocket)
        {
            _socket?.Dispose();
        }

        _socket = null;
        _ownsSocket = false;

        _bytesSent = 0;
        _bytesReceived = 0;

        _sendSequence.Reset(0);
        _receiveSequence.Reset(0);
    }

    #endregion Disposal
}
