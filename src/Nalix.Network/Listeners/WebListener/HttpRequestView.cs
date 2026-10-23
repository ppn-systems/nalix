// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers.Text;
using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using Nalix.Environment.Memory;

#pragma warning disable IDE0079
#pragma warning disable IDE0130

namespace Nalix.Network.Listeners.Web;

/// <summary>
/// Represents the supported HTTP/1.1 methods for minimal control-plane endpoints.
/// </summary>
public enum HttpMethodType : byte
{
    /// <summary>
    /// HTTP GET method.
    /// </summary>
    Get = 1,

    /// <summary>
    /// HTTP POST method.
    /// </summary>
    Post = 2,

    /// <summary>
    /// HTTP OPTIONS method (used for CORS preflight).
    /// </summary>
    Options = 3
}

/// <summary>
/// Zero-allocation read-only view over a single-buffer HTTP/1.1 request.
/// </summary>
[SkipLocalsInit]
public readonly ref struct HttpRequestView
{
    /// <summary>
    /// Gets the raw HTTP method bytes (e.g., <c>"GET"u8</c>, <c>"POST"u8</c>).
    /// </summary>
    public ReadOnlySpan<byte> Method { get; init; }

    /// <summary>
    /// Gets the request path without the query string (e.g., <c>"/metrics"u8</c>).
    /// </summary>
    public ReadOnlySpan<byte> Path { get; init; }

    /// <summary>
    /// Gets the raw query string without the leading <c>'?'</c>, or empty if none.
    /// </summary>
    public ReadOnlySpan<byte> Query { get; init; }

    /// <summary>
    /// Gets the request payload body bytes (for <c>POST</c> requests), or empty if none.
    /// </summary>
    public ReadOnlySpan<byte> Body { get; init; }

    /// <summary>
    /// Gets the <c>Content-Type</c> header value, or empty if absent.
    /// </summary>
    public ReadOnlySpan<byte> ContentType { get; init; }

    /// <summary>
    /// Gets the <c>Origin</c> header value, or empty if absent.
    /// </summary>
    public ReadOnlySpan<byte> Origin { get; init; }

    /// <summary>
    /// Gets the resolved client endpoint (accounting for Proxy Protocol or trusted forwarded headers).
    /// </summary>
    public EndPoint? RemoteEndPoint { get; init; }
}

