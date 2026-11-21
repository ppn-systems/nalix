// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Net;
using System.Net.Sockets;
using Nalix.Abstractions.Diagnostics;

namespace Nalix.Network.Listeners.Datagram;

public sealed partial class DatagramListener
{
    /// <summary>
    /// Creates and binds the UDP socket: dual-stack when <see cref="Options.NetworkSocketOptions.EnableDualStack"/> is set and the
    /// host supports IPv6, IPv4 otherwise.
    /// </summary>
    private Socket CreateSocket()
    {
        Socket socket = this.CreateUnboundSocket();
        try
        {
            // Never SO_REUSEADDR here, whatever NetworkSocketOptions.ReuseAddress says (it exists for TCP TIME_WAIT):
            // on a UDP socket it lets another socket bind the same port and take a share of its datagrams.
            if (OperatingSystem.IsWindows())
            {
                socket.ExclusiveAddressUse = true;
            }

            socket.ReceiveBufferSize = _options.SocketReceiveBufferSize;
            socket.SendBufferSize = _options.SocketSendBufferSize;
            TrySetDontFragment(socket);

            IPAddress any = socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any;
            socket.Bind(new IPEndPoint(any, _port));
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Debug))
        {
            DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Debug,
                new DiagnosticLog("NW.DatagramListener:CreateSocket",
                    $"bound local-endpoint={socket.LocalEndPoint} address-family={socket.AddressFamily} " +
                    $"rcvbuf={socket.ReceiveBufferSize} sndbuf={socket.SendBufferSize}"));
        }

        return socket;
    }

    private Socket CreateUnboundSocket()
    {
        if (_socketOptions.EnableDualStack && Socket.OSSupportsIPv6)
        {
            try
            {
                return new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp) { DualMode = _socketOptions.DualMode };
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressFamilyNotSupported or SocketError.ProtocolNotSupported)
            {
                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Warning))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Warning,
                        new DiagnosticLog("NW.DatagramListener:CreateSocket", $"ipv6-unavailable fallback=ipv4 port={_port}", ex));
                }
            }
        }

        return new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    }

    /// <summary>
    /// Path MTU discovery needs oversized datagrams to be dropped, not fragmented.
    /// </summary>
    private static void TrySetDontFragment(Socket socket)
    {
        try
        {
            socket.DontFragment = true;
        }
        catch (Exception ex) when (ex is SocketException or NotSupportedException)
        {
            // Not supported for this socket on this platform: probes still work, they may be fragmented.
        }
    }
}
