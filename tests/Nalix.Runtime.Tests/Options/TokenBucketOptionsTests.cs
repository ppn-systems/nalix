// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Exceptions;
using Nalix.Runtime.Options;
using Xunit;

namespace Nalix.Runtime.Tests.Options;

public sealed class TokenBucketOptionsTests
{
    [Fact]
    public void Validate_WhenShardCountIsNotPowerOfTwo_ThrowsValidationException()
    {
        TokenBucketOptions options = new()
        {
            ShardCount = 3
        };

        _ = Assert.Throws<ValidationException>(options.Validate);
    }
}
