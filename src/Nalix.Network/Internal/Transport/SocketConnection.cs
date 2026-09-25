// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions;
using Nalix.Abstractions.Diagnostics;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Environment.Configuration;
using Nalix.Environment.Fragments;
using Nalix.Environment.Memory;
using Nalix.Environment.Options;
using Nalix.Environment.Time;
using Nalix.Framework.Memory.Objects;
using Nalix.Network.Connections;
using Nalix.Network.Internal.Pooling;
using Nalix.Network.Options;

#pragma warning disable CA2213 // Disposable fields should be disposed

#if DEBUG
[assembly: InternalsVisibleTo("Nalix.Network.Tests")]
[assembly: InternalsVisibleTo("Nalix.Network.Benchmarks")]
#endif

#nullable enable

namespace Nalix.Network.Internal.Transport;

/// <summary>
/// Manages the socket connection and handles sending/receiving data with caching and logging.
/// The receive path uses <see cref="PooledSocketReceiveContext"/> (SAEA-backed, pooled via
/// <see cref="ObjectPoolManager"/>) to eliminate per-receive allocations and scale
/// stably at 10 000+ concurrent connections.
///
/// <para><b>DDoS Protection (Layer 1 — Per-Connection Throttle):</b><br/>
/// Each connection tracks how many packets are currently pending processing via
/// <c>_pendingProcessCallbacks</c>. If a single connection floods packets faster
/// than the handler can process them, incoming packets are dropped at the receive
/// loop level — before they ever reach <see cref="AsyncCallback"/> or the ThreadPool.
/// This prevents a single abusive IP from consuming the global callback quota and
/// starving legitimate connections.</para>
/// </summary>
[DebuggerNonUserCode]
[SkipLocalsInit]
[DebuggerDisplay("{ToString()}")]
[ExcludeFromCodeCoverage]
internal sealed partial class SocketConnection : IDisposable, IPoolable
{
    #region Constructor and Pooling

    public SocketConnection()
    {
    }

    internal void Initialize(Socket socket, IConnection owner)
    {
        _socket = socket;
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _connectionOwner = owner as Connection;
        _endpointString = owner.NetworkEndpoint.ToString() ?? "Unknown";

        _buffer ??= BufferLease.ByteArrayPool.Rent(s_fragmentOptions.MinReceiveBufferSize);
    }

    public void ResetForPool()
    {
        // A pooled instance must never receive a shutdown callback meant for its previous
        // connection. DISPOSE already unregisters; this is a defensive second release.
        _shutdownRegistration.Dispose();
        _shutdownRegistration = default;

        _socket = null!;
        _owner = null!;
        _connectionOwner = null;
        _endpointString = "Unknown";

        _packetCount = 0;
        _bytesSent = 0;
        _bytesReceived = 0;
        _openFragmentStreams = 0;
        _receiveLoopTask = null;
        _fragmentAssembler = null;

        _disposed = 0;
        _closeSignaled = 0;
        _receiveStarted = 0;
        _cancelSignaled = 0;
        _socketDetached = 0;
        _framingLocked = 0;
        _framing = TransportFraming.None;

        _bufferDataLength = 0;
        this.StolenData = null;
        this.LastPingTime = 0;

        if (_buffer != null)
        {
            BufferLease.ByteArrayPool.Return(_buffer);
            _buffer = null;
        }
    }

    #endregion Constructor and Pooling

    #region Nested Types

    internal enum ReceiveResult : byte
    {
        Success,
        PeerClosed,
        InvalidPacketSize,
        SocketAborted,
        Failed
    }

    internal enum SendResult : byte
    {
        Success,
        PeerClosed,
        Aborted,
        Failed
    }

    #endregion Nested Types

    #region Const

    private const byte HeaderSize = sizeof(ushort);

    private static long s_evictedFragmentsTicks;
    private static long s_evictedFragmentsSuppressed;
    private static long s_receiveFaultedTicks;
    private static long s_receiveFaultedSuppressed;
    private static long s_fragmentErrorTicks;
    private static long s_fragmentErrorSuppressed;
    private static long s_receiveVarIntFaultedTicks;
    private static long s_receiveVarIntFaultedSuppressed;
    private static long s_protocolViolationTicks;
    private static long s_protocolViolationSuppressed;

    #endregion Const

    #region Fields

    private readonly Lock _sendLock = new();
    private Socket _socket = null!;

    /// <summary>
    /// Gets the underlying <see cref="System.Net.Sockets.Socket"/> for direct access.
    /// </summary>
    internal Socket Socket => _socket;
    private IConnection _owner = null!;
    private Connection? _connectionOwner;

    /// <summary>
    /// PooledReceiveContext wraps a PooledSocketAsyncEventArgs from ObjectPoolManager.
    /// One context per connection; returned to the pool on Dispose.
    /// </summary>
    private PooledSocketReceiveContext _recvCtx = null!;

