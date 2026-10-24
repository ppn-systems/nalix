// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using Nalix.Abstractions.Exceptions;
using Nalix.Codec.Options;
using Nalix.Environment.Options;
using Nalix.Framework.Options;
using Xunit;

namespace Nalix.Framework.Tests.Options;

public sealed class CompressionOptionsTests
{
    [Fact]
    public void Ctor_WhenDefaultsUsed_ProducesValidOptions()
    {
        CompressionOptions options = new();

        Assert.True(options.Enabled);
        Assert.Equal(512, options.MinSizeToCompress);
        options.Validate();
    }

    [Fact]
    public void Validate_WhenMinSizeIsNegative_ThrowsValidationException()
    {
        CompressionOptions options = new()
        {
            MinSizeToCompress = -1
        };

        Assert.Throws<ValidationException>(options.Validate);
    }
}
