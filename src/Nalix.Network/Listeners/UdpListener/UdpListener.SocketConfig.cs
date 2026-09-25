// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Nalix.Abstractions.Diagnostics;

namespace Nalix.Network.Listeners.Udp;

public abstract partial class UdpListenerBase
{
    /// <summary>
    /// Creates and binds the underlying <see cref="Socket"/> for UDP datagram reception.
    /// </summary>
    /// <remarks>
    /// Derived types can override this method to customize how the UDP socket is created or bound,
    /// but should preserve the contract that <see cref="_socket"/> is ready for receive operations
    /// when the method returns.
    /// </remarks>
    [DebuggerStepThrough]
    [MethodImpl(MethodImplOptions.NoInlining)]
    protected virtual void Initialize()
    {
        // Determine address family from configuration.
        IPAddress bindAddress = _options.EnableDualStack ? IPAddress.IPv6Any : IPAddress.Any;
        AddressFamily af = _options.EnableDualStack ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork;

        try
        {
            _socket = new Socket(af, SocketType.Dgram, ProtocolType.Udp);
        }
        catch (SocketException ex) when (af == AddressFamily.InterNetworkV6 &&
            ex.SocketErrorCode is SocketError.AddressFamilyNotSupported or SocketError.ProtocolNotSupported)
        {
            // Host has IPv6 disabled (e.g. ipv6.disable=1, minimal containers). Mirror the TCP
            // listener and fall back to IPv4-only instead of failing to bind at all.
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Warning))
            {
                DiagnosticsEvents.Write(
                    DiagnosticsEvents.Internal.Warning,
                    new DiagnosticLog("NW.UdpListenerBase:Initialize", $"ipv6-unavailable fallback=ipv4 port={_port}", ex));
            }

            af = AddressFamily.InterNetwork;
            bindAddress = IPAddress.Any;
            _socket = new Socket(af, SocketType.Dgram, ProtocolType.Udp);
        }

        bool actualDualMode = false;
        bool requestedDualMode = af == AddressFamily.InterNetworkV6 && _options.DualMode;

        // IPv6 dual-mode allows the socket to accept both IPv4 and IPv6 datagrams
        // on a single binding when the OS supports it.
        if (requestedDualMode)
        {
            try
            {
                _socket.DualMode = true;
                actualDualMode = _socket.DualMode;
            }
            catch (Exception ex) when (ex is SocketException or NotSupportedException or ObjectDisposedException or InvalidOperationException)
            {
                actualDualMode = false;

                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Warning))
                {
                    DiagnosticsEvents.Write(
                        DiagnosticsEvents.Internal.Warning,
                        new DiagnosticLog(
                            "NW.UdpListenerBase:Initialize",
                            $"dualmode-not-applied port={_port} exception-type={ex.GetType().Name}",
                            ex));
                }
            }
        }

        // Apply socket-level tuning before binding.
        this.ConfigureSocket(_socket);

        _socket.Bind(new IPEndPoint(bindAddress, _port));

        // Update the reusable endpoint to match the bound address family so that
        // ReceiveFromAsync can populate it without an address-family mismatch.
        _anyEndPoint = new IPEndPoint(bindAddress, 0);

#pragma warning disable IDE0072 // Add missing cases
        string mode = af switch
        {
            AddressFamily.InterNetwork => "ipv4-only",
            AddressFamily.InterNetworkV6 when actualDualMode => "dual-stack",
            AddressFamily.InterNetworkV6 => "ipv6-only",
            _ => "unknown"
        };
#pragma warning restore IDE0072 // Add missing cases

        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Information))
        {
            DiagnosticsEvents.Write(
                DiagnosticsEvents.Internal.Information,
                new DiagnosticLog(
                    "NW.UdpListenerBase:Initialize",
                    $"init-ok mode={mode} port={_port} " +
                    $"af={af} " +
                    $"dual-requested={requestedDualMode} " +
                    $"dual-actual={actualDualMode} " +
                    $"local-endpoint={_socket.LocalEndPoint} " +
                    $"options-reuse-address={_options.ReuseAddress} " +
                    $"options-buffer-size={_options.BufferSize}"));
        }
    }

    /// <summary>
    /// Applies UDP-relevant socket-level performance tuning to the given socket.
    /// </summary>
    /// <param name="socket">The socket to configure.</param>
    /// <remarks>
    /// Override this method when a derived listener needs a different tuning profile, such as
    /// platform-specific socket options or custom buffer sizing.
    /// <para>
    /// Note: TCP-specific options (<c>NoDelay</c>, <c>KeepAlive</c>) are intentionally excluded
    /// because they have no effect on UDP sockets.
    /// </para>
    /// </remarks>
    [DebuggerStepThrough]
    [MethodImpl(MethodImplOptions.NoInlining)]
    [SuppressMessage(
        "CodeQuality", "IDE0079:Remove unnecessary suppression", Justification = "<Pending>")]
    [SuppressMessage(
        "Interoperability", "CA1416:Validate platform compatibility", Justification = "<Pending>")]
    protected virtual void ConfigureSocket(Socket socket)
    {
        ArgumentNullException.ThrowIfNull(socket, nameof(socket));

        socket.Blocking = false;
        socket.ExclusiveAddressUse = !_options.ReuseAddress;
        socket.SendBufferSize = _options.BufferSize;
        socket.ReceiveBufferSize = _options.BufferSize;

        if (_options.ReuseAddress)
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        }

        // 1. GENERAL: Disabling packet fragmentation (IP Fragmentation).
        // Real-time Enterprise UDP Protocols always keep size < MTU (1400 bytes).
        // If a packet accidentally passes this threshold, it's better to drop the entire packet than to let the OS...
        // Fragmenting it into smaller packets increases latency spikes due to reassembly.
        try
        {
            socket.DontFragment = true;
        }
        catch (SocketException)
        {
            // Best effort only: some platforms/socket configurations do not support this option.
        }
        catch (ObjectDisposedException)
        {
            // Socket lifetime race: ignore to preserve existing non-fatal behavior.
        }

        // 2. WINDOWS: Fixing the classic WSAECONNRESET error of UDP on Windows.
        // Normally, if the server sends a UDP packet to the client's IP address, but the client is offline (power off),
        // Windows will receive an ICMP Port Unreachable error. Immediately, it throws a SocketException(ConnectionReset) error.
        // into the NEAREST ReceiveFromAsync function. This disrupts or interrupts the reception of many other users!
        // SIO_UDP_CONNRESET = -1744830452 disables the error reporting mechanism for this issue.
        if (OperatingSystem.IsWindows())
        {
            try
            {
                // DWORD 0 = false -> disable UDP connection reset
                const int SIO_UDP_CONNRESET = -1744830452;
                _ = socket.IOControl(SIO_UDP_CONNRESET, [0, 0, 0, 0], null);
            }
            catch (SocketException ex)
            {
                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Error)) { DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Error, new DiagnosticLog("NW.UdpListenerBase:ConfigureSocket", "Failed to set SIO_UDP_CONNRESET.", ex)); }
            }
        }
    }
}

