// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Framework.Tasks;
using Xunit;

namespace Nalix.Framework.Tests.Tasks;

/// <summary>
/// Covers task naming helpers that are intentionally separate from <see cref="TaskManager"/> runtime behavior.
/// </summary>
public sealed class TaskNamingTests
{
    [Fact]
    public void Tags_WhenRead_ExposesExpectedPublicConstants()
    {
        Assert.Equal("tcp", TaskNaming.Tags.Tcp);
        Assert.Equal("udp", TaskNaming.Tags.Udp);
        Assert.Equal("net", TaskNaming.Tags.Net);
        Assert.Equal("time", TaskNaming.Tags.Time);
        Assert.Equal("sync", TaskNaming.Tags.Sync);
        Assert.Equal("wheel", TaskNaming.Tags.Wheel);
        Assert.Equal("proc", TaskNaming.Tags.Process);
        Assert.Equal("worker", TaskNaming.Tags.Worker);
        Assert.Equal("accept", TaskNaming.Tags.Accept);
        Assert.Equal("cleanup", TaskNaming.Tags.Cleanup);
        Assert.Equal("service", TaskNaming.Tags.Service);
        Assert.Equal("dispatch", TaskNaming.Tags.Dispatch);
    }

    [Theory]
    [InlineData(null, "-")]
    [InlineData("", "-")]
    [InlineData("abc-_.123", "abc-_.123")]
    [InlineData("ab c/+", "ab_c__")]
    [InlineData("Xin-Chao.2026", "Xin-Chao.2026")]
    [InlineData("name:with:colon", "name_with_colon")]
    public void SanitizeToken_WhenValueProvided_ReturnsExpectedValue(string? value, string expected)
    {
        string sanitized = TaskNaming.SanitizeToken(value!);

        Assert.Equal(expected, sanitized);
    }

    [Theory]
    [InlineData("cleanup job", 0xBC614E, "cleanup_job.cleanup.00BC614E")]
    [InlineData("svc", 15, "svc.cleanup.0000000F")]
    [InlineData("sync/service", unchecked((int)0xFFFFFFFF), "sync_service.cleanup.FFFFFFFF")]
    public void CleanupJobId_WhenPrefixAndKeyProvided_BuildsStableIdentifier(string prefix, int instanceKey, string expected)
    {
        string identifier = TaskNaming.Recurring.CleanupJobId(prefix, instanceKey);

        Assert.Equal(expected, identifier);
    }
}
