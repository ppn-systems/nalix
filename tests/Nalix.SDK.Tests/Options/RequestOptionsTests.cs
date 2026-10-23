// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.SDK.Options;
using Xunit;

namespace Nalix.SDK.Tests.Options;

public sealed class RequestOptionsTests
{
    [Fact]
    public void Validate_WhenTimeoutOrRetryNegative_ThrowsArgumentOutOfRangeException()
    {
        RequestOptions negativeTimeout = RequestOptions.Default.WithTimeout(-1);
        RequestOptions negativeRetry = RequestOptions.Default.WithRetry(-1);

        _ = Assert.Throws<ArgumentOutOfRangeException>(negativeTimeout.Validate);
        _ = Assert.Throws<ArgumentOutOfRangeException>(negativeRetry.Validate);
    }

    [Fact]
    public void With_WhenChained_ReturnsNewImmutableCopies()
    {
        RequestOptions original = RequestOptions.Default;
        RequestOptions changed = original.WithTimeout(1500).WithRetry(2).WithEncrypt();

        Assert.Equal(RequestOptions.DefaultTimeoutMs, original.TimeoutMs);
        Assert.Equal(0, original.RetryCount);
        Assert.False(original.Encrypt);

        Assert.Equal(1500, changed.TimeoutMs);
        Assert.Equal(2, changed.RetryCount);
        Assert.True(changed.Encrypt);
    }
}
