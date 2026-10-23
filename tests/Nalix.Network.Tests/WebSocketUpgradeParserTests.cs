// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text;
using Nalix.Network.Internal.WebSockets;
using Xunit;

namespace Nalix.Network.Tests;

public class WebSocketUpgradeParserTests
{
    [Fact]
    public void Parse_ExtractsForwardedClientIpHeaders()
    {
        byte[] request = Encoding.ASCII.GetBytes(
            "GET /ws/ HTTP/1.1\r\n" +
            "Host: example.com\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            "Sec-WebSocket-Version: 13\r\n" +
            "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n" +
            "CF-Connecting-IP: 203.0.113.10\r\n" +
            "X-Real-IP: 203.0.113.11\r\n" +
            "X-Forwarded-For: 203.0.113.12, 10.0.0.1\r\n" +
            "\r\n");

        WebSocketUpgradeResult result = WebSocketUpgradeParser.Parse(request);

        Assert.True(result.IsValid);
        Assert.True(result.IsHeaderComplete);
        Assert.False(result.IsMalformed);
        Assert.Equal("203.0.113.10", Encoding.ASCII.GetString(result.CfConnectingIp));
        Assert.Equal("203.0.113.11", Encoding.ASCII.GetString(result.XRealIp));
        Assert.Equal("203.0.113.12, 10.0.0.1", Encoding.ASCII.GetString(result.XForwardedFor));
    }

    [Fact]
    public void Parse_SplitsPathAndQuery_AndExtractsContentLengthAndType()
    {
        byte[] request = Encoding.ASCII.GetBytes(
            "POST /admin/kick?reason=spam HTTP/1.1\r\n" +
            "Host: example.com\r\n" +
            "Content-Type: application/json\r\n" +
            "Content-Length: 14\r\n" +
            "\r\n" +
            "{\"userId\":123}");

        WebSocketUpgradeResult result = WebSocketUpgradeParser.Parse(request);

        Assert.False(result.IsValid);
        Assert.True(result.IsHeaderComplete);
        Assert.False(result.IsMalformed);
        Assert.Equal("POST", Encoding.ASCII.GetString(result.HttpMethod));
        Assert.Equal("/admin/kick", Encoding.ASCII.GetString(result.Path));
        Assert.Equal("reason=spam", Encoding.ASCII.GetString(result.Query));
        Assert.Equal("application/json", Encoding.ASCII.GetString(result.ContentType));
        Assert.Equal(14, result.ContentLength);
    }

    [Fact]
    public void Parse_RejectsTransferEncodingChunkedAsMalformed()
    {
        byte[] request = Encoding.ASCII.GetBytes(
            "POST /webhook HTTP/1.1\r\n" +
            "Host: example.com\r\n" +
            "Transfer-Encoding: chunked\r\n" +
            "\r\n");

        WebSocketUpgradeResult result = WebSocketUpgradeParser.Parse(request);

        Assert.True(result.IsHeaderComplete);
        Assert.True(result.IsMalformed);
        Assert.False(result.IsValid);
    }

    [Theory]
    [InlineData("Content-Length: 14\r\nContent-Length: 0\r\n")]
    [InlineData("Content-Length: 14\r\nContent-Length: 14\r\n")]
    [InlineData("Content-Length: +14\r\n")]
    [InlineData("Content-Length: -0\r\n")]
    public void Parse_RejectsDuplicateOrSignedContentLength_AsMalformed(string clHeaders)
    {
        byte[] request = Encoding.ASCII.GetBytes(
            "POST /webhook HTTP/1.1\r\n" +
            "Host: example.com\r\n" +
            clHeaders +
            "\r\n");

        WebSocketUpgradeResult result = WebSocketUpgradeParser.Parse(request);

        Assert.True(result.IsHeaderComplete);
        Assert.True(result.IsMalformed);
        Assert.False(result.IsValid);
    }

    [Theory]
    [InlineData("Content-Length : 10\r\n")]
    [InlineData("Transfer-Encoding : chunked\r\n")]
    [InlineData("Transfer-Encoding\t: chunked\r\n")]
    [InlineData(" Content-Length: 10\r\n")]
    [InlineData("\tContent-Length: 10\r\n")]
    [InlineData("InvalidHeaderLineWithoutColon\r\n")]
    public void Parse_RejectsWhitespaceBeforeColon_ObsFold_AndMissingColon_AsMalformed(string headerLine)
    {
        byte[] request = Encoding.ASCII.GetBytes(
            "POST /webhook HTTP/1.1\r\n" +
            "Host: example.com\r\n" +
            headerLine +
            "\r\n");

        WebSocketUpgradeResult result = WebSocketUpgradeParser.Parse(request);

        Assert.True(result.IsHeaderComplete);
        Assert.True(result.IsMalformed);
        Assert.False(result.IsValid);
    }

