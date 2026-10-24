// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Nalix.Abstractions.Diagnostics;
using Nalix.Abstractions.Exceptions;
using Nalix.Environment.Memory;
using Nalix.Framework.Memory.Objects;
using Nalix.Network.Connections;
using Nalix.Network.Internal.Pooling;
using Nalix.Network.Internal.Tcp;
using Nalix.Network.Internal.WebSockets;

#pragma warning disable IDE0079
#pragma warning disable CA2213
#pragma warning disable CA1031
#pragma warning disable CA2000

namespace Nalix.Network.Listeners.Web;

public abstract partial class WebSocketListenerBase
{
    [DebuggerStepThrough]
    internal override AcceptResult ProcessAcceptedSocket(Socket socket, PooledAcceptContext context)
    {
        // For non-proxy connections, we intercept here
        if (!socket.Connected || socket.Handle.ToInt64() == -1)
        {
            SafeCloseSocket(socket);
            return new AcceptResult(AcceptConnectionResult.InvalidSocket, null);
        }

        if (socket.RemoteEndPoint is not IPEndPoint ip || !this.Limiter.TryAccept(ip))
        {
            this.Metrics.RECORD_LIMITER_REJECTION();
            SafeCloseSocket(socket);
            return new AcceptResult(AcceptConnectionResult.RejectedByLimiter, null);
        }

        if (!this.InitializeOptions(socket))
        {
            this.Limiter.Release(ip);
            SafeCloseSocket(socket);
            this.Metrics.RECORD_ERROR();
            return new AcceptResult(AcceptConnectionResult.Failed, null);
        }

        // Return context to pool since we don't need it for WS handshake
        ObjectPoolManager.Shared.Return(context);

        this.BeginWebSocketHandshake(socket, null, null, 0, 0);

        return new AcceptResult(AcceptConnectionResult.Pending, null);
    }

    [DebuggerStepThrough]
    internal override AcceptResult ProcessProxyAcceptedSocket(Socket socket, EndPoint? realEndPoint, int headerBytesConsumed, byte[]? receiveBuffer, int bytesReceived)
    {
        if (!this.InitializeOptions(socket))
        {
            try
            {
                if ((realEndPoint ?? socket.RemoteEndPoint) is IPEndPoint ip)
                {
                    _limiter.Release(ip);
                }
            }
            catch { }
            SafeCloseSocket(socket);
            this.Metrics.RECORD_ERROR();
            return new AcceptResult(AcceptConnectionResult.Failed, null);
        }

        int extraBytes = (receiveBuffer != null && bytesReceived > headerBytesConsumed)
            ? bytesReceived - headerBytesConsumed
            : 0;

        this.BeginWebSocketHandshake(socket, realEndPoint, receiveBuffer, headerBytesConsumed, extraBytes);

        return new AcceptResult(AcceptConnectionResult.Pending, null);
    }

    private void BeginWebSocketHandshake(Socket socket, EndPoint? realEndPoint, byte[]? proxyBuffer, int proxyOffset, int proxyBytesReceived)
    {
        WebSocketUpgradeContext state = ObjectPoolManager.Shared.Get<WebSocketUpgradeContext>();
        state.Socket = socket;
        state.RealEndPoint = realEndPoint;
        state.HandshakeStartTimeTicks = Stopwatch.GetTimestamp();

        if (proxyBuffer != null && proxyBytesReceived > 0)
        {
            state.Buffer = BufferLease.ByteArrayPool.Rent(Math.Max(_wsconfig.MaxUpgradeRequestSize, proxyBytesReceived));
            Buffer.BlockCopy(proxyBuffer, proxyOffset, state.Buffer, 0, proxyBytesReceived);
            state.BytesReceived = proxyBytesReceived;
        }
        else
        {
            state.Buffer = BufferLease.ByteArrayPool.Rent(_wsconfig.MaxUpgradeRequestSize);
            state.BytesReceived = 0;
        }

#pragma warning disable CA2000
        if (!_wsReceiveArgsPool.TryPop(out SocketAsyncEventArgs? args))
        {
            args = new SocketAsyncEventArgs();
            args.Completed += this.OnWebSocketReadCompleted;
        }
#pragma warning restore CA2000

        args.UserToken = state;

        lock (_wsUpgradeLock)
        {
            this.EnqueueWsUpgradeContext(state);
        }

        if (state.BytesReceived > 0)
        {
            this.ProcessWebSocketUpgradeBuffer(socket, state, args);
            return;
        }

        args.SetBuffer(state.Buffer, 0, state.Buffer.Length);

        bool pending = false;
        try
        {
            pending = socket.ReceiveAsync(args);
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            if (!this.TryDetachOwnedWsUpgradeContext(state, args))
            {
                return;
            }
            this.Metrics.RECORD_ERROR();
            this.ReleaseWsUpgradeContext(state, args, success: false);
            return;
        }

        if (!pending)
        {
            this.OnWebSocketReadCompleted(socket, args);
        }
    }

