// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Runtime.CompilerServices;
using System.Text;
using Nalix.Network.Listeners.Web;

namespace Nalix.Network.Internal.WebSockets;

/// <summary>
/// Represents a single entry in the minimal HTTP route table.
/// </summary>
internal readonly struct HttpRouteEntry
{
    public readonly HttpMethodType Method;
    public readonly byte[] PathBytes;
    public readonly HttpRouteHandler? Handler;
    public readonly byte[]? StaticResponseBytes;

    public HttpRouteEntry(HttpMethodType method, byte[] pathBytes, HttpRouteHandler? handler, byte[]? staticResponseBytes)
    {
        Method = method;
        PathBytes = pathBytes;
        Handler = handler;
        StaticResponseBytes = staticResponseBytes;
    }
}

/// <summary>
/// Zero-allocation lookup table for minimal HTTP routes attached to a WebSocket listener.
/// </summary>
internal sealed class HttpRouteTable
{
    private HttpRouteEntry[] _entries = new HttpRouteEntry[12];
    private int _count;

    /// <summary>
    /// Gets the number of registered routes.
    /// </summary>
    public int Count => _count;

    /// <summary>
    /// Registers or overrides a dynamic HTTP route handler.
    /// </summary>
    public void Register(HttpMethodType method, string path, HttpRouteHandler handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(handler);

        byte[] pathBytes = NormalizePathToUtf8(path);
        this.Upsert(new HttpRouteEntry(method, pathBytes, handler, staticResponseBytes: null));
    }

    /// <summary>
    /// Registers or overrides a pre-encoded static HTTP response for a route.
    /// </summary>
    public void RegisterStatic(HttpMethodType method, string path, ReadOnlySpan<byte> fullHttpResponse)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        byte[] pathBytes = NormalizePathToUtf8(path);
        this.Upsert(new HttpRouteEntry(method, pathBytes, handler: null, staticResponseBytes: fullHttpResponse.ToArray()));
    }

    /// <summary>
    /// Attempts to match an incoming HTTP request against registered routes without allocating.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryMatch(
        ReadOnlySpan<byte> methodSpan,
        ReadOnlySpan<byte> pathSpan,
        out HttpRouteEntry matchedEntry,
        out bool isOptionsPreflight,
        out bool methodNotAllowed)
    {
        matchedEntry = default;
        isOptionsPreflight = false;
        methodNotAllowed = false;

        int count = _count;
        if (count == 0 || pathSpan.IsEmpty)
        {
            return false;
        }

        ReadOnlySpan<byte> normalizedPath = TrimTrailingSlash(pathSpan);
        bool pathFound = false;
        bool hasMethod = TryParseMethod(methodSpan, out HttpMethodType requestMethod);

        HttpRouteEntry[] entries = _entries;
        for (int i = 0; i < count; i++)
        {
            ref readonly HttpRouteEntry entry = ref entries[i];
            if (!EqualsAsciiIgnoreCase(normalizedPath, entry.PathBytes))
            {
                continue;
            }

            pathFound = true;

            if (hasMethod && requestMethod == HttpMethodType.Options)
            {
                isOptionsPreflight = true;
                return true;
            }

            if (hasMethod && entry.Method == requestMethod)
            {
                matchedEntry = entry;
                return true;
            }
        }

        if (pathFound)
        {
            methodNotAllowed = true;
            return true;
        }

        return false;
    }

    private void Upsert(in HttpRouteEntry newEntry)
    {
        for (int i = 0; i < _count; i++)
        {
            if (_entries[i].Method == newEntry.Method &&
                EqualsAsciiIgnoreCase(newEntry.PathBytes, _entries[i].PathBytes))
            {
                _entries[i] = newEntry;
                return;
            }
        }

        if (_count == _entries.Length)
        {
            Array.Resize(ref _entries, _entries.Length * 2);
        }

        _entries[_count++] = newEntry;
    }

    private static byte[] NormalizePathToUtf8(string path)
    {
        ReadOnlySpan<char> span = path.AsSpan().Trim();
        if (span.Length > 1 && span[^1] == '/')
        {
            span = span[..^1];
        }

        if (span.IsEmpty || span[0] != '/')
        {
            return Encoding.UTF8.GetBytes("/" + span.ToString());
        }

        return Encoding.UTF8.GetBytes(span.ToString());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<byte> TrimTrailingSlash(ReadOnlySpan<byte> path)
    {
        while (path.Length > 1 && path[^1] == (byte)'/')
        {
            path = path[..^1];
        }

        return path;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryParseMethod(ReadOnlySpan<byte> method, out HttpMethodType result)
    {
        if (method.SequenceEqual("GET"u8))
        {
            result = HttpMethodType.Get;
            return true;
        }

        if (method.SequenceEqual("POST"u8))
        {
            result = HttpMethodType.Post;
            return true;
        }

        if (method.SequenceEqual("OPTIONS"u8))
        {
            result = HttpMethodType.Options;
            return true;
        }

        result = default;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool EqualsAsciiIgnoreCase(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        for (int i = 0; i < a.Length; i++)
        {
            byte ca = a[i];
            byte cb = b[i];
            if (ca == cb)
            {
                continue;
            }

            if (ca >= 'A' && ca <= 'Z')
            {
                ca = (byte)(ca + 32);
            }

            if (cb >= 'A' && cb <= 'Z')
            {
                cb = (byte)(cb + 32);
            }

            if (ca != cb)
            {
                return false;
            }
        }

        return true;
    }
}
