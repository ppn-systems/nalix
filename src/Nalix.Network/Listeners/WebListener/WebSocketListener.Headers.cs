// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Nalix.Abstractions.Diagnostics;
using Nalix.Network.Internal.WebSockets;

namespace Nalix.Network.Listeners.Web;

public abstract partial class WebSocketListenerBase
{
    /// <summary>
    /// Resolves the logical client endpoint from trusted HTTP forwarded headers.
    /// </summary>
    private bool TryResolveForwardedEndpoint(Socket socket, WebSocketUpgradeResult result, out IPEndPoint? endpoint, out bool reject)
    {
        endpoint = null;
        reject = false;

        bool hasForwardedHeader = !result.CfConnectingIp.IsEmpty || !result.XRealIp.IsEmpty || !result.XForwardedFor.IsEmpty;
        if (!_forwardedConfig.Enabled || !hasForwardedHeader)
        {
            return false;
        }

        // Forwarded IP headers are client-controlled unless the physical peer is a trusted proxy.
        if (socket.RemoteEndPoint is not IPEndPoint physicalIp || !_limiter.IsTrustedProxy(physicalIp))
        {
            string diagEvent = _forwardedConfig.RequireTrustedProxy
                ? DiagnosticsEvents.Internal.Warning
                : DiagnosticsEvents.Internal.Trace;

            if (DiagnosticsEvents.Source.IsEnabled(diagEvent))
            {
                EndPoint? remoteEndPoint = socket.RemoteEndPoint;
                DiagnosticsEvents.Write(diagEvent,
                    new DiagnosticLog("NW.WebSocketListenerBase:ForwardedHeaders",
                        $"forwarded-header-ignored reason=untrusted-physical-peer remote-endpoint={remoteEndPoint?.ToString() ?? "<null>"} require-trusted-proxy={_forwardedConfig.RequireTrustedProxy}"));
            }

            reject = _forwardedConfig.RequireTrustedProxy;
            return true;
        }

        if (!TryGetForwardedAddress(result, out IPAddress forwardedAddress))
        {
            reject = true;
            return true;
        }

        endpoint = new IPEndPoint(forwardedAddress, physicalIp.Port);
        return true;
    }

    private static bool TryGetForwardedAddress(WebSocketUpgradeResult result, out IPAddress address)
    {
        // Header precedence follows common reverse-proxy deployments: Cloudflare, direct proxy, then chain.
        // Do not fall back to lower-priority headers if a higher-priority header is present.
        if (!result.CfConnectingIp.IsEmpty)
        {
            return TryParseHeaderAddress(result.CfConnectingIp, out address);
        }

        if (!result.XRealIp.IsEmpty)
        {
            return TryParseHeaderAddress(result.XRealIp, out address);
        }

        return TryParseHeaderAddress(GetFirstForwardedFor(result.XForwardedFor), out address);
    }

    private static ReadOnlySpan<byte> GetFirstForwardedFor(ReadOnlySpan<byte> value)
    {
        int comma = value.IndexOf((byte)',');
        return comma < 0 ? value : value[..comma];
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