    private int _packetCount;
    private long _bytesSent;
    private long _bytesReceived;
    private int _openFragmentStreams;
    private Task? _receiveLoopTask;
    private CancellationTokenRegistration _shutdownRegistration;
    private FragmentAssembler? _fragmentAssembler;

    /// <summary>
    /// 0 = no, 1 = yes
    /// </summary>
    private int _disposed;

    private int _closeSignaled;

    /// <summary>
    /// 0 = not yet, 1 = started
    /// </summary>
    private int _receiveStarted;
    /// <summary>
    /// 0 = not yet, 1 = started
    /// </summary>
    private int _cancelSignaled;

    /// <summary>
    /// 0 = no, 1 = yes
    /// </summary>
    private int _socketDetached;

    private static readonly FragmentOptions s_fragmentOptions = ConfigurationManager.Instance.Get<FragmentOptions>();
    private static readonly ObjectPoolManager s_pool = ObjectPoolManager.Shared;

    private static readonly int s_maxReceiveBufferSize = GET_RECEIVE_BUFFER_SIZE();

    /// <summary>
    /// Elastic receive buffer for opportunistic reads.
    /// Starts small and grows dynamically for large packets to save memory.
    /// </summary>
    private byte[]? _buffer = BufferLease.ByteArrayPool.Rent(s_fragmentOptions.MinReceiveBufferSize);

    private int _bufferDataLength;
    private string _endpointString = "Unknown";

    #endregion Fields

    #region Options

    /// <summary>
    /// Loaded once at startup from NetworkCallbackOptions via ConfigurationManager.
    /// All throttle values are read from config so they can be tuned without recompile.
    /// </summary>
    private static readonly NetworkCallbackOptions s_opts = ConfigurationManager.Instance.Get<NetworkCallbackOptions>();

    #endregion Options

    #region Properties

    /// <inheritdoc/>
    public long BytesSent => Interlocked.Read(ref _bytesSent);

    /// <inheritdoc/>
    public long BytesReceived => Interlocked.Read(ref _bytesReceived);

    /// <summary>
    /// Gets or sets the timestamp (in milliseconds) of the last received ping.
    /// Thread-safe via Interlocked operations.
    /// </summary>
    public long LastPingTime
    {
        get => Interlocked.Read(ref field);
        set => Interlocked.Exchange(ref field, value);
    } = Clock.UnixMillisecondsNow();

    /// <summary>
    /// Gets the task representing the receive loop.
    /// Used by unwrapped connections to await loop shutdown and recover stolen packets.
    /// </summary>
    public Task? ReceiveLoopTask => _receiveLoopTask;

    /// <summary>
    /// Contains bytes that were read from the kernel but not processed by the framework
    /// due to Unwrap() or cancellation.
    /// </summary>
    public byte[]? StolenData { get; private set; }

    #endregion Properties

    #region Public Methods

    /// <summary>
    /// Starts the SAEA-backed receive loop exactly once.
    /// The optional <paramref name="cancellationToken"/> participates in cooperative shutdown.
    /// </summary>
    /// <param name="cancellationToken"></param>
    [SuppressMessage(
        "CodeQuality", "IDE0079:Remove unnecessary suppression", Justification = "<Pending>")]
    [SuppressMessage(
        "Reliability", "CA2016:Forward the 'CancellationToken' parameter to methods", Justification = "<Pending>")]
    public void BeginReceive(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
#if DEBUG
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Debug))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Debug, new DiagnosticLog("NW.SocketConnection:BeginReceive", $"saea-receive-loop skip \u2014 already disposed endpoint={_endpointString}"));
            }
#endif
            return;
        }

        // Guard: start exactly once.
        if (Interlocked.CompareExchange(ref _receiveStarted, 1, 0) != 0)
        {
#if DEBUG
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Debug))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Debug, new DiagnosticLog("NW.SocketConnection:BeginReceive", $"saea-receive-loop skip \u2014 already started endpoint={_endpointString}"));
            }
#endif
            return;
        }

        // Lock framing mode. Once we start receiving, framing cannot be changed.
        Interlocked.CompareExchange(ref _framingLocked, 1, 0);

        // Acquire PooledReceiveContext from ObjectPoolManager — same pattern as
        // PooledAcceptContext usage in the accept loop.
        _recvCtx = s_pool.Get<PooledSocketReceiveContext>();
        _recvCtx.EnsureArgsBound();

#if DEBUG
        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Debug))
        {
            DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Debug, new DiagnosticLog("NW.SocketConnection:BeginReceive", $"saea-receive-loop started endpoint={_endpointString} framing={_framing}"));
        }
