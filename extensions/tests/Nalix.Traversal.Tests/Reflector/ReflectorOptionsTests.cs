// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Traversal.Options;

namespace Nalix.Traversal.Tests.Reflector;

/// <summary>
/// Verifies the documented default limits of <see cref="ReflectorOptions"/>.
/// </summary>
public sealed class ReflectorOptionsTests
{
    [Fact]
    public void Ctor_WhenCreated_UsesDocumentedDefaults()
    {
        ReflectorOptions options = new();

        Assert.Equal((ushort)28001, options.Port);
        Assert.Equal(500L * 1024, options.BandwidthBurstCapacity);
        Assert.Equal(200L * 1024, options.BandwidthFillRate);
    }
}
