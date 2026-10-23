// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Exceptions;
using Nalix.Hosting.Options;

namespace Nalix.Hosting.Tests.Options;

public sealed class HostingOptionsTests
{
    [Fact]
    public void Validate_NegativeMinWorkerThreads_ThrowsClearError()
    {
        HostingOptions options = new() { MinWorkerThreads = -1 };

        // [ValueRange(0, int.MaxValue)] on MinWorkerThreads -> misconfiguration must
        // fail fast/clearly rather than silently clamping or being ignored.
        _ = Assert.ThrowsAny<Exception>(options.Validate);
    }

    [Fact]
    public void Validate_DefaultOptions_Succeeds()
    {
        HostingOptions options = new();
        options.Validate(); // Should not throw.
        Assert.False(options.DisableConsoleClear);
        Assert.True(options.EnableGlobalExceptionHandling);
    }

    [Fact]
    public void MinCompletionPortThreads_NegativeValue_ThrowsClearError()
    {
        HostingOptions options = new() { MinCompletionPortThreads = -5 };
        _ = Assert.ThrowsAny<Exception>(options.Validate);
    }
}