#endif

        // The receive loop only observes the token between receives, and a pending SAEA receive
        // is not cancellable through it. Without this registration a listener shutdown (e.g.
        // NetworkApplication.DeactivateAsync) cancels the token but leaves every established
        // connection open until the peer happens to send another frame, so clients never learn
        // the server went away and never auto-reconnect. Shutting the socket down completes the
        // pending receive and lets the loop run its normal close path.
        if (cancellationToken.CanBeCanceled)
        {
            _shutdownRegistration = cancellationToken.UnsafeRegister(
                static state => ((SocketConnection)state!).ABORT_RECEIVE_ON_SHUTDOWN(), this);
        }

        if (_framing == TransportFraming.VarIntLengthPrefixed)
        {
            _receiveLoopTask = this.SAEA_RECEIVE_LOOP_VARINT_ASYNC(cancellationToken);
        }
        else
        {
            _receiveLoopTask = this.SAEA_RECEIVE_LOOP_ASYNC(cancellationToken);
        }
    }

    /// <summary>
    /// Injects pre-read bytes (e.g. trailing data after a PROXY protocol header)
    /// into the connection's receive buffer before the normal receive loop starts.
    /// </summary>
    internal void InjectPreReadBytes(ReadOnlySpan<byte> preReadBytes)
    {
        if (preReadBytes.IsEmpty)
        {
            return;
        }

        if (_bufferDataLength + preReadBytes.Length > _buffer!.Length)
        {
            throw new InvalidOperationException("Pre-read bytes exceed receive buffer capacity.");
        }

        preReadBytes.CopyTo(_buffer.AsSpan(_bufferDataLength));
        _bufferDataLength += preReadBytes.Length;
    }

    /// <summary>
    /// Detaches the underlying socket, preventing it from being disposed.
    /// </summary>
    /// <returns>The detached socket.</returns>
    public Socket Unwrap()
    {
        Volatile.Write(ref _socketDetached, 1);
        this.CANCEL_RECEIVE_ONCE();
        return _socket;
    }

    #endregion Public Methods

    #region Dispose Pattern

    /// <summary>Disposes the resources used by this instance.</summary>
    public void Dispose()
    {
        this.DISPOSE(true);
        GC.SuppressFinalize(this);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override string ToString()
        => $"FramedSocketConnection (Client={_endpointString}, Disposed={Volatile.Read(ref _disposed) != 0}, LastPing={this.LastPingTime}ms, PendingPackets={_connectionOwner?.PendingPackets ?? 0}, OpenFragmentStreams={Volatile.Read(ref _openFragmentStreams)}.";

    #endregion Dispose Pattern

    #region Private: SAEA Receive Loop

    private static int GET_RECEIVE_BUFFER_SIZE()
    {
        if (s_fragmentOptions.MaxChunkSize <= 0)
        {
            throw new InvalidOperationException(
                $"[{nameof(SocketConnection)}] Invalid configuration: MaxChunkSize must be > 0, got {s_fragmentOptions.MaxChunkSize}.");
        }

        // 5 bytes is the max size of a VarInt header. We use this to ensure the buffer is
        // always large enough to fit the largest possible framing header (VarInt vs 2-byte ushort).
        return 5 + FragmentHeader.WireSize + s_fragmentOptions.MaxChunkSize;
    }

    /// <summary>
    /// Main receive loop — uses <see cref="PooledSocketReceiveContext"/> (SAEA) for zero-alloc receives.
    ///
    /// <para><b>Layer 1 throttle:</b> before handing a packet off to the cache, this loop
    /// checks <c>_pendingProcessCallbacks</c>. If the connection has
    /// <see cref="NetworkCallbackOptions.MaxPerConnectionPendingPackets"/> packets already queued in
    /// <see cref="AsyncCallback"/> awaiting a ThreadPool thread, the current packet is
    /// dropped and a warning is emitted. The buffer is returned to the pool immediately and
    /// a fresh one is rented so the loop can continue receiving (and discarding) the flood
    /// without stalling or allocating.</para>
    /// </summary>
    /// <param name="token"></param>
    /// <exception cref="SocketException"></exception>
    [DebuggerStepThrough]
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private async Task SAEA_RECEIVE_LOOP_ASYNC(CancellationToken token)
    {
        try
        {
            // The opportunistic loop: read as much as possible, then parse as many frames as possible.
            while (Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _cancelSignaled) == 0 && !token.IsCancellationRequested)
            {
                // Step 1: Parse all complete frames currently in the buffer.
                int consumed = 0;
                bool parsedAtLeastOne = false;
                int? pendingFrameSize = null;
                bool protocolViolation = false;

                while (_bufferDataLength - consumed >= HeaderSize)
                {
                    /*
                     * [Step 1: Header Peek]
                     * Every Nalix frame starts with a 2-byte little-endian length prefix.
                     * We peek at this header to determine the size of the incoming frame.
                     */
                    ushort size = BinaryPrimitives.ReadUInt16LittleEndian(MemoryExtensions
                                                  .AsSpan(_buffer!, consumed, HeaderSize));

                    if (!IS_VALID_PACKET_SIZE(size))
                    {

                        if (_owner is IConnectionTrafficMetrics trafficMetrics)
                        {
                            trafficMetrics.IncrementPacketsDropped();
                        }

                        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Warning))
                        {
                            if (Security.ThrottledEventGate.TryAcquire(ref s_protocolViolationTicks, ref s_protocolViolationSuppressed, DateTime.UtcNow.Ticks, TimeSpan.TicksPerSecond * 5, out long suppressed))
                            {
                                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Warning))
                                {
                                    DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Warning, new DiagnosticLog("NW.SocketConnection:Internal", $"invalid packet size prefix, closing connection size={size} endpoint={_owner.NetworkEndpoint.Address} suppressed-count={suppressed}"));
                                }
                            }
                        }

                        protocolViolation = true;
                        break;
                    }

                    // Check if the full frame (header + payload) is present in the buffer.
                    if (_bufferDataLength - consumed < size)
                    {
                        // Current frame is incomplete. Break and wait for more data.
                        pendingFrameSize = size;
                        break;
                    }

                    /*
                     * [Step 2: Dispatch Frame]
                     * If the full frame is present, we calculate the payload length
                     * (Total Size - 2 byte header) and hand it off for processing.
                     */
                    int payloadLen = size - HeaderSize;
                    this.PROCESS_FRAME_FROM_BUFFER(consumed + HeaderSize, payloadLen);

                    /*
                     * [Step 3: Fragment Cleanup]
                     * Periodic check to evict stale fragment streams. This prevents
                     * "slow-drip" DDoS attacks where an attacker sends partial fragments
                     * to consume server memory.
                     */
                    if ((++_packetCount & (FragmentAssembler.EvictInterval - 1)) == 0)
                    {
                        FragmentAssembler? fragmentAssembler = _fragmentAssembler;
                        int evicted = fragmentAssembler?.EvictExpired() ?? 0;
                        if (evicted > 0)
                        {
                            _ = Interlocked.Add(ref _openFragmentStreams, -evicted);

                            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Warning))
                            {
                                if (Security.ThrottledEventGate.TryAcquire(ref s_evictedFragmentsTicks, ref s_evictedFragmentsSuppressed, DateTime.UtcNow.Ticks, TimeSpan.TicksPerSecond * 5, out long suppressed))
                                {
                                    if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Warning))
                                    {
                                        DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Warning, new DiagnosticLog("NW.SocketConnection:Internal", $"evicted stale fragment stream(s) evicted-count={evicted} endpoint={_owner.NetworkEndpoint.Address} suppressed-count={suppressed}"));
                                    }
                                    ;
                                }
                            }
                        }
                    }

                    consumed += size;
                    parsedAtLeastOne = true;
                }

                // Invariant: every inner-loop exit either dispatched+advanced consumed,
                // recorded a legitimate wait (pendingFrameSize / not-enough-header-bytes),
                // or flagged a protocol violation. It must never silently stall.
                Debug.Assert(
                    protocolViolation || pendingFrameSize.HasValue ||
                    _bufferDataLength - consumed < HeaderSize || parsedAtLeastOne,
                    "SAEA receive loop inner exit with no progress and no recorded wait/violation state.");

                if (protocolViolation)
                {
                    break;
                }

                /*
                 * [Step 4: Buffer Compaction]
                 * Move any unconsumed data (partial frames) to the front of the
                 * buffer so we can read more data into the free space at the end.
                 */
                if (consumed > 0)
                {
                    int remaining = _bufferDataLength - consumed;
                    if (remaining > 0)
                    {
                        Buffer.BlockCopy(_buffer!, consumed, _buffer!, 0, remaining);
                    }
                    _bufferDataLength = remaining;
                }

                /*
                 * [Step 4.5: Elastic Buffer Resizing]
                 * If a large frame is pending, grow the buffer exactly to its size.
                 * If we just finished processing all data and are completely idle
                 * (0 bytes left) and not processing a fragment stream, shrink the buffer.
                 */
                if (pendingFrameSize.HasValue)
                {
                    int requiredSize = pendingFrameSize.Value;
                    if (requiredSize > s_maxReceiveBufferSize)
                    {
                        throw Throw.GetMessageSize();
                    }

                    if (requiredSize > _buffer!.Length)
                    {
                        byte[] newBuffer = BufferLease.ByteArrayPool.Rent(requiredSize);
                        if (_bufferDataLength > 0)
                        {
                            Buffer.BlockCopy(_buffer, 0, newBuffer, 0, _bufferDataLength);
                        }
                        BufferLease.ByteArrayPool.Return(_buffer);
                        _buffer = newBuffer;
                    }
                }
                else if (_bufferDataLength == 0 && _buffer!.Length > s_fragmentOptions.MinReceiveBufferSize && Volatile.Read(ref _openFragmentStreams) == 0)
                {
                    byte[] newBuffer = BufferLease.ByteArrayPool.Rent(s_fragmentOptions.MinReceiveBufferSize);
                    BufferLease.ByteArrayPool.Return(_buffer);
                    _buffer = newBuffer;
                }

                /*
                 * [Step 5: Opportunistic Read]
                 * If we didn't parse any frames or the buffer is empty, we await more
                 * data from the socket. This avoids a tight CPU spin.
                 */
                if (!parsedAtLeastOne || _bufferDataLength < HeaderSize)
                {
                    ReceiveResult receiveResult = await this.RECEIVE_OPPORTUNISTIC_ASYNC(token).ConfigureAwait(false);
                    if (receiveResult != ReceiveResult.Success)
                    {
                        if (receiveResult == ReceiveResult.InvalidPacketSize)
                        {
                            if (_owner is IConnectionTrafficMetrics trafficMetrics)
                            {
                                trafficMetrics.IncrementPacketsDropped();
                            }
                        }
                        break;
                    }
                }
            }
        }
        catch (Exception ex) when (IS_BENIGN_DISCONNECT(ex))
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Trace))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Trace, new DiagnosticLog("NW.SocketConnection:Internal", $"saea-receive-loop ended (peer closed/shutdown) endpoint={_owner?.NetworkEndpoint.Address}"));
            }
        }
        catch (OperationCanceledException)
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Trace))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Trace, new DiagnosticLog("NW.SocketConnection:Internal", $"saea-receive-loop cancelled endpoint={_owner?.NetworkEndpoint.Address}"));
            }
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            this.HANDLE_RECEIVE_LOOP_ERROR(ex);
        }
        finally
        {
            if (Volatile.Read(ref _socketDetached) == 1 && _bufferDataLength > 0)
            {
                this.StolenData = new byte[_bufferDataLength];
                Buffer.BlockCopy(_buffer!, 0, this.StolenData, 0, _bufferDataLength);
            }
            this.CANCEL_RECEIVE_ONCE();
            this.INVOKE_CLOSE_ONCE();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void HANDLE_RECEIVE_LOOP_ERROR(Exception ex)
    {
        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Trace))
        {
            Exception e = (ex as AggregateException)?.Flatten() ?? ex;
            if (Security.ThrottledEventGate.TryAcquire(ref s_receiveFaultedTicks, ref s_receiveFaultedSuppressed, DateTime.UtcNow.Ticks, TimeSpan.TicksPerSecond * 5, out long suppressed))
            {
                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Trace))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Trace, new DiagnosticLog("NW.SocketConnection:Internal", $"receive faulted endpoint={_owner.NetworkEndpoint.Address} suppressed-count={suppressed}", e));
                }
                ;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ValueTask<ReceiveResult> RECEIVE_OPPORTUNISTIC_ASYNC(CancellationToken token)
    {
        if (token.IsCancellationRequested)
        {
            return ValueTask.FromResult(ReceiveResult.SocketAborted);
        }

        int freeSpace = _buffer!.Length - _bufferDataLength;
        if (freeSpace == 0)
        {
            return ValueTask.FromResult(ReceiveResult.InvalidPacketSize);
        }

        try
        {
            ValueTask<int> vt = _recvCtx.ReceiveAsync(_socket, _buffer, _bufferDataLength, freeSpace);

            if (vt.IsCompletedSuccessfully)
            {
                int n = vt.Result;
                if (n == 0)
                {
                    return ValueTask.FromResult(ReceiveResult.PeerClosed);
                }

                _bufferDataLength += n;
                _ = Interlocked.Add(ref _bytesReceived, n);

                return ValueTask.FromResult(ReceiveResult.Success);
            }

            return AWAIT_RECEIVE(this, vt);
        }
        catch (SocketException ex) when (ex.SocketErrorCode is
               SocketError.ConnectionReset or
               SocketError.ConnectionAborted or
               SocketError.Shutdown or
               SocketError.OperationAborted)
        {
            return ValueTask.FromResult(ReceiveResult.SocketAborted);
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            return ValueTask.FromResult(ReceiveResult.Failed);
        }

        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
        static async ValueTask<ReceiveResult> AWAIT_RECEIVE(SocketConnection self, ValueTask<int> vt)
        {
            try
            {
                int n = await vt.ConfigureAwait(false);
                if (n == 0)
                {
                    return ReceiveResult.PeerClosed;
                }

                self._bufferDataLength += n;
                _ = Interlocked.Add(ref self._bytesReceived, n);
                return ReceiveResult.Success;
            }
            catch (SocketException ex) when (ex.SocketErrorCode is
                   SocketError.ConnectionReset or
                   SocketError.ConnectionAborted or
                   SocketError.Shutdown or
                   SocketError.OperationAborted)
            {
                return ReceiveResult.SocketAborted;
            }
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
            {
                return ReceiveResult.Failed;
            }
        }
    }

    /// <summary>
    /// Processes a single frame. Fragmented frames are handled zero-copy directly
    /// from the receive buffer. Regular frames are copied into a new BufferLease.
    /// </summary>
    private void PROCESS_FRAME_FROM_BUFFER(int offset, int payloadLen)
    {
        ReadOnlySpan<byte> rawPayloadSpan = MemoryExtensions.AsSpan(_buffer, offset, payloadLen);

        // Fragment Assembly Check (Zero-Copy Peak).
        // A FragmentHeader is 8 bytes. We peek directly from the SAEA buffer BEFORE renting any leases.
        if (FragmentAssembler.IsFragmentedFrame(rawPayloadSpan, out FragmentHeader header))
        {
            // Direct zero-copy handoff. Only the inner chunk body is passed.
            this.HANDLE_FRAGMENTED_FRAME_DIRECT(header, rawPayloadSpan[FragmentHeader.WireSize..]);
            return;
        }

        /*
         * [Buffer Leasing - Regular Frame Path]
         * We copy the frame into a new BufferLease so the receive loop can
         * continue reading from the socket without waiting for the protocol
         * handler to finish.
         */
        BufferLease lease = BufferLease.CopyFrom(rawPayloadSpan);
        lease.IsReliable = true;

        // Safety: The application protocol (FramePipeline) requires a valid packet header.
        // If the payload is too small, it's a malformed packet that would cause OOB reads.
        if (payloadLen < PacketConstants.HeaderSize)
        {
#if DEBUG
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Warning))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Warning, new DiagnosticLog("NW.SocketConnection:Internal", $"malformed-payload (too small for protocol header) payload-length={payloadLen} endpoint={_endpointString}"));
            }
