// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Net.WebSockets;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Codec.DataFrames;
using Nalix.Environment.Memory;
using Nalix.SDK.Options;
using Nalix.SDK.Transport.Internal.Ws;

namespace Nalix.SDK.Transport;

/// <summary>
/// Provides a WebSocket transport session built on <see cref="WsFrameReader"/> and <see cref="WsFrameSender"/>.
/// </summary>
public class WebSocketSession : TransportSession
{
    #region Fields

    private readonly WsFrameSender _sender;
    private readonly WsFrameReader _reader;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);

#pragma warning disable CA2213
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
#pragma warning restore CA2213

    private int _disposed;

    #endregion Fields

    #region Properties

    /// <inheritdoc/>
    public override TransportOptions Options { get; }

    /// <summary>Gets the WebSocket-specific options for this session.</summary>
    public WebSocketTransportOptions WebSocketOptions { get; }

    /// <inheritdoc/>
    public override bool IsConnected => _socket?.State == WebSocketState.Open && Volatile.Read(ref _disposed) == 0;

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

    /// <summary>Occurs when a complete frame is received and decoded asynchronously.</summary>
    public event Func<ReadOnlyMemory<byte>, Task>? OnMessageAsync;

    #endregion Events

    #region Constructor

    /// <summary>Initializes a new instance of the <see cref="WebSocketSession"/> class.</summary>
    /// <param name="options">The transport options for this session.</param>
    /// <param name="webSocketOptions">The WebSocket-specific transport options for this session.</param>
    /// <param name="state">An optional shared runtime state instance.</param>
    public WebSocketSession(TransportOptions options, WebSocketTransportOptions? webSocketOptions = null, SessionState? state = null) : base(state)
    {
        this.Options = options ?? throw new ArgumentNullException(nameof(options));
        this.WebSocketOptions = webSocketOptions ?? new WebSocketTransportOptions();
        this.WebSocketOptions.Validate();

        // Force eager creation so the reconnect supervisor subscribes to OnDisconnected
        // before any disconnect can occur — lazy creation on first RequestAsync failure
        // would miss the very disconnect it needs to react to.
        _ = this.ReconnectSupervisor;

        _sender = new WsFrameSender(() => _socket!, options, this.State, this.HandleError);
        _reader = new WsFrameReader(() => _socket!, options, this.State, this.WebSocketOptions, this.HandleReceiveMessage, this.HandleError);
    }

    #endregion Constructor

    #region APIs

    /// <inheritdoc/>
    public override async Task ConnectAsync(string? host = null, ushort? port = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, nameof(WebSocketSession));

        if (!PacketRegistry.IsBuilt)
        {
            PacketRegistry.Build();
        }

        await _connectionLock.WaitAsync(ct).ConfigureAwait(false);

        // #393: tracks whether THIS attempt's socket ever reached OnConnected, so the catch below
        // knows whether a raised OnDisconnected would have a matching "connected" event to pair with.
        bool connected = false;

        try
        {
            string effectiveHost = string.IsNullOrWhiteSpace(host) ? this.Options.Address : host;
            ushort effectivePort = port ?? this.Options.Port;

            if (_socket is not null || _loopCts is not null || _loopTask is not null)
            {
                await this.DisconnectInternalAsync(waitForLoop: true).ConfigureAwait(false);
            }

            this.ResetSequenceCounters();

            _socket = new ClientWebSocket();

            if (!string.IsNullOrWhiteSpace(this.WebSocketOptions.SubProtocol))
            {
                _socket.Options.AddSubProtocol(this.WebSocketOptions.SubProtocol);
            }

            string scheme = this.WebSocketOptions.UseTls ? "wss" : "ws";
            string path = NormalizeWebSocketPath(this.WebSocketOptions.Path);
            Uri uri = new($"{scheme}://{effectiveHost}:{effectivePort}{path}");

            using CancellationTokenSource connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (this.Options.ConnectTimeoutMillis > 0)
            {
                connectCts.CancelAfter(TimeSpan.FromMilliseconds(this.Options.ConnectTimeoutMillis));
            }

            await _socket.ConnectAsync(uri, connectCts.Token).ConfigureAwait(false);
            connected = true;
            this.OnConnected?.Invoke(this, EventArgs.Empty);

            _loopCts = new CancellationTokenSource();

            // Use fire-and-forget async instead of LongRunning thread —
            // LongRunning + TaskScheduler.Default deadlocks in single-threaded
            // runtimes (Blazor WASM). The async loop yields correctly via
            // ConfigureAwait(false) on both desktop and WASM.
            _loopTask = _reader.ReceiveLoopAsync(_loopCts.Token);
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            // #393: when `connected` is false there is no matching "connected" event for a raised
            // OnDisconnected to pair with — suppress it and let the NetworkException thrown below be
            // the one signal. During an outage every failed retry would otherwise raise one, making
            // "one OnDisconnected per real transport close" not a guarantee callers can build on.
            await this.DisconnectInternalAsync(waitForLoop: true, raiseDisconnected: connected).ConfigureAwait(false);
            this.OnError?.Invoke(this, ex);
            throw new NetworkException($"WebSocket Connection failed: {ex.Message}", ex);
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
            await this.DisconnectInternalAsync().ConfigureAwait(false);
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

    private async Task DisconnectInternalAsync(ClientWebSocket? expectedSocket = null, bool waitForLoop = false, bool raiseDisconnected = true)
    {
        ClientWebSocket? socket;
        CancellationTokenSource? loopCts;
        Task? loopTask;

        if (expectedSocket is not null)
        {
            socket = Interlocked.CompareExchange(ref _socket, null, expectedSocket);
            if (!ReferenceEquals(socket, expectedSocket))
            {
                // The socket field has already moved to a newer connection or been cleaned up.
                // Do not tear down the newer connection!
                return;
            }

            loopCts = Interlocked.Exchange(ref _loopCts, null);
            loopTask = Interlocked.Exchange(ref _loopTask, null);
        }
        else
        {
            loopCts = Interlocked.Exchange(ref _loopCts, null);
            loopTask = Interlocked.Exchange(ref _loopTask, null);
            socket = Interlocked.Exchange(ref _socket, null);
        }

        try
        {
#pragma warning disable CA1849
            loopCts?.Cancel();
#pragma warning restore CA1849
        }
        catch (ObjectDisposedException ex)
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                this.OnError?.Invoke(this, ex);
            }
        }
        finally
        {
            loopCts?.Dispose();
        }

        if (socket is not null)
        {
            try
            {
                if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived || socket.State == WebSocketState.CloseSent)
                {
                    using CancellationTokenSource cts = new(TimeSpan.FromSeconds(2));
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client disconnected", cts.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
            {
                if (Volatile.Read(ref _disposed) == 0)
                {
                    this.OnError?.Invoke(this, ex);
                }
            }

            socket.Dispose();

            // #393: a socket that never reached OnConnected (a failed/timed-out/refused connect
            // attempt) must not raise OnDisconnected — an app has no matching OnConnected to pair it
            // with, and during an outage every failed retry would otherwise raise one, making "one
            // OnDisconnected per real transport close" not a guarantee callers can build on.
            if (raiseDisconnected)
            {
                this.OnDisconnected?.Invoke(this, new NetworkException("The WebSocket session was disconnected."));
            }
        }

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

    /// <summary>
    /// Sends raw binary data synchronously.
    /// </summary>
    [UnsupportedOSPlatform("browser")]
    public void Send(ReadOnlySpan<byte> data, bool encrypt = true) => _sender.Send(data, encrypt);

    /// <inheritdoc/>
    public override async Task SendAsync(IPacket packet, CancellationToken ct = default)
        => await this.SendAsync(packet, this.State.EncryptionEnabled, ct).ConfigureAwait(false);

    /// <inheritdoc/>
    public override async Task SendAsync(IPacket packet, bool? encrypt = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(packet);
        this.StampSequenceIdIfUnset(packet);

        BufferLease lease = BufferLease.Rent(packet.Length);
        lease.CommitLength(packet.Serialize(lease.SpanFull));
        bool sent = await _sender.SendAsync(lease, encrypt, ct).ConfigureAwait(false);

        if (!sent)
        {
            throw new NetworkException("Failed to send WebSocket packet: the frame was not delivered to the socket.");
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
    }

    #endregion APIs

    #region Private

    private static string NormalizeWebSocketPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "/";
        }

        string normalized = path.Trim();
        return normalized[0] == '/' ? normalized : "/" + normalized;
    }

    private void HandleError(Exception ex, ClientWebSocket? originatingSocket = null)
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

    private void HandleReceiveMessage(IBufferLease lease)
    {
        try
        {
            this.OnMessageReceived?.Invoke(this, lease);

            if (this.OnMessageAsync is { } asyncHandler)
            {
                lease.Retain();

                Task dispatchTask;
                try
                {
                    dispatchTask = asyncHandler(lease.Memory);
                }
                catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
                {
                    lease.Dispose();
                    this.OnError?.Invoke(this, ex);
                    return;
                }

                if (dispatchTask.IsCompletedSuccessfully)
                {
                    lease.Dispose();
                }
                else
                {
                    _ = dispatchTask.ContinueWith(static (task, state) =>
                    {
                        if (state is not Tuple<WebSocketSession, IBufferLease> payload)
                        {
                            return;
                        }

                        WebSocketSession self = payload.Item1;
                        IBufferLease retained = payload.Item2;
                        try
                        {
                            if (task.Exception?.GetBaseException() is Exception ex)
                            {
                                self.OnError?.Invoke(self, ex);
                            }
                        }
                        finally
                        {
                            retained.Dispose();
                        }
                    }, Tuple.Create(this, lease), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
            }
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            this.OnError?.Invoke(this, ex);
        }
    }

    #endregion Private
}
