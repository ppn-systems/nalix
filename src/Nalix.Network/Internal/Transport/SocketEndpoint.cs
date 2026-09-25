// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Contracts;
using System.Net;
using System.Runtime.CompilerServices;
using Nalix.Abstractions.Networking;
using Nalix.Environment.Hashing;

#if DEBUG
[assembly: InternalsVisibleTo("Nalix.Network.Tests")]
[assembly: InternalsVisibleTo("Nalix.Network.Benchmarks")]
#endif

namespace Nalix.Network.Internal.Transport;

[SkipLocalsInit]
[DebuggerNonUserCode]
[ExcludeFromCodeCoverage]
[DebuggerDisplay("{ToString()}")]
internal readonly struct SocketEndpoint : INetworkEndpoint, IEquatable<SocketEndpoint>
{
    public static SocketEndpoint Empty { get; } = new(0, 0, 0, false, false);

    private readonly ulong _hi;
    private readonly ulong _lo;
    private readonly int _port;

    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static SocketEndpoint FromIpAddress(IPAddress ip)
    {
        NormalizeAddress(ip, out ulong hi, out ulong lo, out bool isV6);
        return new SocketEndpoint(hi, lo, 0, isV6, hasPort: false);
    }

    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static SocketEndpoint FromEndPoint(EndPoint? endpoint)
    {
        if (endpoint is not IPEndPoint ipEndPoint)
        {
            throw ThrowInvalidEndpointType();
        }

        NormalizeAddress(ipEndPoint.Address, out ulong hi, out ulong lo, out bool isV6);
        return new SocketEndpoint(hi, lo, ipEndPoint.Port, isV6, hasPort: true);
    }

    /// <summary>
    /// Converts an <see cref="INetworkEndpoint"/> instance to a concrete <see cref="SocketEndpoint"/>.
    /// </summary>
    /// <param name="endpoint">The network endpoint instance.</param>
    /// <returns>A concrete <see cref="SocketEndpoint"/> representation.</returns>
    /// <remarks>
    /// <b>Explicit Contract</b>: This method expects <see cref="SocketEndpoint"/>-backed endpoints for maximum performance.
    /// It will safely unbox using pattern matching. If a non-SocketEndpoint is passed (e.g., in unit testing mocks),
    /// it falls back to parsing the IP address representation without throwing exceptions.
    /// </remarks>
    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SocketEndpoint FromNetworkEndpoint(INetworkEndpoint? endpoint)
    {
        if (endpoint is null)
        {
            return Empty;
        }

        if (endpoint is SocketEndpoint socketEndpoint)
        {
            return socketEndpoint;
        }

        if (IPAddress.TryParse(endpoint.Address, out IPAddress? ip))
        {
            return FromIpAddress(ip);
        }

        return Empty;
    }

    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void NormalizeAddress(IPAddress ip, out ulong hi, out ulong lo, out bool isV6)
    {
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        Span<byte> buf = stackalloc byte[16];
        if (!ip.TryWriteBytes(buf, out int written))
        {
            byte[] tmp = ip.GetAddressBytes();
            MemoryExtensions.CopyTo(tmp, buf);
            written = tmp.Length;
        }

        if (written == 4)
        {
            uint v4 = BinaryPrimitives.ReadUInt32BigEndian(buf[..4]);
            hi = 0UL;
            lo = v4;
            isV6 = false;
        }
        else
        {
            hi = BinaryPrimitives.ReadUInt64BigEndian(buf[..8]);
            lo = BinaryPrimitives.ReadUInt64BigEndian(buf.Slice(8, 8));
            isV6 = true;
        }
    }

    private SocketEndpoint(ulong hi, ulong lo, int port, bool isV6, bool hasPort)
    {
        _hi = hi;
        _lo = lo;
        _port = port;

        this.IsIPv6 = isV6;
        this.HasPort = hasPort;
    }

    public string Address
    {
        [Pure]
        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
        get
        {
            if (!this.IsIPv6)
            {
                uint v4 = (uint)_lo;
                byte[] bytes = new byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(bytes, v4);
                return new IPAddress(bytes).ToString();
            }

            Span<byte> buf = stackalloc byte[16];
            BinaryPrimitives.WriteUInt64BigEndian(buf, _hi);
            BinaryPrimitives.WriteUInt64BigEndian(buf[8..], _lo);

            return new IPAddress(buf).ToString();
        }
    }

    /// <summary>
    /// Formats the IP address directly into a character span without heap allocation.
    /// Max output length: 15 for IPv4 ("ddd.ddd.ddd.ddd"), 45 for IPv6.
    /// </summary>
    /// <param name="destination">Buffer to write into. Use at least 45 chars for safe sizing.</param>
    /// <param name="charsWritten">Number of characters written on success.</param>
    /// <returns>True if the address was formatted; false if the destination was too small.</returns>
    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public bool TryFormatAddress(Span<char> destination, out int charsWritten)
    {
        if (!this.IsIPv6)
        {
            return this.TryFormatIPv4(destination, out charsWritten);
        }

        return this.TryFormatIPv6(destination, out charsWritten);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private bool TryFormatIPv4(Span<char> destination, out int charsWritten)
    {
        // Maximum IPv4 string: "255.255.255.255" = 15 chars
        if (destination.Length < 15)
        {
            charsWritten = 0;
            return false;
        }

        uint v4 = (uint)_lo;
        byte b0 = (byte)(v4 >> 24);
        byte b1 = (byte)(v4 >> 16);
        byte b2 = (byte)(v4 >> 8);
        byte b3 = (byte)v4;

        int pos = 0;
        pos += WriteByteToChars(destination[pos..], b0);
        destination[pos++] = '.';
        pos += WriteByteToChars(destination[pos..], b1);
        destination[pos++] = '.';
        pos += WriteByteToChars(destination[pos..], b2);
        destination[pos++] = '.';
        pos += WriteByteToChars(destination[pos..], b3);

        charsWritten = pos;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int WriteByteToChars(Span<char> dest, byte value)
    {
        if (value >= 100)
        {
            dest[0] = (char)('0' + (value / 100));
            int tens = value / 10 % 10;
            dest[1] = (char)('0' + tens);
            dest[2] = (char)('0' + (value % 10));
            return 3;
        }

        if (value >= 10)
        {
            dest[0] = (char)('0' + (value / 10));
            dest[1] = (char)('0' + (value % 10));
            return 2;
        }

        dest[0] = (char)('0' + value);
        return 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private bool TryFormatIPv6(Span<char> destination, out int charsWritten)
    {
        // Maximum IPv6 string: "ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff" = 39 chars
        if (destination.Length < 39)
        {
            charsWritten = 0;
            return false;
        }

        Span<byte> addr = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64BigEndian(addr, _hi);
        BinaryPrimitives.WriteUInt64BigEndian(addr[8..], _lo);

        int pos = 0;
        for (int i = 0; i < 8; i++)
        {
            if (i > 0)
            {
                destination[pos++] = ':';
            }

            ushort group = (ushort)((addr[i * 2] << 8) | addr[(i * 2) + 1]);
            pos += WriteHexGroupToChars(destination[pos..], group);
        }

        charsWritten = pos;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int WriteHexGroupToChars(Span<char> dest, ushort value)
    {
        // Write 1-4 hex digits
        if (value == 0)
        {
            dest[0] = '0';
            return 1;
        }

        int digits = value <= 0xF ? 1 : value <= 0xFF ? 2 : value <= 0xFFF ? 3 : 4;
        for (int i = digits - 1; i >= 0; i--)
        {
            int nibble = value & 0xF;
            dest[i] = (char)(nibble < 10 ? '0' + nibble : 'a' + nibble - 10);
            value >>= 4;
        }
        return digits;
    }

    /// <summary>
    /// Extracts the subnet key directly from the raw address bytes without allocating.
    /// IPv4: /24 subnet key (top 24 bits). IPv6: /48 subnet key (top 48 bits).
    /// </summary>
    /// <param name="ipv4Subnet">The /24 subnet key for IPv4 endpoints.</param>
    /// <param name="ipv6Subnet">The /48 subnet key for IPv6 endpoints.</param>
    /// <param name="isIPv6">True if this is an IPv6 endpoint.</param>
    /// <returns>True if a valid non-empty address exists; false for Empty.</returns>
    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetSubnetKey(out uint ipv4Subnet, out long ipv6Subnet, out bool isIPv6)
    {
        if (this.Equals(Empty))
        {
            ipv4Subnet = 0;
            ipv6Subnet = 0;
            isIPv6 = false;
            return false;
        }

        isIPv6 = this.IsIPv6;
        if (!isIPv6)
        {
            // Extract /24 subnet: top 24 bits of the IPv4 address stored in _lo
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)_lo);
            ipv4Subnet = (uint)((bytes[0] << 16) | (bytes[1] << 8) | bytes[2]);
            ipv6Subnet = 0;
        }
        else
        {
            ipv4Subnet = 0;
            // Extract /48 subnet: top 48 bits from _hi
            Span<byte> bytes = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(bytes, _hi);
            long key = 0;
            for (int i = 0; i < 6; i++)
            {
                key = (key << 8) | bytes[i];
            }
            ipv6Subnet = key;
        }

        return true;
    }

    /// <summary>
    /// Creates a SocketEndpoint from raw IP address bytes and port without allocating IPAddress/IPEndPoint.
    /// </summary>
    /// <param name="addressBytes">4 bytes for IPv4, 16 bytes for IPv6.</param>
    /// <param name="port">The port number.</param>
    /// <param name="isIPv6">True if the address is IPv6.</param>
    /// <returns>A new SocketEndpoint.</returns>
    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static SocketEndpoint FromRawBytes(ReadOnlySpan<byte> addressBytes, int port, bool isIPv6)
    {
        if (!isIPv6)
        {
            uint v4 = BinaryPrimitives.ReadUInt32BigEndian(addressBytes[..4]);
            return new SocketEndpoint(0, v4, port, isV6: false, hasPort: true);
        }
        else
        {
            ulong hi = BinaryPrimitives.ReadUInt64BigEndian(addressBytes[..8]);
            ulong lo = BinaryPrimitives.ReadUInt64BigEndian(addressBytes.Slice(8, 8));
            return new SocketEndpoint(hi, lo, port, isV6: true, hasPort: true);
        }
    }

    /// <summary>
    /// Writes the raw address bytes to a destination span.
    /// </summary>
    /// <param name="destination">Must be at least 4 bytes for IPv4, 16 for IPv6.</param>
    /// <param name="bytesWritten">Number of bytes written (4 or 16).</param>
    /// <returns>True on success, false if destination is too small.</returns>
    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryWriteAddressBytes(Span<byte> destination, out int bytesWritten)
    {
        if (!this.IsIPv6)
        {
            if (destination.Length < 4) { bytesWritten = 0; return false; }
            BinaryPrimitives.WriteUInt32BigEndian(destination, (uint)_lo);
            bytesWritten = 4;
            return true;
        }
        else
        {
            if (destination.Length < 16) { bytesWritten = 0; return false; }
            BinaryPrimitives.WriteUInt64BigEndian(destination, _hi);
            BinaryPrimitives.WriteUInt64BigEndian(destination[8..], _lo);
            bytesWritten = 16;
            return true;
        }
    }

    /// <summary>
    /// Allocates an IPAddress from the stored raw bytes. Used only when an IPAddress
    /// is required for interop (e.g., NetworkAccessList checks).
    /// </summary>
    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal IPAddress ToIPAddress()
    {
        if (!this.IsIPv6)
        {
            Span<byte> buf = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(buf, (uint)_lo);
            return new IPAddress(buf);
        }
        else
        {
            Span<byte> buf = stackalloc byte[16];
            BinaryPrimitives.WriteUInt64BigEndian(buf, _hi);
            BinaryPrimitives.WriteUInt64BigEndian(buf[8..], _lo);
            return new IPAddress(buf);
        }
    }

    /// <summary>
    /// Gets a value indicating whether the address is a loopback address
    /// (<c>127.0.0.0/8</c>, <c>::1</c>, or an IPv4-mapped <c>::ffff:127.x.x.x</c>).
    /// </summary>
    public bool IsLoopback
    {
        [Pure]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => !this.IsIPv6
            ? ((uint)_lo >> 24) == 127u
            : _hi == 0UL && (_lo == 1UL || ((_lo >> 32) == 0xFFFFUL && (((uint)_lo) >> 24) == 127u));
    }

    public int Port
    {
        [Pure]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => this.HasPort ? _port : 0;
    }

    public bool HasPort
    {
        [Pure]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get;
    }

    public bool IsIPv6
    {
        [Pure]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get;
    }

    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(SocketEndpoint other)
    {
        // Compare by IP address only — port is intentionally excluded so that
        // the per-IP fairness counters in AsyncCallback track by IP, not by
        // IP:port (SEC-13). Two endpoints with the same IP but different source
        // ports must hash/equal identically for rate-limiting purposes.
        return _hi == other._hi &&
               _lo == other._lo &&
               this.IsIPv6 == other.IsIPv6;
    }

    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(INetworkEndpoint? other)
    {
        if (other is null)
        {
            return false;
        }

        return other is SocketEndpoint concrete
            ? this.Equals(concrete)
            : string.Equals(this.Address, other.Address, StringComparison.Ordinal);
    }

    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override bool Equals(object? obj) => obj is SocketEndpoint k && this.Equals(k);

    // Hash by IP only — port is excluded to match the IP-only Equals semantics.
    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetHashCode() => XxHash32.Compute(_hi, _lo, isIPv6: this.IsIPv6);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator ==(SocketEndpoint left, SocketEndpoint right) => left.Equals(right);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator !=(SocketEndpoint left, SocketEndpoint right) => !left.Equals(right);

    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override string ToString()
    {
        string addr = this.Address;
        if (!this.HasPort)
        {
            return addr;
        }

        return !this.IsIPv6 ? $"{addr}:{_port}" : $"[{addr}]:{_port}";
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Exception ThrowInvalidEndpointType() => throw new ArgumentException("Endpoint must be of type IPEndPoint.", "endpoint");
}