#endif
            lease.Dispose();
            return;
        }

        // Regular Frame Path.
        // Update last-ping timestamp at the transport layer so the timing
        // wheel sees activity even if the sink drops the frame.
        this.LastPingTime = Clock.UnixMillisecondsNow();

        bool handled = _connectionOwner?.OnFrameReceived(lease) ?? true;
        if (!handled)
        {
            if (_owner is IConnectionTrafficMetrics trafficMetrics)
            {
                trafficMetrics.IncrementPacketsDropped();
            }
#if DEBUG
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Warning))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Warning, new DiagnosticLog("NW.SocketConnection:Internal", $"frame-dropped payload-length={payloadLen} endpoint={_endpointString}"));
            }
#endif
            lease.Dispose();
        }

#if DEBUG
        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Debug))
        {
            DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Debug, new DiagnosticLog("NW.SocketConnection:Internal", $"handoff-to-sink payload-length={payloadLen} endpoint={_endpointString}"));
        }
#endif
    }

    /// <summary>
    /// Helper to handle fragmented frames directly from the receive buffer.
    /// This eliminates double-copying and temporary chunk lease renting.
    /// </summary>
    private void HANDLE_FRAGMENTED_FRAME_DIRECT(FragmentHeader header, ReadOnlySpan<byte> chunkBody)
    {
        try
        {
            FragmentAssembler fragmentAssembler = this.GET_OR_CREATE_FRAGMENT_ASSEMBLER();

            if (header.ChunkIndex == 0)
            {
                int openStreams = Interlocked.Increment(ref _openFragmentStreams);

                if (openStreams > s_opts.MaxPerConnectionOpenFragmentStreams)
                {
                    Interlocked.Decrement(ref _openFragmentStreams);

                    if (s_opts.OverflowPolicy == NetworkOverflowPolicy.Disconnect)
                    {
                        _connectionOwner?.Disconnect("Exceeded MaxPerConnectionOpenFragmentStreams threshold.");
                    }
#if DEBUG
                    if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Debug))
                    {
                        DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Debug, new DiagnosticLog("NW.SocketConnection:Internal", $"fragment-limit open-streams={openStreams} endpoint={_endpointString}"));
                    }
#endif
                    return;
                }
            }

