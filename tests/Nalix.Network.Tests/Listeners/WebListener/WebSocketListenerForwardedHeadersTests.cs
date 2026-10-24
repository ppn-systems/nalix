// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Net;
using System.Text;
using Moq;
using Nalix.Abstractions.Networking;
using Nalix.Network.Internal.WebSockets;
using Nalix.Network.Listeners.Web;
using Nalix.Network.Options;
using Xunit;

namespace Nalix.Network.Tests.Listeners.WebListener;

public sealed class WebSocketListenerForwardedHeadersTests
{
    private static readonly IPEndPoint TrustedPeer = new(IPAddress.Parse("10.0.0.1"), 5000);

    [Fact]
    public void TryResolveForwardedEndpoint_WithAppendedXForwardedFor_ReturnsRightMostUntrustedEntry()
    {
        IConnectionGuard guard = CreateGuard("10.0.0.1");
        WebSocketUpgradeResult result = Parse(xForwardedFor: "1.2.3.4, 203.0.113.9");

        bool handled = WebSocketListenerBase.TryResolveForwardedEndpoint(TrustedPeer, result, CreateOptions(), guard, out IPEndPoint? endpoint, out bool reject);

        Assert.True(handled);
        Assert.False(reject);
        Assert.Equal(IPAddress.Parse("203.0.113.9"), endpoint!.Address);
    }

    [Fact]
    public void TryResolveForwardedEndpoint_WithTrustedProxyInXForwardedFor_SkipsTrustedEntry()
    {
        IConnectionGuard guard = CreateGuard("10.0.0.1", "10.0.0.5");
        WebSocketUpgradeResult result = Parse(xForwardedFor: "203.0.113.9, 10.0.0.5");

        bool handled = WebSocketListenerBase.TryResolveForwardedEndpoint(TrustedPeer, result, CreateOptions(), guard, out IPEndPoint? endpoint, out bool reject);

        Assert.True(handled);
        Assert.False(reject);
        Assert.Equal(IPAddress.Parse("203.0.113.9"), endpoint!.Address);
    }

    [Fact]
    public void TryResolveForwardedEndpoint_WhenAllXForwardedForEntriesTrusted_ReturnsLeftMostEntry()
    {
        IConnectionGuard guard = CreateGuard("10.0.0.1", "10.0.0.5", "10.0.0.6");
        WebSocketUpgradeResult result = Parse(xForwardedFor: "10.0.0.5, 10.0.0.6");

        _ = WebSocketListenerBase.TryResolveForwardedEndpoint(TrustedPeer, result, CreateOptions(), guard, out IPEndPoint? endpoint, out bool reject);

        Assert.False(reject);
        Assert.Equal(IPAddress.Parse("10.0.0.5"), endpoint!.Address);
    }

    [Fact]
    public void TryResolveForwardedEndpoint_WithMalformedXForwardedForEntry_Rejects()
    {
        IConnectionGuard guard = CreateGuard("10.0.0.1", "10.0.0.5");
        WebSocketUpgradeResult result = Parse(xForwardedFor: "203.0.113.9, not-an-ip, 10.0.0.5");

        bool handled = WebSocketListenerBase.TryResolveForwardedEndpoint(TrustedPeer, result, CreateOptions(), guard, out IPEndPoint? endpoint, out bool reject);

        Assert.True(handled);
        Assert.True(reject);
        Assert.Null(endpoint);
    }

    [Fact]
    public void TryResolveForwardedEndpoint_WithClientIpHeaderXRealIp_IgnoresSpoofedCfConnectingIp()
    {
        IConnectionGuard guard = CreateGuard("10.0.0.1");
        WebSocketUpgradeResult result = Parse(cfConnectingIp: "6.6.6.6", xRealIp: "203.0.113.9");
        ForwardedHeadersOptions options = CreateOptions();
        options.ClientIpHeader = ForwardedClientIpHeader.XRealIp;

        _ = WebSocketListenerBase.TryResolveForwardedEndpoint(TrustedPeer, result, options, guard, out IPEndPoint? endpoint, out bool reject);

        Assert.False(reject);
        Assert.Equal(IPAddress.Parse("203.0.113.9"), endpoint!.Address);
    }

