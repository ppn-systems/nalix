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
using Nalix.SDK.Transport.Internal.Tcp;

namespace Nalix.SDK.Transport;

/// <summary>
/// Provides a TCP transport session built on <see cref="TcpFrameReader"/> and <see cref="TcpFrameSender"/>.
/// </summary>
[UnsupportedOSPlatform("browser")]
public class TcpSession : TransportSession
{
    #region Fields

    // Low-level components for reading and sending frames
    private readonly TcpFrameSender _sender;
    private readonly TcpFrameReader _reader;

    private readonly SemaphoreSlim _connectionLock = new(1, 1);
#pragma warning disable CA2213 // Disposed through Interlocked.Exchange locals inside DisconnectInternalAsync/Dispose(bool).
    private Socket? _socket;
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
#pragma warning restore CA2213
    private int _disposed;

    #endregion Fields

    #region Properties

    /// <summary>Gets the fixed framing header size in bytes.</summary>
    public const int HeaderSize = 2;

    /// <inheritdoc/>
    public override TransportOptions Options { get; }


    /// <inheritdoc/>
    public override bool IsConnected => _socket?.Connected == true && Volatile.Read(ref _disposed) == 0;

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

    /// <summary>Initializes a new instance of the <see cref="TcpSession"/> class.</summary>
    /// <param name="options">The transport options for this session.</param>
    /// <param name="state">An optional shared runtime state instance.</param>
    public TcpSession(TransportOptions options, SessionState? state = null) : base(state)
    {
        this.Options = options ?? throw new ArgumentNullException(nameof(options));

        // Force eager creation so the reconnect supervisor subscribes to OnDisconnected
        // before any disconnect can occur — lazy creation on first RequestAsync failure
        // would miss the very disconnect it needs to react to.
        _ = this.ReconnectSupervisor;

        // Initialize frame helpers with a factory to get the latest socket instance
        _sender = new TcpFrameSender(() => _socket!, options, this.State, this.HandleError);
        _reader = new TcpFrameReader(() => _socket!, options, this.State, this.HandleReceiveMessage, this.HandleError);
    }

    #endregion Constructor

    #region APIs

    /// <inheritdoc/>
    public override async Task ConnectAsync(string? host = null, ushort? port = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, nameof(TcpSession));

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