#if DEBUG
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Debug))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Debug, new DiagnosticLog("NW.SocketConnection:Internal", $"recv-frag stream-id={header.StreamId} chunk-index={header.ChunkIndex} total-chunks={header.TotalChunks} is-last={header.IsLast} endpoint={_endpointString}"));
            }
#endif

            FragmentAssemblyResult? assembled = fragmentAssembler.Add(header, chunkBody, out bool streamEvicted);

            if (assembled is not null)
            {
                BufferLease assembledLease = assembled.Value.Lease;
                assembledLease.IsReliable = true;
                assembledLease.Retain();

                this.LastPingTime = Clock.UnixMillisecondsNow();

                bool handled = _connectionOwner?.OnFrameReceived(assembledLease) ?? true;
                if (!handled)
                {
                    if (_owner is IConnectionTrafficMetrics trafficMetrics)
                    {
                        trafficMetrics.IncrementPacketsDropped();
                    }
                    Interlocked.Decrement(ref _openFragmentStreams);
                    assembledLease.Dispose();
                }
                else
                {
#if DEBUG
                    if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Debug))
                    {
                        DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Debug, new DiagnosticLog("NW.SocketConnection:Internal", $"assembled stream stream-id={header.StreamId} endpoint={_endpointString}"));
                    }
