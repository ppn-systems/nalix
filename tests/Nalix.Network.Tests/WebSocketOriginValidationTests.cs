// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Network.Options;
using Xunit;

namespace Nalix.Network.Tests;

/// <summary>
/// Verifies CSWSH Origin-header validation on <see cref="NetworkWebSocketOptions.IsOriginAllowed"/>.
/// Covers issue #298 acceptance criteria: authorized, unauthorized, missing, and empty-allowlist cases.
/// </summary>
public class WebSocketOriginValidationTests
{
    [Fact]
    public void EmptyAllowlist_AllowsAnyOrigin_LegacyBehavior()
    {
        NetworkWebSocketOptions opt = new() { AllowedOrigins = string.Empty };

        Assert.True(opt.IsOriginAllowed("https://evil.example"));
        Assert.True(opt.IsOriginAllowed(null));
        Assert.True(opt.IsOriginAllowed(""));
    }

    [Fact]
    public void AuthorizedOrigin_IsAllowed()
    {
        NetworkWebSocketOptions opt = new() { AllowedOrigins = "https://schools.eyc.education,https://app.example.com" };

        Assert.True(opt.IsOriginAllowed("https://schools.eyc.education"));
        Assert.True(opt.IsOriginAllowed("https://app.example.com"));
    }

    [Fact]
    public void UnauthorizedOrigin_IsRejected()
    {
        NetworkWebSocketOptions opt = new() { AllowedOrigins = "https://schools.eyc.education" };

        Assert.False(opt.IsOriginAllowed("https://evil.example"));
        Assert.False(opt.IsOriginAllowed("http://schools.eyc.education")); // scheme mismatch
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MissingOrigin_FollowsAllowMissingOriginSetting(bool allowMissing)
    {
        NetworkWebSocketOptions opt = new()
        {
            AllowedOrigins = "https://schools.eyc.education",
            AllowMissingOrigin = allowMissing,
        };

        Assert.Equal(allowMissing, opt.IsOriginAllowed(null));
        Assert.Equal(allowMissing, opt.IsOriginAllowed(""));
    }

    [Fact]
    public void OriginMatch_IsCaseInsensitive_AndIgnoresTrailingSlash()
    {
        NetworkWebSocketOptions opt = new() { AllowedOrigins = "https://schools.eyc.education" };

        Assert.True(opt.IsOriginAllowed("HTTPS://Schools.EYC.Education"));
        Assert.True(opt.IsOriginAllowed("https://schools.eyc.education/"));
        Assert.True(opt.IsOriginAllowed(" https://schools.eyc.education "));
    }

    [Fact]
    public void Allowlist_TrimsEntriesAndTrailingSlashes()
    {
        NetworkWebSocketOptions opt = new() { AllowedOrigins = " https://a.com/ , https://b.com " };

        Assert.True(opt.IsOriginAllowed("https://a.com"));
        Assert.True(opt.IsOriginAllowed("https://b.com"));
    }

    [Fact]
    public void CacheRebuilds_WhenAllowedOriginsChanges()
    {
        NetworkWebSocketOptions opt = new() { AllowedOrigins = "https://a.com" };
        Assert.True(opt.IsOriginAllowed("https://a.com"));
        Assert.False(opt.IsOriginAllowed("https://b.com"));

        opt.AllowedOrigins = "https://b.com";
        Assert.False(opt.IsOriginAllowed("https://a.com"));
        Assert.True(opt.IsOriginAllowed("https://b.com"));
    }

    [Fact]
    public void PortMismatch_IsRejected()
    {
        NetworkWebSocketOptions opt = new() { AllowedOrigins = "https://app.example.com:8443" };

        Assert.True(opt.IsOriginAllowed("https://app.example.com:8443"));
        Assert.False(opt.IsOriginAllowed("https://app.example.com"));
        Assert.False(opt.IsOriginAllowed("https://app.example.com:9443"));
    }

    [Fact]
    public void SubdomainOrSuffix_IsNotAPrefixMatch()
    {
        NetworkWebSocketOptions opt = new() { AllowedOrigins = "https://example.com" };

        Assert.False(opt.IsOriginAllowed("https://evil.example.com"));
        Assert.False(opt.IsOriginAllowed("https://example.com.evil.net"));
        Assert.False(opt.IsOriginAllowed("null"));
    }
}