/// <summary>
/// Zero-allocation pooled buffer writer for building an HTTP/1.1 response body and metadata.
/// </summary>
[SkipLocalsInit]
[DebuggerNonUserCode]
public ref struct HttpResponseWriter
{
    private const int DefaultInitialCapacity = 512;
    private const int MaxResponseBodyCapacity = 1_048_576; // 1 MB hard ceiling

    private byte[]? _buffer;
    private ReadOnlySpan<byte> _contentType;

    /// <summary>
    /// Initializes a new <see cref="HttpResponseWriter"/> with default status <c>200 OK</c>
    /// and <c>text/plain; charset=utf-8</c> content type.
    /// </summary>
    public HttpResponseWriter()
    {
        _buffer = null;
        _contentType = "text/plain; charset=utf-8"u8;
        this.StatusCode = 200;
        this.WrittenCount = 0;
    }

    /// <summary>
    /// Gets or sets the HTTP status code (default is <c>200</c>).
    /// </summary>
    public int StatusCode { get; set; }

    /// <summary>
    /// Gets or sets the <c>Content-Type</c> header bytes (default is <c>"text/plain; charset=utf-8"u8</c>).
    /// </summary>
    public ReadOnlySpan<byte> ContentType
    {
        readonly get => _contentType;
        set => _contentType = value;
    }

    /// <summary>
    /// Gets the number of body bytes written so far.
    /// </summary>
    public int WrittenCount { get; private set; }

    /// <summary>
    /// Gets the written response body as a read-only byte span.
    /// </summary>
    public readonly ReadOnlySpan<byte> WrittenSpan
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _buffer is null ? ReadOnlySpan<byte>.Empty : new ReadOnlySpan<byte>(_buffer, 0, this.WrittenCount);
    }

    /// <summary>
    /// Returns a writable <see cref="Span{T}"/> of at least <paramref name="sizeHint"/> bytes.
    /// </summary>
    /// <param name="sizeHint">The minimum number of bytes requested.</param>
    /// <returns>A writable byte span at the current write position.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<byte> GetSpan(int sizeHint = 0)
    {
        if (sizeHint <= 0)
        {
            sizeHint = 256;
        }

        this.EnsureCapacity(sizeHint);
        return new Span<byte>(_buffer!, this.WrittenCount, _buffer!.Length - this.WrittenCount);
    }

    /// <summary>
    /// Advances the write position by <paramref name="count"/> bytes after writing into <see cref="GetSpan"/>.
    /// </summary>
    /// <param name="count">The number of bytes written.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="count"/> is negative or exceeds capacity.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Advance(int count)
    {
        if (count < 0 || _buffer is null || (uint)(this.WrittenCount + count) > (uint)_buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        this.WrittenCount += count;
    }

    /// <summary>
    /// Appends raw UTF-8 or binary bytes to the response body.
    /// </summary>
    /// <param name="bytes">The bytes to write.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return;
        }

        Span<byte> dest = this.GetSpan(bytes.Length);
        bytes.CopyTo(dest);
        this.WrittenCount += bytes.Length;
    }

    /// <summary>
    /// Encodes and appends a character span as UTF-8 to the response body.
    /// </summary>
    /// <param name="text">The characters to encode as UTF-8.</param>
    public void WriteUtf8(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
        {
            return;
        }

        int maxBytes = Encoding.UTF8.GetMaxByteCount(text.Length);
        Span<byte> dest = this.GetSpan(maxBytes);
        int written = Encoding.UTF8.GetBytes(text, dest);
        this.WrittenCount += written;
    }

    /// <summary>
    /// Sets <see cref="ContentType"/> to <c>application/json; charset=utf-8</c> and writes the JSON payload.
    /// </summary>
    /// <param name="json">The JSON string or character span to write.</param>
    public void WriteJson(ReadOnlySpan<char> json)
    {
        _contentType = "application/json; charset=utf-8"u8;
        this.WriteUtf8(json);
    }

    /// <summary>
    /// Formats a 32-bit integer as UTF-8 digits into the response body without allocation.
    /// </summary>
    /// <param name="value">The integer value to format.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteInt32(int value)
    {
        Span<byte> dest = this.GetSpan(16);
        if (Utf8Formatter.TryFormat(value, dest, out int written))
        {
            this.WrittenCount += written;
        }
    }

    /// <summary>
    /// Formats a 64-bit integer as UTF-8 digits into the response body without allocation.
    /// </summary>
    /// <param name="value">The long integer value to format.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteInt64(long value)
    {
        Span<byte> dest = this.GetSpan(24);
        if (Utf8Formatter.TryFormat(value, dest, out int written))
        {
            this.WrittenCount += written;
        }
    }

    private void EnsureCapacity(int needed)
    {
        if (_buffer is null)
        {
            int initial = Math.Max(DefaultInitialCapacity, needed);
            if (initial > MaxResponseBodyCapacity)
            {
                throw new InvalidOperationException($"HTTP response body exceeds maximum capacity of {MaxResponseBodyCapacity} bytes.");
            }

            _buffer = BufferLease.ByteArrayPool.Rent(initial);
            return;
        }

        int remaining = _buffer.Length - this.WrittenCount;
        if (remaining >= needed)
        {
            return;
        }

        int newSize = Math.Max(_buffer.Length * 2, this.WrittenCount + needed);
        if (newSize > MaxResponseBodyCapacity)
        {
            throw new InvalidOperationException($"HTTP response body exceeds maximum capacity of {MaxResponseBodyCapacity} bytes.");
        }

        byte[] newBuf = BufferLease.ByteArrayPool.Rent(newSize);
        if (this.WrittenCount > 0)
        {
            new ReadOnlySpan<byte>(_buffer, 0, this.WrittenCount).CopyTo(newBuf);
        }

        BufferLease.ByteArrayPool.Return(_buffer);
        _buffer = newBuf;
    }

    /// <summary>
    /// Returns the rented response buffer to the pool.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Dispose()
    {
        if (_buffer is not null)
        {
            BufferLease.ByteArrayPool.Return(_buffer);
            _buffer = null;
        }

        this.WrittenCount = 0;
    }
}

/// <summary>
/// Delegate for handling a minimal single-buffer HTTP/1.1 request on a WebSocket listener port.
/// </summary>
/// <param name="request">The zero-allocation view over the incoming HTTP request.</param>
/// <param name="response">The zero-allocation writer for producing the HTTP response.</param>
public delegate void HttpRouteHandler(in HttpRequestView request, ref HttpResponseWriter response);