#endif
                    Interlocked.Decrement(ref _openFragmentStreams);
                    assembledLease.Dispose();
                }
            }
            else if (streamEvicted)
            {
                Interlocked.Decrement(ref _openFragmentStreams);
            }
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            this.HANDLE_FRAGMENT_ERROR(ex);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void HANDLE_FRAGMENT_ERROR(Exception ex)
    {
        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Error))
        {
            if (Security.ThrottledEventGate.TryAcquire(ref s_fragmentErrorTicks, ref s_fragmentErrorSuppressed, DateTime.UtcNow.Ticks, TimeSpan.TicksPerSecond * 5, out long suppressed))
            {
                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Error))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Error, new DiagnosticLog("NW.SocketConnection:Internal", $"fragment-error endpoint={_owner?.NetworkEndpoint.Address} suppressed-count={suppressed}", ex));
                }
                ;
            }
        }
    }

    #endregion Private: SAEA Receive Loop

    #region Private Methods

    [DebuggerStepThrough]
    private void DISPOSE(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (disposing)
        {
            // 1. Signal cancellation so the receive loop exits cleanly and stops
            //    scheduling any more receives.
            _shutdownRegistration.Dispose();
            this.CANCEL_RECEIVE_ONCE();

            // 2. Shutdown and close the socket. This forces any in-flight SAEA
            //    receive to complete or abort, which lets the pooled receive
            //    context observe an idle state and become returnable.
            if (Volatile.Read(ref _socketDetached) == 0)
            {
                try
                {
                    if (_socket.Connected)
                    {
                        _socket.Shutdown(SocketShutdown.Both);
                    }
                }
                catch (ObjectDisposedException ex)
                {
                    _ = ex.HResult;
#if DEBUG
                    if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Trace))
                    {
                        DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Trace, new DiagnosticLog("NW.SocketConnection:Internal", $"socket-shutdown-ignored disposed endpoint={_endpointString}", ex));
                    }
#endif
                }
                catch (SocketException ex) when (IS_BENIGN_DISCONNECT(ex))
                {
#if DEBUG
                    if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Trace))
                    {
                        System.Net.Sockets.SocketError socketError = ex.SocketErrorCode;
                        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Trace))
                        {
                            DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Trace, new DiagnosticLog("NW.SocketConnection:Internal", $"socket-shutdown-benign endpoint={_endpointString} socket-error={socketError}", ex));
                        }
                        ;
                    }