    [Fact]
    public void TryResolveForwardedEndpoint_WithClientIpHeaderAuto_PrefersCfConnectingIp()
    {
        IConnectionGuard guard = CreateGuard("10.0.0.1");
        WebSocketUpgradeResult result = Parse(cfConnectingIp: "198.51.100.1", xRealIp: "203.0.113.9", xForwardedFor: "192.0.2.7");

        _ = WebSocketListenerBase.TryResolveForwardedEndpoint(TrustedPeer, result, CreateOptions(), guard, out IPEndPoint? endpoint, out bool reject);

        Assert.False(reject);
        Assert.Equal(IPAddress.Parse("198.51.100.1"), endpoint!.Address);
    }

    [Fact]
    public void TryResolveForwardedEndpoint_WhenPinnedHeaderMissing_ReturnsFalse()
    {
        IConnectionGuard guard = CreateGuard("10.0.0.1");
        WebSocketUpgradeResult result = Parse(cfConnectingIp: "6.6.6.6");
        ForwardedHeadersOptions options = CreateOptions();
        options.ClientIpHeader = ForwardedClientIpHeader.XForwardedFor;

        bool handled = WebSocketListenerBase.TryResolveForwardedEndpoint(TrustedPeer, result, options, guard, out IPEndPoint? endpoint, out bool reject);

        Assert.False(handled);
        Assert.False(reject);
        Assert.Null(endpoint);
    }

    [Fact]
    public void TryResolveForwardedEndpoint_WithUntrustedPhysicalPeer_IgnoresForwardedHeaders()
    {
        IConnectionGuard guard = CreateGuard("10.0.0.1");
        WebSocketUpgradeResult result = Parse(cfConnectingIp: "6.6.6.6", xRealIp: "6.6.6.7", xForwardedFor: "6.6.6.8");
        IPEndPoint untrustedPeer = new(IPAddress.Parse("192.0.2.50"), 5000);

        bool handled = WebSocketListenerBase.TryResolveForwardedEndpoint(untrustedPeer, result, CreateOptions(), guard, out IPEndPoint? endpoint, out bool reject);

        Assert.True(handled);
        Assert.False(reject);
        Assert.Null(endpoint);
    }

    private static ForwardedHeadersOptions CreateOptions() => new() { Enabled = true };

    private static IConnectionGuard CreateGuard(params string[] trusted)
    {
        Mock<IConnectionGuard> guard = new();
        _ = guard.Setup(g => g.IsTrustedProxy(It.IsAny<IPEndPoint?>()))
                 .Returns((IPEndPoint? ep) => ep is not null && Array.IndexOf(trusted, ep.Address.ToString()) >= 0);
        return guard.Object;
    }

    private static WebSocketUpgradeResult Parse(string? cfConnectingIp = null, string? xRealIp = null, string? xForwardedFor = null)
    {
        StringBuilder sb = new();
        _ = sb.Append("GET /ws/ HTTP/1.1\r\nHost: example.com\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n")
              .Append("Sec-WebSocket-Version: 13\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n");
        if (cfConnectingIp is not null)
        {
            _ = sb.Append("CF-Connecting-IP: ").Append(cfConnectingIp).Append("\r\n");
        }

        if (xRealIp is not null)
        {
            _ = sb.Append("X-Real-IP: ").Append(xRealIp).Append("\r\n");
        }

        if (xForwardedFor is not null)
        {
            _ = sb.Append("X-Forwarded-For: ").Append(xForwardedFor).Append("\r\n");
        }

        _ = sb.Append("\r\n");
        return WebSocketUpgradeParser.Parse(Encoding.ASCII.GetBytes(sb.ToString()));
    }
}
