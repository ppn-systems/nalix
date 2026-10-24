// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using Nalix.Abstractions.Exceptions;
using Nalix.Codec.Options;
using Nalix.Environment.Options;
using Nalix.Framework.Options;
using Xunit;

namespace Nalix.Framework.Tests.Options;

public sealed class WorkerOptionsTests
{
    [Fact]
    public void Ctor_WhenDefaultsUsed_MatchesExpectedValues()
    {
        WorkerOptions options = new();

        Assert.Equal((ushort)1, options.MachineId);
        Assert.Null(options.Tag);
        Assert.True(options.RetainFor.HasValue);
        Assert.Equal(TimeSpan.FromMinutes(2), options.RetainFor.Value);
        Assert.Null(options.GroupConcurrencyLimit);
        Assert.False(options.TryAcquireSlotImmediately);
        Assert.False(options.CancellationToken.CanBeCanceled);
        Assert.Null(options.OnCompleted);
        Assert.Null(options.OnFailed);
    }
}