#endif
                }
                catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
                {
                    if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Warning))
                    {
                        DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Warning, new DiagnosticLog("NW.SocketConnection:Internal", $"socket-shutdown-failed endpoint={_endpointString}", ex));
                    }
                }

                try
                {
                    _socket.Close();
                }
                catch (ObjectDisposedException ex)
                {
                    _ = ex.HResult;
#if DEBUG
                    if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Trace))
                    {
                        DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Trace, new DiagnosticLog("NW.SocketConnection:Internal", $"socket-close-ignored disposed endpoint={_endpointString}", ex));
                    }
#endif
                }
                catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
                {
                    if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Warning))
                    {
                        DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Warning, new DiagnosticLog("NW.SocketConnection:Internal", $"socket-close-failed endpoint={_endpointString}", ex));
                    }
                }
            }

            Task? receiveLoopTask = Interlocked.Exchange(ref _receiveLoopTask, null);
            if (receiveLoopTask is not null)
            {
                this.OBSERVE_RECEIVE_LOOP_SHUTDOWN(receiveLoopTask);
            }

            // 3. Return the pooled receive context only after the socket can no
            //    longer use it.
            if (_recvCtx is not null)
            {
                // Always dispose/return context. PooledSocketReceiveContext.Dispose()
                // contains defensive wait logic to ensure kernel marks SAEA as idle.
                // Not returning it here caused the approx 524 object leak identified in stress tests.
                _recvCtx.Dispose();
                s_pool.Return(_recvCtx);

                _recvCtx = null!;
            }

            // 6. Fire the close callback after the socket and buffers are already
            //    out of circulation.
            this.INVOKE_CLOSE_ONCE();

            // 7. Dispose remaining resources.
            if (Volatile.Read(ref _socketDetached) == 0)
            {
                _socket.Dispose();
            }
            Interlocked.Exchange(ref _fragmentAssembler, null)?.Dispose();
        }

        // 4. Return the receive buffer. Interlocked.Exchange prevents double-
        //    return if Dispose races with the receive loop cleanup.
        //    IMPORTANT: We move this OUTSIDE the 'if (disposing)' block to ensure
        //    the pooled buffer is returned even if the connection object is leaked
        //    and GC'd without an explicit Dispose() call.
        byte[]? bufToReturn = Interlocked.Exchange(ref _buffer, null!);
        if (bufToReturn is not null)
        {
            BufferLease.ByteArrayPool.Return(bufToReturn);
        }

