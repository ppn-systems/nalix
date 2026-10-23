// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using Nalix.Abstractions.Exceptions;
using Nalix.Codec.Options;
using Nalix.Environment.Options;
using Nalix.Framework.Options;
using Xunit;

namespace Nalix.Framework.Tests.Options;

public sealed class RecurringOptionsTests
{
    [Fact]
    public void Ctor_WhenDefaultsUsed_MatchesExpectedValues()
    {
        RecurringOptions options = new();

        Assert.Null(options.Tag);
        Assert.True(options.NonReentrant);
        Assert.Equal(TimeSpan.FromMilliseconds(250), options.Jitter);
        Assert.Null(options.ExecutionTimeout);
        Assert.Equal(1, options.FailuresBeforeBackoff);
        Assert.Equal(TimeSpan.FromSeconds(15), options.BackoffCap);
    }
}
