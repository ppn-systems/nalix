// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.


using System;
using Nalix.Framework.Memory.Buffers;
using Nalix.Framework.Options;
using Xunit;

namespace Nalix.Framework.Tests.Memory;

[Collection(TestCollections.ReturnValidation)]
public sealed class ReturnValidationTests
{
    [Fact]
    public void BufferPoolManager_RentReturnWorks()
    {
        var options = MemoryTestSupport.CreateBufferOptions(enableMemoryTrimming: false);

        using BufferPoolManager manager = new(options);

        byte[] arr = manager.Rent(256);
        Assert.NotNull(arr);
        Assert.True(arr.Length >= 256);

        manager.Return(arr);
    }
}