#if DEBUG
        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Trace))
        {
            DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Trace, new DiagnosticLog("NW.SocketConnection:Internal", $"disposed endpoint={_endpointString}"));
        }
#endif

        s_pool.Return(this);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IS_VALID_PACKET_SIZE(uint size) => size is >= HeaderSize;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void OBSERVE_RECEIVE_LOOP_SHUTDOWN(Task receiveLoopTask)
    {
        if (receiveLoopTask.IsCompleted)
        {
            if (receiveLoopTask.Exception?.GetBaseException() is Exception ex && !IS_BENIGN_DISCONNECT(ex))
            {
                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Warning))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Warning, new DiagnosticLog("NW.SocketConnection:Internal", $"receive-loop-faulted-during-dispose endpoint={_endpointString}", ex));
                }
            }
            return;
        }

        _ = receiveLoopTask.ContinueWith(static (task, state) =>
        {
            if (state is not SocketConnection self)
            {
                return;
            }

            Exception? ex = task.Exception?.GetBaseException();
            if (ex is not null && !IS_BENIGN_DISCONNECT(ex))
            {
                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Warning))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Warning, new DiagnosticLog("NW.SocketConnection:Internal", $"receive-loop-faulted-after-dispose endpoint={self._endpointString}", ex));
                }
            }
        }, this, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    [DebuggerStepThrough]
    private static bool IS_BENIGN_DISCONNECT(Exception ex)
    {
        if (ex is OperationCanceledException or ObjectDisposedException)
        {
            return true;
        }

        if (ex is NetworkException netEx && netEx.InnerException != null)
        {
            return IS_BENIGN_DISCONNECT(netEx.InnerException);
        }

        if (ex is SocketException se)
        {
            return se.SocketErrorCode
                is SocketError.ConnectionReset
                or SocketError.ConnectionAborted
                or SocketError.Shutdown
                or SocketError.OperationAborted;
        }

        if (ex is IOException ioex &&
            ioex.InnerException is SocketException ise)
        {
            return ise.SocketErrorCode
                is SocketError.ConnectionReset
                or SocketError.ConnectionAborted
                or SocketError.Shutdown
                or SocketError.OperationAborted;
        }

        if (ex is AggregateException agg)
        {
            agg = agg.Flatten();
            foreach (Exception inner in agg.InnerExceptions)
            {
                if (!IS_BENIGN_DISCONNECT(inner))
                {
                    return false;
                }
            }
            return agg.InnerExceptions.Count > 0;
        }

        return false;
    }

    [DebuggerStepThrough]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void INVOKE_CLOSE_ONCE()
    {
        if (Interlocked.Exchange(ref _closeSignaled, 1) != 0)
        {
            return;
        }

        _connectionOwner?.OnTransportClosed();
    }

    private void ABORT_RECEIVE_ON_SHUTDOWN()
    {
        if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _socketDetached) != 0)
        {
            return;
        }

        this.CANCEL_RECEIVE_ONCE();

        try
        {
            _socket.Shutdown(SocketShutdown.Both);
        }
        catch (ObjectDisposedException)
        {
            // Already torn down by a concurrent close.
        }
        catch (SocketException)
        {
            // Not connected any more; the receive loop is already exiting.
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CANCEL_RECEIVE_ONCE()
    {
        if (Interlocked.Exchange(ref _cancelSignaled, 1) != 0)
        {
            return;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private FragmentAssembler GET_OR_CREATE_FRAGMENT_ASSEMBLER()
    {
        FragmentAssembler? assembler = _fragmentAssembler;
        if (assembler is not null)
        {
            return assembler;
        }

        assembler = new FragmentAssembler();
        FragmentAssembler? existing = Interlocked.CompareExchange(ref _fragmentAssembler, assembler, null);
        if (existing is not null)
        {
            assembler.Dispose();
            return existing;
        }

        return assembler;
    }

#if DEBUG
    private static string FORMAT_FRAME_FOR_LOG(ReadOnlySpan<byte> payload, int maxBytes = 64)
    {
        if (payload.IsEmpty)
        {
            return "<empty>";
        }

        int show = payload.Length > maxBytes ? maxBytes : payload.Length;
        string hex = Convert.ToHexString(payload[..show]);
        if (payload.Length > show)
        {
            hex += "...";
        }

        return $"len={payload.Length} hex={hex}";
    }
#endif

    #endregion Private Methods
}
