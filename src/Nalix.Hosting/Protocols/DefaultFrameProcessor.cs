// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Nalix.Abstractions;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Security;
using Nalix.Codec.Transforms;
using Nalix.Environment.Configuration;
using Nalix.Environment.Options;
using Nalix.Hosting.Internal;
using Nalix.Hosting.Internal.Exceptions;
using Nalix.Network.Connections;

namespace Nalix.Hosting.Protocols;

/// <summary>
/// Provides the default implementation for processing inbound network frames.
/// </summary>
/// <remarks>
/// This processor applies the configured inbound frame pipeline,
/// validates transport sequence numbers, replaces the connection buffer
/// when transforms produce a new lease, and forwards the resulting message
/// to the associated protocol.
/// </remarks>
public sealed class DefaultFrameProcessor : IFrameProcessor
{
    #region Fields

    private static readonly AttributeKey s_throttleAttributeKey = AttributeKey.FromName("sys.log.protocol.process_error");
    private static readonly long s_throttleWindowTicks = (long)(TimeSpan.FromSeconds(20).TotalSeconds * Stopwatch.Frequency);

    private readonly ILogger _logger;
    private readonly IProtocol _protocol;
    private readonly SequenceOptions _sequenceOptions;

    #endregion Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="DefaultFrameProcessor"/> class.
    /// </summary>
    /// <param name="logger">
    /// The logger used for recording frame processing events.
    /// </param>
    /// <param name="protocol">
    /// The protocol that will receive successfully processed messages.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="protocol"/> or <paramref name="logger"/> is <see langword="null"/>.
    /// </exception>
    public DefaultFrameProcessor(ILogger logger, IProtocol protocol)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(protocol);

        _logger = logger;
        _protocol = protocol;
        _sequenceOptions = ConfigurationManager.Instance.Get<SequenceOptions>();
    }

    #endregion Constructors

    #region Properties

    /// <inheritdoc />
    /// <remarks>
    /// <see langword="true"/> when the owning protocol is <see cref="DefaultProtocol"/> backed by the
    /// queued <see cref="Runtime.Dispatching.PacketDispatchChannel"/>: frame processing then only
    /// decrypts/decompresses and enqueues, and handlers run on dispatch workers.
    /// </remarks>
    public bool SupportsInlineProcessing => _protocol is DefaultProtocol { UsesQueuedDispatch: true };

    #endregion Properties

    #region Methods

    /// <summary>
    /// Processes an incoming network frame from a connected client.
    /// Applies inbound pipeline transformations (e.g., decrypt, decompress),
    /// optionally replaces the underlying buffer lease, then forwards the
    /// processed message to the protocol layer for handling.
    /// </summary>
    /// <param name="sender">The source of the event triggering this frame processing.</param>
    /// <param name="args">Connection event arguments containing the frame data and connection context.</param>
    /// <remarks>
    /// This method is performance-critical and is intentionally marked with <see cref="DebuggerStepThroughAttribute"/>
    /// to avoid stepping into during debugging sessions.
    ///
    /// Pipeline behavior:
    /// <list type="number">
    /// <item>Validates and extracts the buffer lease from event args.</item>
    /// <item>Applies inbound transformations via <c>FramePipeline.ProcessInbound</c>.</item>
    /// <item>Replaces the lease if pipeline produces a new buffer.</item>
    /// <item>Forwards the event to protocol handler.</item>
    /// </list>
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="args"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when lease is missing from event args.</exception>
    /// <exception cref="CipherException">May occur during cryptographic processing.</exception>
    /// <exception cref="InvalidCastException">May occur during frame decoding.</exception>
    /// <exception cref="SerializationFailureException">Thrown when deserialization fails.</exception>
    /// <exception cref="Exception">Unhandled exceptions are logged and reported to connection error handler.</exception>
    [DebuggerStepThrough]
    public void ProcessFrame(object? sender, IConnectionEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args is not ConnectionEventArgs replaceable)
        {
            return;
        }

        if (args.Lease is not { } lease)
        {
            Throw.EventArgsMustHaveLease();
            return;
        }

        uint window;
        uint? seq = null;
        bool exchanged = false;
        ISequenceCounter counter;
        IBufferLease current = lease;

        if (current.IsReliable)
        {
            window = _sequenceOptions.TcpWindow;
            counter = args.Connection.TCP.ReceiveSequence;

        }
        else
        {
            if (args.Connection.UDP is not { } udp)
            {
                _logger.DiscardUnreliableFrame();
                return;
            }

            window = _sequenceOptions.UdpWindow;
            counter = udp.ReceiveSequence;
        }

        try
        {
            if (!FramePipeline.TryProcessInbound(ref current, args.Connection.Secret.AsSpan(), args.Connection.Algorithm, out seq))
            {
                args.Connection.IncrementErrorCount();

                _logger.DroppedInboundPacket();
                return;
            }

            if (!counter.IsValid(seq, window: window))
            {
                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    _logger.RejectedSequence(current.IsReliable ? "TCP" : "UDP", seq, counter.Current());
                }
                return;
            }

            if (!ReferenceEquals(current, lease))
            {
                replaceable.ExchangeLease(current)?.Dispose();
                lease = current;
                exchanged = true;
            }

            _protocol.ProcessMessage(sender, args);
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            args.Connection.IncrementErrorCount();

            if (ex is InternalErrorException or SerializationFailureException)
            {
                _logger.ProcessExceptionTrace(ex);
            }
            else if (ShouldEmitThrottledLog(args.Connection))
            {
                _logger.ProcessExceptionError(ex);
            }
        }
        finally
        {
            if (seq.HasValue)
            {
                counter.UpdateTo(seq.Value);
            }

            if (!exchanged && !ReferenceEquals(current, lease))
            {
                current.Dispose();
            }
        }
    }

    #endregion Methods

    #region Throttle Helper

    private static bool ShouldEmitThrottledLog(IConnection connection)
    {
        if (connection is null || connection.IsDisposed)
        {
            return false;
        }

        IObjectMap<AttributeKey, object>? attrs = connection.Attributes;
        if (attrs is null)
        {
            return true;
        }

        long nowTicks = Stopwatch.GetTimestamp();

        if (!attrs.TryGetValue(s_throttleAttributeKey, out object? val) || val is not long lastTicks)
        {
            attrs[s_throttleAttributeKey] = nowTicks;
            return true;
        }

        if (nowTicks - lastTicks < s_throttleWindowTicks)
        {
            return false;
        }

        attrs[s_throttleAttributeKey] = nowTicks;
        return true;
    }

    #endregion Throttle Helper
}
