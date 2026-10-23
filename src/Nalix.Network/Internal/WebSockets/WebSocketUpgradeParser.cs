// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers.Text;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

#pragma warning disable CA5350 // SHA1 is required by RFC 6455

namespace Nalix.Network.Internal.WebSockets;

/// <summary>
/// Zero-allocation parser for HTTP/1.1 WebSocket upgrade and minimal HTTP requests.
/// </summary>
internal static class WebSocketUpgradeParser
{
    private static ReadOnlySpan<byte> DoubleCrlf => "\r\n\r\n"u8;
    private static ReadOnlySpan<byte> Crlf => "\r\n"u8;

    // Header keys
    private static ReadOnlySpan<byte> HostKey => "host:"u8;
    private static ReadOnlySpan<byte> UpgradeKey => "upgrade:"u8;
    private static ReadOnlySpan<byte> ConnectionKey => "connection:"u8;
    private static ReadOnlySpan<byte> SecWebSocketVersionKey => "sec-websocket-version:"u8;
    private static ReadOnlySpan<byte> SecWebSocketKeyKey => "sec-websocket-key:"u8;
    private static ReadOnlySpan<byte> OriginKey => "origin:"u8;
    private static ReadOnlySpan<byte> SecWebSocketProtocolKey => "sec-websocket-protocol:"u8;
    private static ReadOnlySpan<byte> CfConnectingIpKey => "cf-connecting-ip:"u8;
    private static ReadOnlySpan<byte> XRealIpKey => "x-real-ip:"u8;
    private static ReadOnlySpan<byte> XForwardedForKey => "x-forwarded-for:"u8;
    private static ReadOnlySpan<byte> ContentLengthKey => "content-length:"u8;
    private static ReadOnlySpan<byte> ContentTypeKey => "content-type:"u8;
    private static ReadOnlySpan<byte> TransferEncodingKey => "transfer-encoding:"u8;

    // Header expected values
    private static ReadOnlySpan<byte> UpgradeWebSocketValue => "websocket"u8;
    private static ReadOnlySpan<byte> ConnectionUpgradeValue => "upgrade"u8;
    private static ReadOnlySpan<byte> Version13Value => "13"u8;
    private static ReadOnlySpan<byte> Http11Version => "HTTP/1.1"u8;
    private static ReadOnlySpan<byte> Http10Version => "HTTP/1.0"u8;
    private static ReadOnlySpan<byte> GetMethod => "GET"u8;
    private static ReadOnlySpan<byte> PostMethod => "POST"u8;

    // RFC 6455 magic string
    private static ReadOnlySpan<byte> MagicString => "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"u8;

