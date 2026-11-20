// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Nalix.Abstractions.Exceptions;
using Nalix.Network.Internal.Pooling;

namespace Nalix.Network.Internal;

/// <summary>
/// Provides cached, zero-allocation exception instances for Abstractions transport errors.
/// These instances avoid the overhead of stack trace generation by overriding the StackTrace property.
/// </summary>
internal static class Throw
{
    #region Cached Exceptions (private)

    private static readonly SocketException s_shutdown = new CachedSocketException((int)SocketError.Shutdown);
    private static readonly SocketException s_messageSize = new CachedSocketException((int)SocketError.MessageSize);
    private static readonly SocketException s_operationAborted = new CachedSocketException((int)SocketError.OperationAborted);
    private static readonly SocketException s_connectionAborted = new CachedSocketException((int)SocketError.ConnectionAborted);
    private static readonly SocketException s_connectionResetInternal = new CachedSocketException((int)SocketError.ConnectionReset);
    private static readonly SocketException s_protocolNotSupported = new CachedSocketException((int)SocketError.ProtocolNotSupported);

    private static readonly NetworkException s_connectionReset = new CachedNetworkException("Connection closed by peer.", s_connectionResetInternal);
    private static readonly NetworkException s_sendFailed = new CachedNetworkException("The socket closed while sending.");
    private static readonly NetworkException s_udpPayloadTooLarge = new CachedNetworkException("UDP payload too large. Use TCP for large data.");
    private static readonly NetworkException s_udpPartialSend = new CachedNetworkException("UDP partial send occurred.");
    private static readonly NetworkException s_udpSendFailed = new CachedNetworkException("UDP transmission failed.");
    private static readonly NetworkException s_processChannelFull = new CachedNetworkException("Process channel is full.");
    private static readonly NetworkException s_invalidSocket = new CachedNetworkException("Invalid socket.");
    private static readonly NetworkException s_connectionRejectedByLimiter = new CachedNetworkException("Connection rejected by limiter.");

    private static readonly ObjectDisposedException s_pooledContextDisposed = new CachedObjectDisposedException(nameof(PooledSocketReceiveContext));
    private static readonly InternalErrorException s_argsNotBound = new CachedInternalErrorException("Args not bound.");

    private static readonly InternalErrorException s_tryAcceptContextNotBound = new CachedInternalErrorException("TryAccept context was not bound to pooled socket args.");
    private static readonly InternalErrorException s_failedToAcquirePooledArgs = new CachedInternalErrorException("Failed to acquire PooledSocketAsyncEventArgs.");
    private static readonly InternalErrorException s_connectionNotAvailable = new CachedInternalErrorException("Connection is not available for this event.");
    private static readonly InternalErrorException s_udpTransportNotCreated = new CachedInternalErrorException("UDP transport has not been created yet.");
    private static readonly InternalErrorException s_eventArgsMustHaveLease = new CachedInternalErrorException("Event args must have Lease.");
    private static readonly InvalidOperationException s_webSocketClosed = new CachedInvalidOperationException("WebSocket is closed.");
    private static readonly InvalidOperationException s_webSocketInvalidReceiveBuffer = new CachedInvalidOperationException("WebSocket receive buffer size must be greater than zero.");
    private static readonly InvalidOperationException s_webSocketMessageTooLarge = new CachedInvalidOperationException("WebSocket message size exceeds configured maximum.");
    private static readonly NotSupportedException s_webSocketSyncSendNotSupported = new CachedNotSupportedException("Synchronous send is not supported for WebSocket transport. Use SendAsync instead.");

    #endregion Cached Exceptions (private)

    #region Getters (return exception, do not throw)

