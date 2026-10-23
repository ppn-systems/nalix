// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using Nalix.Abstractions.Exceptions;
using Nalix.Codec.Options;
using Nalix.Environment.Options;
using Nalix.Framework.Options;
using Xunit;

namespace Nalix.Framework.Tests.Options;

public sealed class SecurityOptionsTests
{
    [Fact]
    public void Validate_WhenIterationsAreAtBoundaries_Succeeds()
    {
        SecurityOptions min = new() { Pbkdf2Iterations = 1000 };
        SecurityOptions max = new() { Pbkdf2Iterations = 10_000_000 };

        min.Validate();
        max.Validate();
    }

    [Fact]
    public void Validate_WhenIterationsAreOutOfRange_ThrowsValidationException()
    {
        SecurityOptions options = new() { Pbkdf2Iterations = 999 };

        Assert.Throws<ValidationException>(() => options.Validate());
    }
}
