// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Nalix.Abstractions.Diagnostics;
using Nalix.Abstractions.Networking;
using Nalix.Network.Internal.WebSockets;
using Nalix.Network.Options;

namespace Nalix.Network.Listeners.Web;

public abstract partial class WebSocketListenerBase
{
    /// <summary>
    /// Resolves the logical client endpoint from trusted HTTP forwarded headers.
    /// </summary>
    private bool TryResolveForwardedEndpoint(Socket socket, WebSocketUpgradeResult result, out IPEndPoint? endpoint, out bool reject)
        => TryResolveForwardedEndpoint(socket.RemoteEndPoint, result, _forwardedConfig, _limiter, out endpoint, out reject);

    /// <summary>
    /// Resolves the logical client endpoint for a physical peer from trusted HTTP forwarded headers.
    /// </summary>
    internal static bool TryResolveForwardedEndpoint(
        EndPoint? remoteEndPoint, WebSocketUpgradeResult result, ForwardedHeadersOptions options,
        IConnectionGuard guard, out IPEndPoint? endpoint, out bool reject)
    {
        endpoint = null;
        reject = false;

        bool hasForwardedHeader = options.ClientIpHeader switch
        {
            ForwardedClientIpHeader.CfConnectingIp => !result.CfConnectingIp.IsEmpty,
            ForwardedClientIpHeader.XRealIp => !result.XRealIp.IsEmpty,
            ForwardedClientIpHeader.XForwardedFor => !result.XForwardedFor.IsEmpty,
            ForwardedClientIpHeader.Auto or _ => !result.CfConnectingIp.IsEmpty || !result.XRealIp.IsEmpty || !result.XForwardedFor.IsEmpty,
        };
        if (!options.Enabled || !hasForwardedHeader)
        {
            return false;
        }

        // Forwarded IP headers are client-controlled unless the physical peer is a trusted proxy.
        if (remoteEndPoint is not IPEndPoint physicalIp || !guard.IsTrustedProxy(physicalIp))
        {
            string diagEvent = options.RequireTrustedProxy
                ? DiagnosticsEvents.Internal.Warning
                : DiagnosticsEvents.Internal.Trace;

            if (DiagnosticsEvents.Source.IsEnabled(diagEvent))
            {
                DiagnosticsEvents.Write(diagEvent,
                    new DiagnosticLog("NW.WebSocketListenerBase:ForwardedHeaders",
                        $"forwarded-header-ignored reason=untrusted-physical-peer remote-endpoint={remoteEndPoint?.ToString() ?? "<null>"} require-trusted-proxy={options.RequireTrustedProxy}"));
            }

            reject = options.RequireTrustedProxy;
            return true;
        }

        if (!TryGetForwardedAddress(result, options.ClientIpHeader, guard, out IPAddress forwardedAddress))
        {
            reject = true;
            return true;
        }

        endpoint = new IPEndPoint(forwardedAddress, physicalIp.Port);
        return true;
    }

    private static bool TryGetForwardedAddress(
        WebSocketUpgradeResult result, ForwardedClientIpHeader header, IConnectionGuard guard, out IPAddress address)
    {
        switch (header)
        {
            case ForwardedClientIpHeader.CfConnectingIp:
                return TryParseHeaderAddress(result.CfConnectingIp, out address);
            case ForwardedClientIpHeader.XRealIp:
                return TryParseHeaderAddress(result.XRealIp, out address);
            case ForwardedClientIpHeader.XForwardedFor:
                return TryGetForwardedForAddress(result.XForwardedFor, guard, out address);
            case ForwardedClientIpHeader.Auto:
            default:
                break;
        }

        // Auto: Cloudflare, direct proxy, then chain.
        // Do not fall back to lower-priority headers if a higher-priority header is present.
        if (!result.CfConnectingIp.IsEmpty)
        {
            return TryParseHeaderAddress(result.CfConnectingIp, out address);
        }

        if (!result.XRealIp.IsEmpty)
        {
            return TryParseHeaderAddress(result.XRealIp, out address);
        }

        return TryGetForwardedForAddress(result.XForwardedFor, guard, out address);
    }

    /// <summary>
    /// Walks X-Forwarded-For right-to-left and returns the first entry that is not a trusted proxy.
    /// Entries left of it are client-controlled. If every entry is trusted, the left-most is used.
    /// Any malformed entry encountered during the walk fails the resolution.
    /// </summary>
    private static bool TryGetForwardedForAddress(ReadOnlySpan<byte> value, IConnectionGuard guard, out IPAddress address)
    {
        address = IPAddress.None;
        ReadOnlySpan<byte> remaining = value;

        while (true)
        {
            int comma = remaining.LastIndexOf((byte)',');
            ReadOnlySpan<byte> entry = comma < 0 ? remaining : remaining[(comma + 1)..];

            if (!TryParseHeaderAddress(entry, out address))
            {
                return false;
            }

            // Left-most entry: use it even if trusted (every hop was a trusted proxy).
            if (comma < 0 || !guard.IsTrustedProxy(new IPEndPoint(address, 0)))
            {
                return true;
            }

            remaining = remaining[..comma];
        }
    }

    private static bool TryParseHeaderAddress(ReadOnlySpan<byte> value, out IPAddress address)
    {
        address = IPAddress.None;
        value = TrimAsciiSpace(value);
        if (value.IsEmpty || value.Length > 45)
        {
            return false;
        }

        int dotCount = 0;
        bool hasColon = false;
        for (int i = 0; i < value.Length; i++)
        {
            byte b = value[i];
            if (b is <= 0x20 or >= 0x7F or (byte)'%')
            {
                return false;
            }

            if (b == (byte)'.')
            {
                dotCount++;
            }
            else if (b == (byte)':')
            {
                hasColon = true;
            }
        }

        if (!hasColon && dotCount != 3)
        {
            return false;
        }

        Span<char> charBuf = stackalloc char[value.Length];
        int written = Encoding.ASCII.GetChars(value, charBuf);
        return IPAddress.TryParse(charBuf[..written], out address!);
    }
}