    /// <summary>
    /// Returns a cached <see cref="SocketException"/> for the given error code if available, 
    /// otherwise returns a new instance.
    /// </summary>
    [SuppressMessage("Style", "IDE0072:Add missing cases", Justification = "<Pending>")]
    public static SocketException GetSocketError(SocketError error) => error switch
    {
        SocketError.Shutdown => s_shutdown,
        SocketError.MessageSize => s_messageSize,
        SocketError.OperationAborted => s_operationAborted,
        SocketError.ConnectionAborted => s_connectionAborted,
        SocketError.ConnectionReset => s_connectionResetInternal,
        SocketError.ProtocolNotSupported => s_protocolNotSupported,
        _ => new SocketException((int)error)
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static NetworkException GetConnectionReset() => s_connectionReset;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static NetworkException GetSendFailed() => s_sendFailed;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static NetworkException GetMessageSize() => s_messageSize is SocketException se
        ? new CachedNetworkException("Message size exceeded.", se)
        : s_sendFailed;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ObjectDisposedException GetPooledContextDisposed() => s_pooledContextDisposed;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static InvalidOperationException GetWebSocketClosed() => s_webSocketClosed;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static InvalidOperationException GetWebSocketMessageTooLarge() => s_webSocketMessageTooLarge;

    #endregion Getters (return exception, do not throw)

    #region Throw Helpers

    [DoesNotReturn]
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ConnectionResetNow() => throw s_connectionReset;

    [DoesNotReturn]
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void SendFailedNow() => throw s_sendFailed;

    [DoesNotReturn]
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void UdpPayloadTooLargeNow() => throw s_udpPayloadTooLarge;

    [DoesNotReturn]
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void UdpPartialSendNow() => throw s_udpPartialSend;

    [DoesNotReturn]
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void UdpSendFailedNow() => throw s_udpSendFailed;

    [DoesNotReturn]
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ProtocolNotSupportedNow() => throw s_protocolNotSupported;

    [StackTraceHidden]
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ProcessChannelFull() => throw s_processChannelFull;

    [StackTraceHidden]
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void InvalidSocket() => throw s_invalidSocket;

    [StackTraceHidden]
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ConnectionRejectedByLimiter() => throw s_connectionRejectedByLimiter;

    [StackTraceHidden]
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ArgsNotBound() => throw s_argsNotBound;

    [StackTraceHidden]
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void EventArgsMustHaveLease() => throw s_eventArgsMustHaveLease;

    [StackTraceHidden]
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void TryAcceptContextNotBound() => throw s_tryAcceptContextNotBound;

    [StackTraceHidden]
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void FailedToAcquirePooledArgs() => throw s_failedToAcquirePooledArgs;

    [StackTraceHidden]
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ConnectionNotAvailable() => throw s_connectionNotAvailable;

    [StackTraceHidden]
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void UdpTransportNotCreated() => throw s_udpTransportNotCreated;

    [StackTraceHidden]
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void WebSocketClosed() => throw s_webSocketClosed;

    [StackTraceHidden]
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void WebSocketInvalidReceiveBuffer() => throw s_webSocketInvalidReceiveBuffer;

    [StackTraceHidden]
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void WebSocketSyncSendNotSupported() => throw s_webSocketSyncSendNotSupported;

    #endregion Throw Helpers

    #region Private Cached Exception Types

    private sealed class CachedObjectDisposedException(string objectName) : ObjectDisposedException(objectName)
    {
        public override string? StackTrace => "   at Nalix.Network.Internal.Transport (Cached Exception)";
    }

    private sealed class CachedNetworkException : NetworkException
    {
        public CachedNetworkException(string message) : base(message) { }
        public CachedNetworkException(string message, Exception inner) : base(message, inner) { }

        // Override StackTrace to return a static string, avoiding the expensive scan.
        public override string? StackTrace => "   at Nalix.Network.Internal.Transport (Cached Exception)";
    }

    private sealed class CachedSocketException(int errorCode) : SocketException(errorCode)
    {
        public override string? StackTrace => "   at Nalix.Network.Internal.Transport (Cached Exception)";
    }

    private sealed class CachedInternalErrorException(string message) : InternalErrorException(message)
    {
        public override string? StackTrace => "   at Nalix.Network.Internal.Transport (Cached Exception)";
    }

    private sealed class CachedInvalidOperationException(string message) : InvalidOperationException(message)
    {
        public override string? StackTrace => "   at Nalix.Network.Internal.Transport (Cached Exception)";
    }

    private sealed class CachedNotSupportedException(string message) : NotSupportedException(message)
    {
        public override string? StackTrace => "   at Nalix.Network.Internal.Transport (Cached Exception)";
    }

    #endregion Private Cached Exception Types
}
