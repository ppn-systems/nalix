// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using Nalix.Abstractions.Exceptions;
using Nalix.Environment.Options;
using Xunit;

namespace Nalix.Environment.Tests.Options;

public sealed class FragmentOptionsTests
{
    [Fact]
    public void Validate_WhenChunkSizeIsNonPositive_ThrowsValidationException()
    {
        FragmentOptions options = new()
        {
            MaxPayloadSize = 1024,
            MaxChunkSize = 0
        };

        Assert.Throws<ValidationException>(options.Validate);
    }

    [Fact]
    public void Validate_WhenPayloadIsSmallerThanChunk_ThrowsValidationException()
    {
        FragmentOptions options = new()
        {
            MaxPayloadSize = 1000,
            MaxChunkSize = 1400
        };

        Assert.Throws<ValidationException>(options.Validate);
    }

    [Fact]
    public void Validate_WhenComputedChunkCountExceedsWireLimit_ThrowsValidationException()
    {
        FragmentOptions options = new()
        {
            MaxPayloadSize = ushort.MaxValue + 1,
            MaxChunkSize = 1
        };

        Assert.Throws<ValidationException>(options.Validate);
    }

    [Fact]
    public void Validate_WhenFrameSizeExceedsWireLimit_ThrowsValidationException()
    {
        FragmentOptions options = new()
        {
            MaxPayloadSize = 100_000,
            MaxChunkSize = ushort.MaxValue
        };

        Assert.Throws<ValidationException>(options.Validate);
    }

    [Theory]
    [InlineData(8192, 8192, true)]
    [InlineData(0, 8192, false)]
    [InlineData(4096, 8192, false)]
    public void Validate_WhenPayloadAndChunkSizesVary_MatchesExpectedValidity(
        int maxPayloadSize,
        int maxChunkSize,
        bool shouldSucceed)
    {
        FragmentOptions options = new()
        {
            MaxPayloadSize = maxPayloadSize,
            MaxChunkSize = maxChunkSize
        };

        Exception? exception = Record.Exception(options.Validate);

        if (shouldSucceed)
        {
            Assert.Null(exception);
        }
        else
        {
            _ = Assert.IsType<ValidationException>(exception);
        }
    }
}