    [Theory]
    [InlineData("GET /healthz HTTP/1.1\nTransfer-Encoding: chunked\r\nHost: example.com\r\n\r\n")]
    [InlineData("GET /healthz HTTP/1.1\r\nHost: example.com\nTransfer-Encoding: chunked\r\n\r\n")]
    [InlineData("GET /healthz HTTP/1.1\r\nHost: example.com\rTransfer-Encoding: chunked\r\n\r\n")]
    [InlineData("GET /healthz \r\nHost: example.com\r\n\r\n")]
    [InlineData("GET /healthz HTTP/1.1 SMUGGLED\r\nHost: example.com\r\n\r\n")]
    [InlineData("GET /healthz HTTP/2.0\r\nHost: example.com\r\n\r\n")]
    public void Parse_RejectsBareCrLf_AndMalformedRequestLine_AsMalformed(string rawRequest)
    {
        byte[] request = Encoding.ASCII.GetBytes(rawRequest);

        WebSocketUpgradeResult result = WebSocketUpgradeParser.Parse(request);

        Assert.True(result.IsHeaderComplete);
        Assert.True(result.IsMalformed);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Parse_RejectsOversizedHeaderKey_AsMalformed()
    {
        string longKey = new('X', 129);
        byte[] request = Encoding.ASCII.GetBytes(
            "GET /healthz HTTP/1.1\r\n" +
            "Host: example.com\r\n" +
            $"{longKey}: value\r\n" +
            "\r\n");

        WebSocketUpgradeResult result = WebSocketUpgradeParser.Parse(request);

        Assert.True(result.IsHeaderComplete);
        Assert.True(result.IsMalformed);
    }

    [Fact]
    public void Parse_RejectsNonZeroContentLengthOnGetOrWebSocketUpgrade_AsMalformed()
    {
        byte[] getWithBody = Encoding.ASCII.GetBytes(
            "GET /healthz HTTP/1.1\r\n" +
            "Host: example.com\r\n" +
            "Content-Length: 5\r\n" +
            "\r\n" +
            "hello");

        WebSocketUpgradeResult getResult = WebSocketUpgradeParser.Parse(getWithBody);
        Assert.True(getResult.IsMalformed);

        byte[] wsWithBody = Encoding.ASCII.GetBytes(
            "GET /ws/ HTTP/1.1\r\n" +
            "Host: example.com\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            "Sec-WebSocket-Version: 13\r\n" +
            "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n" +
            "Content-Length: 5\r\n" +
            "\r\n" +
            "hello");

        WebSocketUpgradeResult wsResult = WebSocketUpgradeParser.Parse(wsWithBody);
        Assert.True(wsResult.IsMalformed);
        Assert.False(wsResult.IsValid);
    }

    [Theory]
    [InlineData("X-Forwarded-For: 1.1.1.1\r\nX-Forwarded-For: 2.2.2.2\r\n")]
    [InlineData("CF-Connecting-IP: 1.1.1.1\r\nCF-Connecting-IP: 2.2.2.2\r\n")]
    [InlineData("X-Real-IP: 1.1.1.1\r\nX-Real-IP: 2.2.2.2\r\n")]
    [InlineData("Origin: https://evil.com\r\nOrigin: https://allowed.com\r\n")]
    [InlineData("Host: a.com\r\nHost: b.com\r\n")]
    public void Parse_RejectsDuplicateSingletonAndForwardedHeaders_AsMalformed(string duplicateHeaders)
    {
        byte[] request = Encoding.ASCII.GetBytes(
            "GET /ws/ HTTP/1.1\r\n" +
            (duplicateHeaders.StartsWith("Host:") ? string.Empty : "Host: example.com\r\n") +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            "Sec-WebSocket-Version: 13\r\n" +
            "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n" +
            duplicateHeaders +
            "\r\n");

        WebSocketUpgradeResult result = WebSocketUpgradeParser.Parse(request);

        Assert.True(result.IsHeaderComplete);
        Assert.True(result.IsMalformed);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void FormatHttpResponseHeader_SanitizesCrlfInContentType_PreventsResponseSplitting()
    {
        System.Span<byte> dest = stackalloc byte[512];
        System.ReadOnlySpan<byte> maliciousContentType = "text/plain\r\nX-Injected-Header: pwned\r\n\r\n<script>"u8;

        int written = Nalix.Network.Listeners.Web.WebSocketListenerBase.FormatHttpResponseHeader(
            200,
            maliciousContentType,
            4,
            dest);

        string formatted = Encoding.ASCII.GetString(dest[..written]);
        Assert.Contains("Content-Type: text/plain\r\n", formatted);
        Assert.DoesNotContain("X-Injected-Header", formatted);
        Assert.DoesNotContain("<script>", formatted);
    }
}