    internal void OnWebSocketReadCompleted(object? sender, SocketAsyncEventArgs args)
    {
        WebSocketUpgradeContext state = (WebSocketUpgradeContext)args.UserToken!;

        if (args.SocketError != SocketError.Success || args.BytesTransferred == 0)
        {
            if (!this.TryDetachOwnedWsUpgradeContext(state, args))
            {
                return;
            }
            this.Metrics.RECORD_ERROR();
            this.ReleaseWsUpgradeContext(state, args, success: false);
            return;
        }

        state.BytesReceived += args.BytesTransferred;
        this.ProcessWebSocketUpgradeBuffer(sender, state, args);
    }

    private void ProcessWebSocketUpgradeBuffer(object? sender, WebSocketUpgradeContext state, SocketAsyncEventArgs args)
    {
        WebSocketUpgradeResult result = WebSocketUpgradeParser.Parse(new ReadOnlySpan<byte>(state.Buffer, 0, state.BytesReceived));

        if (result.IsMalformed)
        {
            if (!this.TryDetachOwnedWsUpgradeContext(state, args))
            {
                return;
            }
            this.Metrics.RECORD_ERROR();
            this.SEND_STATIC_RESPONSE(state, args, BadRequestResponse);
            return;
        }

        long totalRequiredBytes = (long)result.BytesConsumed + result.ContentLength;
        int maxAllowedBytes = Math.Min(state.Buffer.Length, _wsconfig.MaxUpgradeRequestSize);

        if (result.IsHeaderComplete && (result.ContentLength < 0 || totalRequiredBytes > maxAllowedBytes))
        {
            if (!this.TryDetachOwnedWsUpgradeContext(state, args))
            {
                return;
            }
            this.Metrics.RECORD_ERROR();
            this.SEND_STATIC_RESPONSE(state, args, PayloadTooLargeResponse);
            return;
        }

        if (!result.IsHeaderComplete || state.BytesReceived < totalRequiredBytes)
        {
            // If incomplete, check if we've exceeded the max request size
            if (state.BytesReceived >= maxAllowedBytes)
            {
                if (!this.TryDetachOwnedWsUpgradeContext(state, args))
                {
                    return;
                }
                this.Metrics.RECORD_ERROR();
                this.ReleaseWsUpgradeContext(state, args, success: false);
                return;
            }

            // Incomplete, read more into the same buffer
            args.SetBuffer(state.BytesReceived, state.Buffer.Length - state.BytesReceived);
            try
            {
                if (!state.Socket!.ReceiveAsync(args))
                {
                    this.OnWebSocketReadCompleted(sender, args);
                }
            }
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
            {
                if (!this.TryDetachOwnedWsUpgradeContext(state, args))
                {
                    return;
                }
                this.Metrics.RECORD_ERROR();
                this.ReleaseWsUpgradeContext(state, args, success: false);
            }
            return;
        }

        if (_httpRoutes.TryMatch(
            result.HttpMethod,
            result.Path,
            out HttpRouteEntry matchedRoute,
            out bool isOptionsPreflight,
            out bool methodNotAllowed))
        {
            if (!this.TryDetachOwnedWsUpgradeContext(state, args))
            {
                return;
            }

            if (isOptionsPreflight)
            {
                this.SEND_STATIC_RESPONSE(state, args, CorsPreflightResponse);
            }
            else if (methodNotAllowed)
            {
                this.SEND_STATIC_RESPONSE(state, args, MethodNotAllowedResponse);
            }
            else
            {
                this.DISPATCH_HTTP_ROUTE(state, args, in result, in matchedRoute);
            }
            return;
        }

        if (!result.IsValid)
        {
            if (!this.TryDetachOwnedWsUpgradeContext(state, args))
            {
                return;
            }
            this.Metrics.RECORD_ERROR();
            this.SEND_STATIC_RESPONSE(state, args, NotFoundResponse);
            return;
        }

        // Handshake parsed successfully!
        if (!this.TryDetachOwnedWsUpgradeContext(state, args))
        {
            return;
        }

        // Validate path
        string requestPath = Encoding.UTF8.GetString(result.Path);
        if (!IsPathMatch(requestPath, _path))
        {
            this.Metrics.RECORD_ERROR();
            this.ReleaseWsUpgradeContext(state, args, success: false);
            return;
        }

        string? origin = result.Origin.IsEmpty ? null : Encoding.UTF8.GetString(result.Origin);
        if (!_wsconfig.IsOriginAllowed(origin))
        {
            // CSWSH protection: reject before the 101 upgrade so no connection/session is created.
            this.Metrics.RECORD_ERROR();
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Warning))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Warning, new DiagnosticLog(
                    "NW.ws:origin", $"ws-origin-rejected port={_config.Port} origin={(origin is null ? "<missing>" : SanitizeForLog(origin))} remote={state.Socket?.RemoteEndPoint}"));
            }

            this.SEND_STATIC_RESPONSE(state, args, ForbiddenOriginResponse);
            return;
        }

        // Compute Sec-WebSocket-Accept
        Span<byte> acceptKey = stackalloc byte[28]; // Base64(SHA1) = 28 bytes
        int acceptKeyLen = WebSocketUpgradeParser.ComputeAcceptKey(result.SecWebSocketKey, acceptKey);

        if (acceptKeyLen == 0)
        {
            this.Metrics.RECORD_ERROR();
            this.ReleaseWsUpgradeContext(state, args, success: false);
            return;
        }

        // Send 101 Switching Protocols response
        try
        {
            string configuredSubProtocol = _wsconfig.SubProtocol;
            bool hasConfiguredSubProtocol = !string.IsNullOrWhiteSpace(configuredSubProtocol);
            bool hasSubProtocol = hasConfiguredSubProtocol && ContainsRequestedSubProtocol(result.SubProtocol, configuredSubProtocol);

            if (hasConfiguredSubProtocol && !result.SubProtocol.IsEmpty && !hasSubProtocol)
            {
                this.Metrics.RECORD_ERROR();
                this.ReleaseWsUpgradeContext(state, args, success: false);
                return;
            }

            // Capture socket and resolve forwarded endpoint BEFORE sending 101 Switching Protocols
            Socket socket = state.Socket!;
            EndPoint realEndPoint = state.RealEndPoint ?? socket.RemoteEndPoint!;

            if (state.RealEndPoint is null && this.TryResolveForwardedEndpoint(socket, result, out IPEndPoint? forwardedEndpoint, out bool rejectForwarded))
            {
                if (rejectForwarded)
                {
                    this.Metrics.RECORD_LIMITER_REJECTION();
                    this.ReleaseWsUpgradeContext(state, args, success: false);
                    return;
                }

                if (forwardedEndpoint is not null && socket.RemoteEndPoint is IPEndPoint)
                {
                    if (!_limiter.TryAccept(forwardedEndpoint))
                    {
                        this.Metrics.RECORD_LIMITER_REJECTION();
                        this.ReleaseWsUpgradeContext(state, args, success: false);
                        return;
                    }

                    this.RELEASE_PHYSICAL_SLOT(socket);
                    state.RealEndPoint = forwardedEndpoint;
                    realEndPoint = forwardedEndpoint;
                }
            }

            Span<byte> responseBuffer = stackalloc byte[256];
            int offset = 0;

            HandshakeResponsePrefix.CopyTo(responseBuffer[offset..]);
            offset += HandshakeResponsePrefix.Length;

            acceptKey.CopyTo(responseBuffer[offset..]);
            offset += acceptKeyLen;

            if (hasSubProtocol)
            {
                HandshakeSubProtocolPrefix.CopyTo(responseBuffer[offset..]);
                offset += HandshakeSubProtocolPrefix.Length;

                int bytesWritten = Encoding.UTF8.GetBytes(configuredSubProtocol, responseBuffer[offset..]);
                offset += bytesWritten;
            }

            HandshakeResponseSuffix.CopyTo(responseBuffer[offset..]);
            offset += HandshakeResponseSuffix.Length;

            int responseLength = offset;

            // ponytail: fixed 1s timeout guards against a slow/stalled peer blocking this
            // IOCP completion thread indefinitely; make configurable if load demands it.
            socket.SendTimeout = 1000;
            int sent = socket.Send(responseBuffer[..responseLength], SocketFlags.None);

            if (sent != responseLength)
            {
                this.Metrics.RECORD_ERROR();
                this.ReleaseWsUpgradeContext(state, args, success: false);
                return;
            }

            // Create a NetworkStream and wrap it in a WebSocket (ownsSocket: true so closing WS closes TCP socket immediately)
            NetworkStream stream = new(socket, ownsSocket: true);

            WebSocket webSocket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions
            {
                IsServer = true,
                SubProtocol = hasSubProtocol ? configuredSubProtocol : null,
                KeepAliveInterval = TimeSpan.FromSeconds(_wsconfig.KeepAliveIntervalSeconds)
            });

            WebSocketConnection? connection = new(webSocket, _protocol.OpCodeExtractor, realEndPoint, _timing);
            try
            {
                connection.ConnectionClosed += this.HandleConnectionClose;
                connection.ConnectionClosed += _limiter.OnConnectionClosed;
                _ = System.Threading.Interlocked.Exchange(ref state.LimiterSlotReleased, 1);
                connection.MessageProcessed += _protocol.PostProcessMessage;
                connection.MessageProcessing += _protocol.FrameProcessor.ProcessFrame;

                if (_config.EnableTimeout)
                {
                    _timing.Register(connection);
                }

                // Dispatch
                this.DISPATCH_CONNECTION(connection);
                connection = null;

                // Successfully dispatched, release context (keeps socket open)
                this.ReleaseWsUpgradeContext(state, args, success: true);
            }
            finally
            {
                connection?.Dispose();
            }

            return;
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            this.Metrics.RECORD_ERROR();
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Trace))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Trace, new DiagnosticLog("NW.WebSocketListenerBase:Handle", "handshake-send-error", ex));
            }
        }

        this.ReleaseWsUpgradeContext(state, args, success: false);
    }

    /// <summary>
    /// Case-insensitive match of the request path against the configured path: exact, or the
    /// configured path followed by <c>/</c> or <c>?</c> (any prefix when it already ends with <c>/</c>).
    /// </summary>
    private static bool IsPathMatch(string requestPath, string path)
    {
        if (!requestPath.StartsWith(path, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (requestPath.Length == path.Length || path.EndsWith('/'))
        {
            return true;
        }

        char next = requestPath[path.Length];
        return next is '/' or '?';
    }

    private static bool ContainsRequestedSubProtocol(ReadOnlySpan<byte> requested, string configured)
    {
        ReadOnlySpan<char> expected = configured.AsSpan().Trim();
        int start = 0;

        while (start < requested.Length)
        {
            int comma = requested[start..].IndexOf((byte)',');
            int end = comma < 0 ? requested.Length : start + comma;
            ReadOnlySpan<byte> token = TrimAsciiSpace(requested[start..end]);

            if (MatchesAscii(token, expected))
            {
                return true;
            }

            if (comma < 0)
            {
                break;
            }

            start = end + 1;
        }

        return false;
    }

    private static ReadOnlySpan<byte> TrimAsciiSpace(ReadOnlySpan<byte> value)
    {
        int start = 0;
        while (start < value.Length && value[start] <= 32)
        {
            start++;
        }

        int end = value.Length - 1;
        while (end >= start && value[end] <= 32)
        {
            end--;
        }

        return value[start..(end + 1)];
    }

    private static bool MatchesAscii(ReadOnlySpan<byte> value, ReadOnlySpan<char> expected)
    {
        if (value.Length != expected.Length)
        {
            return false;
        }

        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] != expected[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Detaches <paramref name="state"/> under <c>_wsUpgradeLock</c> on a completion/error path.
    /// Returns <see langword="false"/> when the handshake sweep or <c>CLEANUP_WS_UPGRADES</c>
    /// detached it first: that side then owns the context (socket close, limiter slot), so the
    /// caller must not touch the context again. The caller still owns <paramref name="args"/>
    /// (the sweep never touches it), so it is returned to the receive-args pool here.
    /// </summary>
    private bool TryDetachOwnedWsUpgradeContext(WebSocketUpgradeContext state, SocketAsyncEventArgs args)
    {
        lock (_wsUpgradeLock)
        {
            if (this.DetachWsUpgradeContext(state))
            {
                return true;
            }
        }

        args.UserToken = null;
        _wsReceiveArgsPool.Push(args);
        return false;
    }

    /// <summary>
    /// Unlinks <paramref name="state"/> from the pending-upgrade list. Must be called under
    /// <c>_wsUpgradeLock</c>. Returns <see langword="true"/> only if this call removed it, which
    /// makes the caller the single owner of the context.
    /// </summary>
    private bool DetachWsUpgradeContext(WebSocketUpgradeContext state)
    {
        if (state.RemovedFromList)
        {
            return false;
        }

        state.RemovedFromList = true;

        if (state.Prev != null)
        {
            state.Prev.Next = state.Next;
        }
        else if (_wsUpgradeHead == state)
        {
            _wsUpgradeHead = state.Next;
        }

        if (state.Next != null)
        {
            state.Next.Prev = state.Prev;
        }
        else if (_wsUpgradeTail == state)
        {
            _wsUpgradeTail = state.Prev;
        }

        state.Next = null;
        state.Prev = null;
        return true;
    }

    internal void EnqueueWsUpgradeContext(WebSocketUpgradeContext state)
    {
        state.Next = null;
        state.Prev = _wsUpgradeTail;

        if (_wsUpgradeTail != null)
        {
            _wsUpgradeTail.Next = state;
        }
        else
        {
            _wsUpgradeHead = state;
        }

        _wsUpgradeTail = state;
    }

    private void ReleaseWsUpgradeContext(WebSocketUpgradeContext state, SocketAsyncEventArgs args, bool success)
    {
        if (!success)
        {
            this.RELEASE_LIMITER_SLOT(state);
        }

        if (state.Buffer is { Length: > 0 } buf)
        {
            BufferLease.ByteArrayPool.Return(buf);
            state.Buffer = [];
        }

        args.UserToken = null;
        _wsReceiveArgsPool.Push(args);

        if (!success && state.Socket != null)
        {
            SafeCloseSocket(state.Socket);
        }

        state.ResetForPool();
        ObjectPoolManager.Shared.Return(state);
    }

    private void SWEEP_WS_HANDSHAKE_TIMEOUTS()
        => this.CloseChainOutsideLock(this.DETACH_TIMED_OUT_WS_UPGRADES(Stopwatch.GetTimestamp()));

    /// <summary>
    /// Detaches every context whose handshake started more than the configured timeout before
    /// <paramref name="now"/> and returns them as a local chain linked via <c>.Next</c>.
    /// </summary>
    internal WebSocketUpgradeContext? DETACH_TIMED_OUT_WS_UPGRADES(long now)
    {

        // Detach timed-out contexts into a local chain (reusing their own .Next pointer --
        // zero extra allocation) while holding the lock (fast: pointer-chasing only), then
        // close the sockets AFTER releasing it. Socket.Close() can block (TCP teardown,
        // linger, OS scheduling); calling it while holding _wsUpgradeLock would stall every
        // concurrent handshake/healthz/ping request queued on that same lock in
        // BeginWebSocketHandshake / OnWebSocketReadCompleted for as long as the sweep takes
        // to close all timed-out sockets.
        WebSocketUpgradeContext? toClose = null;

        lock (_wsUpgradeLock)
        {
            WebSocketUpgradeContext? current = _wsUpgradeHead;
            while (current != null)
            {
                WebSocketUpgradeContext? next = current.Next;

                if (now - current.HandshakeStartTimeTicks <= _handshakeTimeoutTicks)
                {
                    break;
                }

                _ = this.DetachWsUpgradeContext(current);

                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Trace))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Trace,
                        new DiagnosticLog("NW.WebSocketListenerBase:Sweep", $"ws-handshake-timeout remote-endpoint={current.Socket?.RemoteEndPoint}"));
                }

                current.Next = toClose;
                toClose = current;

                current = next;
            }
        }

        return toClose;
    }

    internal void CLEANUP_WS_UPGRADES()
    {
        WebSocketUpgradeContext? toClose = null;

        // Detach each context (marking RemovedFromList) so a racing completion path sees
        // that cleanup owns it and leaves it alone.
        lock (_wsUpgradeLock)
        {
            while (_wsUpgradeHead is { } current)
            {
                _ = this.DetachWsUpgradeContext(current);
                current.Next = toClose;
                toClose = current;
            }
        }

        this.CloseChainOutsideLock(toClose);
    }

    /// <summary>
    /// Closes every socket in a locally-detached chain of <see cref="WebSocketUpgradeContext"/>
    /// (linked via <c>.Next</c>, reused as scratch space post-detach) without holding
    /// <c>_wsUpgradeLock</c>. Must be called after the lock has been released.
    /// </summary>
    /// <remarks>
    /// Each context here already consumed a ConnectionGuard accept-time slot (via
    /// <c>Limiter.TryAccept</c> in ProcessAcceptedSocket/handshake-completion) that is
    /// never released, because a stalled/timed-out handshake never reaches the point where
    /// OnConnectionClosed gets wired up. Release it here, same as RELEASE_LIMITER_SLOT does for
    /// the devops static-response close paths -- otherwise every swept/timed-out handshake leaks
    /// its slot permanently.
    /// </remarks>
    internal void CloseChainOutsideLock(WebSocketUpgradeContext? head)
    {
        WebSocketUpgradeContext? current = head;
        while (current != null)
        {
            WebSocketUpgradeContext? next = current.Next;
            current.Next = null;

            this.RELEASE_LIMITER_SLOT(current);

            if (current.Socket != null)
            {
                SafeCloseSocket(current.Socket);
            }

            current = next;
        }
    }

    private static string SanitizeForLog(string value)
    {
        const int MaxLen = 128;
        ReadOnlySpan<char> span = value.AsSpan();
        if (span.Length > MaxLen)
        {
            span = span[..MaxLen];
        }

        return string.Create(span.Length, value, static (dst, src) =>
        {
            for (int i = 0; i < dst.Length; i++)
            {
                char c = src[i];
                dst[i] = char.IsControl(c) ? '?' : c;
            }
        });
    }
}
