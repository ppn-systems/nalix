// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers.Text;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using Nalix.Abstractions.Exceptions;
using Nalix.Environment.Memory;
using Nalix.Network.Internal.WebSockets;

#pragma warning disable IDE0079
#pragma warning disable CA1031

namespace Nalix.Network.Listeners.Web;

public abstract partial class WebSocketListenerBase
{
    private void REGISTER_DEFAULT_DEVOPS_ROUTES()
    {
        if (!_wsconfig.EnableDevOpsEndpoints)
        {
            return;
        }

        _httpRoutes.RegisterStatic(HttpMethodType.Get, "/healthz", HealthzResponse);
        _httpRoutes.RegisterStatic(HttpMethodType.Get, "/health", HealthzResponse);
        _httpRoutes.RegisterStatic(HttpMethodType.Get, "/livez", HealthzResponse);
        _httpRoutes.RegisterStatic(HttpMethodType.Get, "/readyz", HealthzResponse);
        _httpRoutes.RegisterStatic(HttpMethodType.Get, "/startupz", HealthzResponse);
        _httpRoutes.RegisterStatic(HttpMethodType.Get, "/ping", HealthzResponse);
        _httpRoutes.RegisterStatic(HttpMethodType.Get, "/version", _versionResponseBytes);
        _httpRoutes.Register(HttpMethodType.Get, "/metrics", this.HANDLE_DEFAULT_METRICS_ROUTE);
    }

    private void HANDLE_DEFAULT_METRICS_ROUTE(in HttpRequestView request, ref HttpResponseWriter response)
    {
        response.ContentType = "text/plain; version=0.0.4"u8;
        response.Write("# HELP nalix_active_connections Number of active connections\n# TYPE nalix_active_connections gauge\nnalix_active_connections "u8);
        response.WriteInt32(_hub.Count);
        response.Write("\n"u8);
    }

