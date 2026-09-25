// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Codec.DataFrames;
using Nalix.Environment.Memory;
using Nalix.SDK.Options;
using Nalix.SDK.Transport.Internal.Udp;

#pragma warning disable CA2213 // Disposable fields should be disposed

namespace Nalix.SDK.Transport;

/// <summary>
/// Provides a high-performance UDP transport session supporting 8-byte session token authentication.
/// </summary>
/// <remarks>
/// <para>
/// Datagram layout for outbound packets: <c>[SessionToken (8 bytes) | Payload ...]</c>.
/// </para>
/// <para>
/// This class uses <see cref="UdpFrameSender"/> and <see cref="UdpFrameReader"/> 
/// to separate low-level frame handling, making the code consistent with <see cref="TcpSession"/>.
/// </para>
/// <para>
/// UDP does NOT perform framing or fragmentation.
/// </para>
/// </remarks>
[UnsupportedOSPlatform("browser")]
public class UdpSession : TransportSession
{
    #region Fields

    // Low-level components for reading and sending datagrams
    private readonly UdpFrameSender _sender;
    private readonly UdpFrameReader _reader;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);

    private Socket? _socket;
    private IPEndPoint? _remoteEndPoint;
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private int _disposed;

    #endregion Fields

    #region Properties

    /// <summary>
    /// Gets or sets the 8-byte session token (Snowflake) used to identify this session on the UDP channel.
    /// </summary>
    public ulong SessionToken
    {
        get => this.State.SessionToken;
        set => this.State.SessionToken = value;
    }

    /// <inheritdoc/>
    public override TransportOptions Options { get; }

    /// <inheritdoc/>
    public override bool IsConnected => _socket != null && Volatile.Read(ref _disposed) == 0;

    /// <summary>Occurs when a complete frame is received and decoded asynchronously.</summary>
    public event Func<ReadOnlyMemory<byte>, Task>? OnMessageAsync;

    #endregion Properties

    #region Events

    /// <inheritdoc/>
    public override event EventHandler? OnConnected;

    /// <inheritdoc/>
    public override event EventHandler<Exception>? OnDisconnected;

    /// <inheritdoc/>
    public override event EventHandler<IBufferLease>? OnMessageReceived;

    /// <inheritdoc/>
    public override event EventHandler<Exception>? OnError;

    #endregion Events

    #region Constructor

    /// <summary>Initializes a new instance of the <see cref="UdpSession"/> class.</summary>
    /// <param name="options">The transport options for this session.</param>
    /// <param name="state">An optional shared runtime state instance.</param>
    public UdpSession(TransportOptions options, SessionState? state = null) : base(state)
    {
        this.Options = options ?? throw new ArgumentNullException(nameof(options));

        // Force eager creation so the reconnect supervisor subscribes to OnDisconnected
        // before any disconnect can occur — lazy creation on first RequestAsync failure
        // would miss the very disconnect it needs to react to.
        _ = this.ReconnectSupervisor;

        // Initialize frame helpers with a factory to get the latest socket instance
        _sender = new UdpFrameSender(() => _socket!, options, this.State, this.HandleError);
        _reader = new UdpFrameReader(
            () => _socket!,
            options,
            this.State,
            this.HandleReceiveMessage,
            () => this.OnMessageAsync,
            this.HandleError);
    }

    #endregion Constructor

    #region APIs

    /// <inheritdoc/>
    public override async Task ConnectAsync(string? host = null, ushort? port = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, nameof(UdpSession));

        if (!PacketRegistry.IsBuilt)
        {
            PacketRegistry.Build();
        }

        await _connectionLock.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            string effectiveHost = string.IsNullOrWhiteSpace(host) ? this.Options.Address : host;
            ushort effectivePort = port ?? this.Options.Port;

            if (_socket is not null || _loopCts is not null || _loopTask is not null)
            {
                await this.DisconnectInternalAsync(waitForLoop: true).ConfigureAwait(false);
            }

            this.ResetSequenceCounters();

            // Resolve the remote endpoint
            IPAddress[] addresses = await Dns.GetHostAddressesAsync(effectiveHost, ct).ConfigureAwait(false);
            if (addresses.Length == 0)
            {
                throw new NetworkException($"Could not resolve host: {effectiveHost}");
            }

            _remoteEndPoint = new IPEndPoint(addresses[0], effectivePort);

            // Initialize UDP socket
            _socket = new Socket(_remoteEndPoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp)
            {
                SendBufferSize = this.Options.BufferSize,
                ReceiveBufferSize = this.Options.BufferSize
            };

            _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

            if (this.State.LocalPort > 0)
            {
                IPAddress bindAddress = _remoteEndPoint.AddressFamily == AddressFamily.InterNetworkV6
                    ? IPAddress.IPv6Any
                    : IPAddress.Any;
                _socket.Bind(new IPEndPoint(bindAddress, this.State.LocalPort));
            }

            // Apply connect timeout if configured
            using CancellationTokenSource connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (this.Options.ConnectTimeoutMillis > 0)
            {
                connectCts.CancelAfter(TimeSpan.FromMilliseconds(this.Options.ConnectTimeoutMillis));
            }

            await _socket.ConnectAsync(_remoteEndPoint, connectCts.Token).ConfigureAwait(false);

            _loopCts = new CancellationTokenSource();

            // Start background receive loop
            _loopTask = Task.Factory.StartNew(() => _reader.ReceiveLoopAsync(_loopCts.Token),
                _loopCts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

            this.OnConnected?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            await this.DisconnectInternalAsync(waitForLoop: true).ConfigureAwait(false);
            this.OnError?.Invoke(this, ex);
            throw new NetworkException($"UDP Connection failed: {ex.Message}", ex);
        }
        finally
        {
            _ = _connectionLock.Release();
        }
    }

    /// <inheritdoc/>
    public override async Task DisconnectAsync()
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        this.MarkIntentionalDisconnect();

        await _connectionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await this.DisconnectInternalAsync(waitForLoop: true).ConfigureAwait(false);
        }
        finally
        {
            // OnDisconnected only fires (and consumes the flag) when a live socket was torn
            // down. If the session was already disconnected the flag would otherwise stay set
            // and silently suppress auto-reconnect for the next genuine drop.
            _ = this.ConsumeIntentionalDisconnect();
            _ = _connectionLock.Release();
        }
    }

    private async Task DisconnectInternalAsync(Socket? expectedSocket = null, bool waitForLoop = false)
    {
        Socket? socket;
        CancellationTokenSource? cts;
        Task? loopTask;

        if (expectedSocket is not null)
        {
            socket = Interlocked.CompareExchange(ref _socket, null, expectedSocket);
            if (!ReferenceEquals(socket, expectedSocket))
            {
                // Socket already swapped or cleaned up by another thread.
                return;
            }

            cts = Interlocked.Exchange(ref _loopCts, null);
            loopTask = Interlocked.Exchange(ref _loopTask, null);
        }
        else
        {
            cts = Interlocked.Exchange(ref _loopCts, null);
            loopTask = Interlocked.Exchange(ref _loopTask, null);
            socket = Interlocked.Exchange(ref _socket, null);
        }

        try
        {
            await (cts?.CancelAsync() ?? Task.CompletedTask).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) { /* ignored */ }

        if (socket != null)
        {
            try
            {
                socket.Dispose();
            }
            catch (ObjectDisposedException) { /* ignored */ }

            this.OnDisconnected?.Invoke(this, new NetworkException("The UDP session was disconnected."));
        }

        cts?.Dispose();

        if (waitForLoop && loopTask is { IsCompleted: false })
        {
            try
            {
                await loopTask.ConfigureAwait(false);
            }
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
            {
                if (Volatile.Read(ref _disposed) == 0)
                {
                    this.OnError?.Invoke(this, ex);
                }
            }
        }
    }

    /// <inheritdoc/>
    public override async Task SendAsync(IPacket packet, CancellationToken ct = default)
        => await this.SendAsync(packet, null, ct).ConfigureAwait(false);

    /// <inheritdoc/>
    public override async Task SendAsync(IPacket packet, bool? encrypt = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(packet);
        this.StampSequenceIdIfUnset(packet);

        using BufferLease lease = BufferLease.Rent(packet.Length);
        int written = packet.Serialize(lease.SpanFull);
        lease.CommitLength(written);

        bool sent = await _sender.SendAsync(lease.Memory, encrypt, ct).ConfigureAwait(false);

        if (!sent)
        {
            throw new NetworkException("Failed to send UDP packet: the datagram was not delivered to the socket.");
        }
    }

    /// <inheritdoc/>
    public override async Task SendAsync(ReadOnlyMemory<byte> payload, bool? encrypt = null, CancellationToken ct = default)
        => await _sender.SendAsync(payload, encrypt, ct).ConfigureAwait(false);

    /// <inheritdoc/>
    public override void ResetSequenceCounters()
    {
        _sender.Sequence.Reset();
        _reader.Sequence.Reset();
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (!disposing || Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
        {
            return;
        }

        // Disposal is an application decision: the OnDisconnected raised below must not
        // start an auto-reconnect loop against a disposed session.
        this.MarkIntentionalDisconnect();
        _ = this.DisconnectInternalAsync();
        _sender.Dispose();
        _reader.Dispose();
        _connectionLock.Dispose();
        _socket?.Dispose();
        _loopCts?.Dispose();
    }

    #endregion APIs

    #region Private Handlers

    private void HandleError(Exception ex, Socket? originatingSocket = null)
    {
        if (originatingSocket is not null && !ReferenceEquals(Volatile.Read(ref _socket), originatingSocket))
        {
            return;
        }

        this.OnError?.Invoke(this, ex);
        // Do not go through the public DisconnectAsync() path — that marks the disconnect as
        // intentional (app-initiated), which would suppress auto-reconnect for what is actually
        // an unexpected fault.
        _ = this.DisconnectInternalAsync(expectedSocket: originatingSocket);
    }

    /// <summary>
    /// Handles messages received by <see cref="UdpFrameReader"/>.
    /// </summary>
    private void HandleReceiveMessage([Borrowed] IBufferLease lease)
    {
        try
        {
            // Direct synchronous dispatch (hot path)
            this.OnMessageReceived?.Invoke(this, lease);
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            this.OnError?.Invoke(this, ex);
        }
    }

    #endregion Private
}
