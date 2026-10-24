// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions.Exceptions;
using Nalix.Environment.Memory;
using Nalix.Framework.Memory.Buffers;
using Nalix.Framework.Memory.Objects;
using Nalix.Framework.Options;
using Xunit;

namespace Nalix.Framework.Tests.Options;

public sealed class BufferOptionsTests
{
    [Fact]
    public void Validate_WhenOptionsAreValid_CompletesSuccessfully()
    {
        BufferOptions config = new()
        {
            EnableBufferLeakDetection = true,
            EnableBufferLeakStackTrace = true,
            SuspiciousThresholdSeconds = 100
        };

        Exception? exception = Record.Exception(config.Validate);

        Assert.Null(exception);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3601)]
    public void Validate_WhenSuspiciousThresholdIsOutOfRange_ThrowsValidationException(int suspiciousThreshold)
    {
        BufferOptions config = new()
        {
            SuspiciousThresholdSeconds = suspiciousThreshold
        };

        _ = Assert.Throws<ValidationException>(config.Validate);
    }
}