    /// <summary>
    /// DevOps and minimal HTTP endpoints (/healthz, /version, /metrics, ...) close the socket directly
    /// without ever creating a connection object, so the ConnectionClosed-based limiter
    /// release never fires. Release the accept-time slot here or it leaks permanently
    /// per request until the subnet/IP is banned forever.
    /// <para>
    /// Must release the same key that was used at accept time. Behind a PROXY-protocol
    /// front end (e.g. cloudflared), that key is the real client endpoint captured in
    /// <see cref="WebSocketUpgradeContext.RealEndPoint"/> -- NOT the physical socket's
    /// RemoteEndPoint, which is the proxy's own local address. Releasing the wrong key
    /// leaves the real client's slot permanently leaked.
    /// </para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RELEASE_LIMITER_SLOT(WebSocketUpgradeContext state)
    {
        if (System.Threading.Interlocked.Exchange(ref state.LimiterSlotReleased, 1) != 0)
        {
            return;
        }

        try
        {
            if (state.RealEndPoint is IPEndPoint realIp)
            {
                _limiter.Release(realIp);
            }
            else if (state.Socket?.RemoteEndPoint is IPEndPoint ip)
            {
                _limiter.Release(ip);
            }
        }
        catch { }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RELEASE_PHYSICAL_SLOT(Socket socket)
    {
        if (socket.RemoteEndPoint is IPEndPoint physicalIp)
        {
            _limiter.Release(physicalIp);
        }
    }

    private byte[] BUILD_VERSION_RESPONSE_BYTES()
    {
        string ver = _wsconfig.ServerVersion ?? "1.0.0";
        string body = $"{{\"name\":\"Nalix\",\"version\":\"{ver}\",\"status\":\"running\"}}";
        byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
        string header = $"HTTP/1.1 200 OK\r\nAccess-Control-Allow-Origin: *\r\nContent-Type: application/json\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n";
        byte[] headerBytes = Encoding.ASCII.GetBytes(header);

        byte[] full = new byte[headerBytes.Length + bodyBytes.Length];
        headerBytes.CopyTo(full, 0);
        bodyBytes.CopyTo(full, headerBytes.Length);
        return full;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SEND_STATIC_RESPONSE(WebSocketUpgradeContext state, SocketAsyncEventArgs args, ReadOnlySpan<byte> response)
    {
        try
        {
            _ = state.Socket?.Send(response, SocketFlags.None);
        }
        catch { }

        this.RELEASE_LIMITER_SLOT(state);

        try
        {
            state.Socket?.Shutdown(SocketShutdown.Send);
        }
        catch { }

        if (state.Socket != null)
        {
            SafeCloseSocket(state.Socket);
        }

        this.ReleaseWsUpgradeContext(state, args, success: true);
    }

    private void DISPATCH_HTTP_ROUTE(
        WebSocketUpgradeContext state,
        SocketAsyncEventArgs args,
        in WebSocketUpgradeResult result,
        in HttpRouteEntry entry)
    {
        if (entry.StaticResponseBytes is not null)
        {
            this.SEND_STATIC_RESPONSE(state, args, entry.StaticResponseBytes);
            return;
        }

        if (entry.Handler is null)
        {
            this.SEND_STATIC_RESPONSE(state, args, NotFoundResponse);
            return;
        }

        EndPoint? remoteEndPoint = state.RealEndPoint ?? state.Socket?.RemoteEndPoint;

        if (state.RealEndPoint is null &&
            state.Socket is not null &&
            this.TryResolveForwardedEndpoint(state.Socket, result, out IPEndPoint? forwardedEndpoint, out bool rejectForwarded))
        {
            if (rejectForwarded)
            {
                this.Metrics.RECORD_LIMITER_REJECTION();
                this.SEND_STATIC_RESPONSE(state, args, ForbiddenOriginResponse);
                return;
            }

            if (forwardedEndpoint is not null)
            {
                remoteEndPoint = forwardedEndpoint;
            }
        }

        ReadOnlySpan<byte> bodySpan = result.ContentLength > 0 && (long)result.BytesConsumed + result.ContentLength <= state.BytesReceived
            ? new ReadOnlySpan<byte>(state.Buffer, result.BytesConsumed, result.ContentLength)
            : ReadOnlySpan<byte>.Empty;

        HttpRequestView requestView = new()
        {
            Method = result.HttpMethod,
            Path = result.Path,
            Query = result.Query,
            Body = bodySpan,
            ContentType = result.ContentType,
            Origin = result.Origin,
            RemoteEndPoint = remoteEndPoint
        };

        HttpResponseWriter writer = new();
        try
        {
            entry.Handler(in requestView, ref writer);
            this.SEND_DYNAMIC_RESPONSE(state, args, ref writer);
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            this.Metrics.RECORD_ERROR();
            this.SEND_STATIC_RESPONSE(state, args, InternalServerErrorResponse);
        }
        finally
        {
            writer.Dispose();
        }
    }

    private void SEND_DYNAMIC_RESPONSE(WebSocketUpgradeContext state, SocketAsyncEventArgs args, ref HttpResponseWriter writer)
    {
        try
        {
            if (state.Socket != null)
            {
                ReadOnlySpan<byte> bodySpan = writer.WrittenSpan;
                ReadOnlySpan<byte> contentType = writer.ContentType.IsEmpty
                    ? "text/plain; charset=utf-8"u8
                    : writer.ContentType;

                Span<byte> headerBuffer = stackalloc byte[512];
                int headerWritten = FormatHttpResponseHeader(writer.StatusCode, contentType, bodySpan.Length, headerBuffer);
                ReadOnlySpan<byte> headerSpan = headerBuffer[..headerWritten];

                int totalLen = headerSpan.Length + bodySpan.Length;
                if (totalLen <= 1024)
                {
                    Span<byte> fullBuffer = stackalloc byte[1024];
                    headerSpan.CopyTo(fullBuffer);
                    bodySpan.CopyTo(fullBuffer[headerSpan.Length..]);
                    _ = state.Socket.Send(fullBuffer[..totalLen], SocketFlags.None);
                }
                else
                {
                    byte[] rented = BufferLease.ByteArrayPool.Rent(totalLen);
                    try
                    {
                        headerSpan.CopyTo(rented);
                        bodySpan.CopyTo(rented.AsSpan(headerSpan.Length));
                        _ = state.Socket.Send(new ReadOnlySpan<byte>(rented, 0, totalLen), SocketFlags.None);
                    }
                    finally
                    {
                        BufferLease.ByteArrayPool.Return(rented);
                    }
                }
            }
        }
        catch { }

        this.RELEASE_LIMITER_SLOT(state);

        try
        {
            state.Socket?.Shutdown(SocketShutdown.Send);
        }
        catch { }

        if (state.Socket != null)
        {
            SafeCloseSocket(state.Socket);
        }

        this.ReleaseWsUpgradeContext(state, args, success: true);
    }

    internal static int FormatHttpResponseHeader(int statusCode, ReadOnlySpan<byte> contentType, int contentLength, Span<byte> destination)
    {
        if (statusCode is < 100 or > 599)
        {
            statusCode = 500;
        }

        ReadOnlySpan<byte> statusLine = GetStatusLinePrefix(statusCode);
        int offset = 0;

        if (!statusLine.IsEmpty)
        {
            statusLine.CopyTo(destination);
            offset += statusLine.Length;
        }
        else
        {
            "HTTP/1.1 "u8.CopyTo(destination);
            offset += 9;
            if (Utf8Formatter.TryFormat(statusCode, destination[offset..], out int codeWritten))
            {
                offset += codeWritten;
            }
            " OK\r\n"u8.CopyTo(destination[offset..]);
            offset += 5;
        }

        ReadOnlySpan<byte> corsAndCtPrefix = "Access-Control-Allow-Origin: *\r\nContent-Type: "u8;
        corsAndCtPrefix.CopyTo(destination[offset..]);
        offset += corsAndCtPrefix.Length;

        int maxCtLen = Math.Min(contentType.Length, 128);
        int safeCtLen = 0;
        while (safeCtLen < maxCtLen)
        {
            byte b = contentType[safeCtLen];
            if ((b < 0x20 && b != (byte)'\t') || b == 0x7F)
            {
                break;
            }
            safeCtLen++;
        }

        if (safeCtLen > 0)
        {
            contentType[..safeCtLen].CopyTo(destination[offset..]);
            offset += safeCtLen;
        }
        else
        {
            ReadOnlySpan<byte> fallbackCt = "text/plain; charset=utf-8"u8;
            fallbackCt.CopyTo(destination[offset..]);
            offset += fallbackCt.Length;
        }

        ReadOnlySpan<byte> clPrefix = "\r\nContent-Length: "u8;
        clPrefix.CopyTo(destination[offset..]);
        offset += clPrefix.Length;

        if (Utf8Formatter.TryFormat(contentLength, destination[offset..], out int clWritten))
        {
            offset += clWritten;
        }

        ReadOnlySpan<byte> suffix = "\r\nConnection: close\r\n\r\n"u8;
        suffix.CopyTo(destination[offset..]);
        offset += suffix.Length;

        return offset;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<byte> GetStatusLinePrefix(int statusCode) => statusCode switch
    {
        200 => "HTTP/1.1 200 OK\r\n"u8,
        201 => "HTTP/1.1 201 Created\r\n"u8,
        202 => "HTTP/1.1 202 Accepted\r\n"u8,
        204 => "HTTP/1.1 204 No Content\r\n"u8,
        400 => "HTTP/1.1 400 Bad Request\r\n"u8,
        401 => "HTTP/1.1 401 Unauthorized\r\n"u8,
        403 => "HTTP/1.1 403 Forbidden\r\n"u8,
        404 => "HTTP/1.1 404 Not Found\r\n"u8,
        405 => "HTTP/1.1 405 Method Not Allowed\r\n"u8,
        409 => "HTTP/1.1 409 Conflict\r\n"u8,
        413 => "HTTP/1.1 413 Payload Too Large\r\n"u8,
        429 => "HTTP/1.1 429 Too Many Requests\r\n"u8,
        500 => "HTTP/1.1 500 Internal Server Error\r\n"u8,
        503 => "HTTP/1.1 503 Service Unavailable\r\n"u8,
        _ => default
    };
}
