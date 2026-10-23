// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using Nalix.Abstractions.Exceptions;
using Nalix.Codec.Options;
using Nalix.Environment.Options;
using Nalix.Framework.Options;
using Xunit;

namespace Nalix.Framework.Tests.Options;

public sealed class TaskManagerOptionsTests
{
    [Fact]
    public void Validate_WhenCleanupIntervalIsTooSmall_ThrowsValidationException()
    {
        TaskManagerOptions options = new()
        {
            CleanupInterval = TimeSpan.FromMilliseconds(999)
        };

        Assert.Throws<ValidationException>(options.Validate);
    }

    [Fact]
    public void Validate_WhenCleanupIntervalIsOneSecond_Succeeds()
    {
        TaskManagerOptions options = new()
        {
            CleanupInterval = TimeSpan.FromSeconds(1)
        };

        options.Validate();
    }

    [Fact]
    public void Validate_WhenHighCpuIsLessThanLowCpu_ThrowsValidationException()
    {
        TaskManagerOptions options = new()
        {
            ThresholdHighCpu = 30,
            ThresholdLowCpu = 50
        };

        Assert.Throws<ValidationException>(options.Validate);
    }
}