    /// <summary>
    /// Parses an HTTP/1.1 request to extract WebSocket upgrade and minimal HTTP headers.
    /// </summary>
    /// <param name="buffer">The received bytes containing the HTTP request.</param>
    /// <returns>A parsed result indicating success or failure and the extracted values.</returns>
    public static WebSocketUpgradeResult Parse(ReadOnlySpan<byte> buffer)
    {
        int headerEnd = buffer.IndexOf(DoubleCrlf);
        if (headerEnd < 0)
        {
            return new WebSocketUpgradeResult { IsValid = false, IsHeaderComplete = false }; // Not complete yet
        }

        int totalBytesConsumed = headerEnd + 4; // Including \r\n\r\n
        ReadOnlySpan<byte> headersSpan = buffer[..headerEnd];

        // 1. Parse Request Line
        int firstLineEnd = headersSpan.IndexOf(Crlf);
        if (firstLineEnd <= 0)
        {
            return new WebSocketUpgradeResult { IsValid = false, IsHeaderComplete = true, IsMalformed = true, BytesConsumed = totalBytesConsumed };
        }

        ReadOnlySpan<byte> requestLine = headersSpan[..firstLineEnd];
        if (ContainsInvalidRequestLineChars(requestLine))
        {
            return new WebSocketUpgradeResult { IsValid = false, IsHeaderComplete = true, IsMalformed = true, BytesConsumed = totalBytesConsumed };
        }

        headersSpan = headersSpan[(firstLineEnd + 2)..];

        int methodEnd = requestLine.IndexOf((byte)' ');
        if (methodEnd <= 0)
        {
            return new WebSocketUpgradeResult { IsValid = false, IsHeaderComplete = true, IsMalformed = true, BytesConsumed = totalBytesConsumed };
        }

        ReadOnlySpan<byte> method = requestLine[..methodEnd];
        if (!IsValidHeaderKey(method))
        {
            return new WebSocketUpgradeResult { IsValid = false, IsHeaderComplete = true, IsMalformed = true, BytesConsumed = totalBytesConsumed };
        }

        ReadOnlySpan<byte> remainingRequestLine = requestLine[(methodEnd + 1)..];

        int pathEnd = remainingRequestLine.IndexOf((byte)' ');
        if (pathEnd <= 0)
        {
            return new WebSocketUpgradeResult { IsValid = false, IsHeaderComplete = true, IsMalformed = true, BytesConsumed = totalBytesConsumed };
        }

        ReadOnlySpan<byte> rawTarget = remainingRequestLine[..pathEnd];
        ReadOnlySpan<byte> httpVersion = remainingRequestLine[(pathEnd + 1)..];
        bool isHttp11 = httpVersion.SequenceEqual(Http11Version);
        bool isHttp10 = httpVersion.SequenceEqual(Http10Version);
        if (!isHttp11 && !isHttp10)
        {
            return new WebSocketUpgradeResult { IsValid = false, IsHeaderComplete = true, IsMalformed = true, BytesConsumed = totalBytesConsumed };
        }

        int querySeparator = rawTarget.IndexOf((byte)'?');
        ReadOnlySpan<byte> path = querySeparator >= 0 ? rawTarget[..querySeparator] : rawTarget;
        ReadOnlySpan<byte> query = querySeparator >= 0 ? rawTarget[(querySeparator + 1)..] : default;

        if (path.IsEmpty || path[0] != (byte)'/')
        {
            return new WebSocketUpgradeResult { IsValid = false, IsHeaderComplete = true, IsMalformed = true, BytesConsumed = totalBytesConsumed };
        }

        // 2. Parse Headers
        bool hasHost = false;
        bool hasUpgrade = false;
        bool hasConnection = false;
        bool hasVersion = false;
        bool hasSecWebSocketKey = false;
        bool hasOrigin = false;
        bool hasContentLength = false;
        bool hasContentType = false;
        bool hasCfConnectingIp = false;
        bool hasXRealIp = false;
        bool hasXForwardedFor = false;
        bool isMalformed = false;
        int contentLength = 0;
        ReadOnlySpan<byte> secWebSocketKey = default;
        ReadOnlySpan<byte> origin = default;
        ReadOnlySpan<byte> subProtocol = default;
        ReadOnlySpan<byte> contentType = default;
        ReadOnlySpan<byte> cfConnectingIp = default;
        ReadOnlySpan<byte> xRealIp = default;
        ReadOnlySpan<byte> xForwardedFor = default;

        Span<byte> lowerKeyBuffer = stackalloc byte[128]; // Max expected header key length

        while (headersSpan.Length > 0)
        {
            int lineEnd = headersSpan.IndexOf(Crlf);
            ReadOnlySpan<byte> line = lineEnd >= 0 ? headersSpan[..lineEnd] : headersSpan;

            int colonIndex = line.IndexOf((byte)':');
            if (colonIndex <= 0)
            {
                isMalformed = true;
                break;
            }

            // RFC 9112 §5.1 / §5.2: Reject whitespace before colon, obs-fold, or invalid token chars in field-name.
            ReadOnlySpan<byte> key = line[..colonIndex];
            ReadOnlySpan<byte> rawValue = line[(colonIndex + 1)..];
            if (key.Length > lowerKeyBuffer.Length || !IsValidHeaderKey(key) || ContainsInvalidHeaderValueChars(rawValue))
            {
                isMalformed = true;
                break;
            }

            ReadOnlySpan<byte> value = TrimSpace(rawValue);
            Span<byte> lowerKey = lowerKeyBuffer[..key.Length];
            ToLowerAscii(key, lowerKey);

            if (lowerKey.SequenceEqual(HostKey[..^1]))
            {
                if (hasHost || value.IsEmpty)
                {
                    isMalformed = true;
                    break;
                }
                hasHost = true;
            }
            else if (lowerKey.SequenceEqual(UpgradeKey[..^1])) // Exclude colon
            {
                if (ContainsIgnoreCase(value, UpgradeWebSocketValue))
                {
                    hasUpgrade = true;
                }
            }
            else if (lowerKey.SequenceEqual(ConnectionKey[..^1]))
            {
                if (ContainsIgnoreCase(value, ConnectionUpgradeValue))
                {
                    hasConnection = true;
                }
            }
            else if (lowerKey.SequenceEqual(SecWebSocketVersionKey[..^1]))
            {
                if (hasVersion || !value.SequenceEqual(Version13Value))
                {
                    isMalformed = true;
                    break;
                }
                hasVersion = true;
            }
            else if (lowerKey.SequenceEqual(SecWebSocketKeyKey[..^1]))
            {
                if (hasSecWebSocketKey || value.IsEmpty)
                {
                    isMalformed = true;
                    break;
                }
                hasSecWebSocketKey = true;
                secWebSocketKey = value;
            }
            else if (lowerKey.SequenceEqual(OriginKey[..^1]))
            {
                if (hasOrigin)
                {
                    isMalformed = true;
                    break;
                }
                hasOrigin = true;
                origin = value;
            }
            else if (lowerKey.SequenceEqual(SecWebSocketProtocolKey[..^1]))
            {
                subProtocol = value;
            }
            else if (lowerKey.SequenceEqual(CfConnectingIpKey[..^1]))
            {
                if (hasCfConnectingIp || value.IsEmpty)
                {
                    isMalformed = true;
                    break;
                }
                hasCfConnectingIp = true;
                cfConnectingIp = value;
            }
            else if (lowerKey.SequenceEqual(XRealIpKey[..^1]))
            {
                if (hasXRealIp || value.IsEmpty)
                {
                    isMalformed = true;
                    break;
                }
                hasXRealIp = true;
                xRealIp = value;
            }
            else if (lowerKey.SequenceEqual(XForwardedForKey[..^1]))
            {
                if (hasXForwardedFor || value.IsEmpty)
                {
                    isMalformed = true;
                    break;
                }
                hasXForwardedFor = true;
                xForwardedFor = value;
            }
            else if (lowerKey.SequenceEqual(ContentLengthKey[..^1]))
            {
                // RFC 9112 §6.3 & RFC 9110 §8.6: Reject duplicate Content-Length or non-DIGIT characters (e.g. +10, -0).
                if (hasContentLength ||
                    !IsAllAsciiDigits(value) ||
                    !Utf8Parser.TryParse(value, out int parsedLen, out int consumed) ||
                    consumed != value.Length ||
                    parsedLen < 0)
                {
                    isMalformed = true;
                    break;
                }

                hasContentLength = true;
                contentLength = parsedLen;
            }
            else if (lowerKey.SequenceEqual(ContentTypeKey[..^1]))
            {
                if (hasContentType)
                {
                    isMalformed = true;
                    break;
                }
                hasContentType = true;
                contentType = value;
            }
            else if (lowerKey.SequenceEqual(TransferEncodingKey[..^1]))
            {
                // Strict constraint: Transfer-Encoding (e.g. chunked) is never allowed.
                isMalformed = true;
                break;
            }

            if (lineEnd < 0)
            {
                break;
            }
            headersSpan = headersSpan[(lineEnd + 2)..];
        }

        // Reject non-zero request body on non-POST requests (GET/OPTIONS) or on WebSocket upgrade requests.
        if (contentLength > 0 && (!method.SequenceEqual(PostMethod) || hasUpgrade || hasSecWebSocketKey))
        {
            isMalformed = true;
        }

        // Validate all required fields are present for a WebSocket upgrade
        bool isValid = !isMalformed &&
                       isHttp11 &&
                       method.SequenceEqual(GetMethod) &&
                       contentLength == 0 &&
                       hasUpgrade &&
                       hasConnection &&
                       hasVersion &&
                       !secWebSocketKey.IsEmpty &&
                       secWebSocketKey.Length == 24;

        return new WebSocketUpgradeResult
        {
            IsValid = isValid,
            IsHeaderComplete = true,
            IsMalformed = isMalformed,
            ContentLength = contentLength,
            SecWebSocketKey = secWebSocketKey,
            Path = path,
            Query = query,
            Origin = origin,
            SubProtocol = subProtocol,
            HttpMethod = method,
            ContentType = contentType,
            CfConnectingIp = cfConnectingIp,
            XRealIp = xRealIp,
            XForwardedFor = xForwardedFor,
            BytesConsumed = totalBytesConsumed
        };
    }