            // Initialize socket with NoDelay to reduce latency
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = this.Options.NoDelay };

            using CancellationTokenSource connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (this.Options.ConnectTimeoutMillis > 0)
            {
                connectCts.CancelAfter(TimeSpan.FromMilliseconds(this.Options.ConnectTimeoutMillis));
            }

            await _socket.ConnectAsync(effectiveHost, effectivePort, connectCts.Token).ConfigureAwait(false);

            if (_socket.LocalEndPoint is IPEndPoint localEp)
            {
                this.State.LocalPort = localEp.Port;
            }

            this.OnConnected?.Invoke(this, EventArgs.Empty);

            // Start background worker for reading frames
            _loopCts = new CancellationTokenSource();

            _loopTask = Task.Factory.StartNew(() => _reader.ReceiveLoopAsync(_loopCts.Token),
                _loopCts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            await this.DisconnectInternalAsync().ConfigureAwait(false);
            this.OnError?.Invoke(this, ex);
            throw new NetworkException($"Connection failed: {ex.Message}", ex);
        }
        finally
        {
            _ = _connectionLock.Release();
        }
    }

    /// <summary>
    /// Connects to the configured remote endpoint and injects a Proxy Protocol V2 header upon connection.
    /// Used primarily for testing and spoofing client IP addresses.
    /// </summary>
    public async Task ConnectWithProxyAsync(byte[] proxyProtocolV2, string? host = null, ushort? port = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(proxyProtocolV2);

        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, nameof(TcpSession));

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

            // Initialize socket with NoDelay to reduce latency
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = this.Options.NoDelay };

            using CancellationTokenSource connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (this.Options.ConnectTimeoutMillis > 0)
            {
                connectCts.CancelAfter(TimeSpan.FromMilliseconds(this.Options.ConnectTimeoutMillis));
            }

            await _socket.ConnectAsync(effectiveHost, effectivePort, connectCts.Token).ConfigureAwait(false);

            if (_socket.LocalEndPoint is IPEndPoint localEp)
            {
                this.State.LocalPort = localEp.Port;
            }

            // Inject Proxy Protocol V2 header if configured before triggering connected events
            _ = await _socket.SendAsync(proxyProtocolV2, SocketFlags.None, connectCts.Token).ConfigureAwait(false);

            this.OnConnected?.Invoke(this, EventArgs.Empty);

            // Start background worker for reading frames
            _loopCts = new CancellationTokenSource();

            _loopTask = Task.Factory.StartNew(() => _reader.ReceiveLoopAsync(_loopCts.Token),
                _loopCts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            await this.DisconnectInternalAsync(waitForLoop: true).ConfigureAwait(false);
            this.OnError?.Invoke(this, ex);
            throw new NetworkException($"Connection failed: {ex.Message}", ex);
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

    private async Task DisconnectInternalAsync(Socket? expectedSocket = null, bool waitForLoop = false)
    {
        Socket? socket;
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
#pragma warning disable CA1849 // DisconnectInternalAsync is synchronous teardown; callers cannot await CancelAsync without changing API shape.
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
                if (socket.Connected)
                {
                    socket.Shutdown(SocketShutdown.Both);
                }
            }
            catch (SocketException ex)
            {
                if (Volatile.Read(ref _disposed) == 0)
                {
                    this.OnError?.Invoke(this, ex);
                }
            }
            catch (ObjectDisposedException ex)
            {
                if (Volatile.Read(ref _disposed) == 0)
                {
                    this.OnError?.Invoke(this, ex);
                }
            }

            socket.Dispose();
            this.OnDisconnected?.Invoke(this, new NetworkException("The TCP session was disconnected."));
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
    /// Sends raw binary data synchronously (zero-allocation friendly for native).
    /// </summary>
    public void Send(ReadOnlySpan<byte> data, bool encrypt = true) => _sender.Send(data, encrypt);

    /// <inheritdoc/>
    public override void ResetSequenceCounters()
    {
        _sender.Sequence.Reset();
        _reader.Sequence.Reset();
    }

    /// <inheritdoc/>
    public override async Task SendAsync(IPacket packet, CancellationToken ct = default)
        => await this.SendAsync(packet, this.State.EncryptionEnabled, ct).ConfigureAwait(false);

    /// <summary>Sends a packet asynchronously with an optional encryption override.</summary>
    /// <param name="packet">The packet to serialize and send.</param>
    /// <param name="encrypt">A value that overrides packet encryption when provided.</param>
    /// <param name="ct">The token to observe while sending.</param>
    public override async Task SendAsync(IPacket packet, bool? encrypt = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(packet);
        this.StampSequenceIdIfUnset(packet);

        BufferLease lease = BufferLease.Rent(packet.Length);
        lease.CommitLength(packet.Serialize(lease.SpanFull));
        bool sent = await _sender.SendAsync(lease, encrypt, ct).ConfigureAwait(false);

        if (!sent)
        {
            throw new NetworkException("Failed to send TCP packet: the frame was not delivered to the socket.");
        }
    }

    /// <inheritdoc/>
    public override async Task SendAsync(ReadOnlyMemory<byte> payload, bool? encrypt = null, CancellationToken ct = default)
        => await _sender.SendAsync(payload, encrypt, ct).ConfigureAwait(false);

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
    /// Handles messages received by <see cref="TcpFrameReader"/>.
    /// </summary>
    private void HandleReceiveMessage(IBufferLease lease)
    {
        try
        {
            // Direct synchronous dispatch (hot path for benchmarks)
            this.OnMessageReceived?.Invoke(this, lease);

            // Concurrent asynchronous dispatch
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
                        if (state is not Tuple<TcpSession, IBufferLease> payload)
                        {
                            return;
                        }

                        TcpSession self = payload.Item1;
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