    /// <summary>
    /// Computes the Sec-WebSocket-Accept key required for the handshake response.
    /// </summary>
    /// <param name="clientKey">The Sec-WebSocket-Key from the client request (must be 24 bytes).</param>
    /// <param name="destination">The destination span for the base64 encoded result.</param>
    /// <returns>The number of bytes written to the destination, or 0 if failed.</returns>
    public static int ComputeAcceptKey(ReadOnlySpan<byte> clientKey, Span<byte> destination)
    {
        if (clientKey.Length != 24)
        {
            return 0;
        }

        Span<byte> combined = stackalloc byte[60]; // 24 bytes key + 36 bytes magic string
        clientKey.CopyTo(combined);
        MagicString.CopyTo(combined[24..]);

        Span<byte> hash = stackalloc byte[20]; // SHA-1 is 20 bytes
        _ = SHA1.HashData(combined, hash);

        _ = Base64.EncodeToUtf8(hash, destination, out _, out int bytesWritten);
        return bytesWritten;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ToLowerAscii(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        for (int i = 0; i < source.Length; i++)
        {
            byte c = source[i];
            destination[i] = (c >= 'A' && c <= 'Z') ? (byte)(c + 32) : c;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ContainsIgnoreCase(ReadOnlySpan<byte> source, ReadOnlySpan<byte> target)
    {
        if (source.Length < target.Length)
        {
            return false;
        }

        int limit = source.Length - target.Length;
        for (int i = 0; i <= limit; i++)
        {
            bool match = true;
            for (int j = 0; j < target.Length; j++)
            {
                byte c = source[i + j];
                if (c is >= (byte)'A' and <= (byte)'Z')
                {
                    c = (byte)(c + 32);
                }

                if (c != target[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<byte> TrimSpace(ReadOnlySpan<byte> source)
    {
        int start = 0;
        while (start < source.Length && (source[start] == (byte)' ' || source[start] == (byte)'\t'))
        {
            start++;
        }

        int end = source.Length - 1;
        while (end >= start && (source[end] == (byte)' ' || source[end] == (byte)'\t'))
        {
            end--;
        }

        return source.Slice(start, end - start + 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ContainsInvalidRequestLineChars(ReadOnlySpan<byte> requestLine)
    {
        for (int i = 0; i < requestLine.Length; i++)
        {
            byte b = requestLine[i];
            if (b is < 0x20 or >= 0x7F)
            {
                return true;
            }
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsValidHeaderKey(ReadOnlySpan<byte> key)
    {
        if (key.IsEmpty)
        {
            return false;
        }

        for (int i = 0; i < key.Length; i++)
        {
            byte b = key[i];
            if (!IsHttpTokenChar(b))
            {
                return false;
            }
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsHttpTokenChar(byte b) =>
        b is (>= (byte)'a' and <= (byte)'z')
          or (>= (byte)'A' and <= (byte)'Z')
          or (>= (byte)'0' and <= (byte)'9')
          or (byte)'-' or (byte)'_' or (byte)'.'
          or (byte)'!' or (byte)'#' or (byte)'$'
          or (byte)'%' or (byte)'&' or (byte)'\''
          or (byte)'*' or (byte)'+' or (byte)'^'
          or (byte)'`' or (byte)'|' or (byte)'~';

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ContainsInvalidHeaderValueChars(ReadOnlySpan<byte> value)
    {
        for (int i = 0; i < value.Length; i++)
        {
            byte b = value[i];
            if ((b < 0x20 && b != (byte)'\t') || b == 0x7F)
            {
                return true;
            }
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsAllAsciiDigits(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
        {
            return false;
        }

        for (int i = 0; i < value.Length; i++)
        {
            if ((uint)(value[i] - (byte)'0') > 9)
            {
                return false;
            }
        }

        return true;
    }
}
