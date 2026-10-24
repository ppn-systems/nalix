// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Framework.Memory.Buffers;
using Nalix.Framework.Options;
using Nalix.Framework.Tests.TestSupport;

namespace Nalix.Framework.Tests.Memory.Buffers;

public sealed class BufferPoolManagerTests
{
    [Fact]
    public void Return_WhenArrayIsNull_DoesNotThrow()
    {
        using BufferPoolManager manager = new(MemoryTestSupport.CreateBufferOptions(enableMemoryTrimming: false));

        Exception? ex = Record.Exception(() => manager.Return(null));

        Assert.Null(ex);
    }

    [Fact]
    public void Dispose_WhenCalledTwice_DoesNotThrow()
    {
        BufferPoolManager manager = new(MemoryTestSupport.CreateBufferOptions(enableMemoryTrimming: false));

        Exception? ex = Record.Exception(() =>
        {
            manager.Dispose();
            manager.Dispose();
        });

        Assert.Null(ex);
    }

    [Fact]
    public void Rent_WhenConfiguredWithDefaults_ReturnsSizedBuffer()
    {
        BufferOptions config = MemoryTestSupport.CreateBufferOptions(enableMemoryTrimming: false);
        using BufferPoolManager manager = new(config);

        byte[] rented = manager.Rent(300);
        manager.Return(rented);

        Assert.True(rented.Length >= 300);
        Assert.Equal(0, manager.MinBufferSize);
        Assert.Equal(0, manager.MaxBufferSize);
    }

    [Fact]
    public void GenerateReport_AfterRent_ContainsStatusHeader()
    {
        using BufferPoolManager manager = new(MemoryTestSupport.CreateBufferOptions(enableMemoryTrimming: false));
        _ = manager.Rent(256);

        string report = manager.GenerateReport();

        Assert.Contains("BufferPoolManager Status", report);
    }

    [Fact]
    public void Rent_WhenSizeIs2114_ReturnsBufferAtLeastThatLarge()
    {
        // Rent through the manager directly instead of mutating the process-wide
        // ByteArrayPool delegates.  The original code called
        // BufferLease.ByteArrayPool.Configure(manager) which left a disposed
        // delegate target in the global static field after this method returned.
        using var manager = new BufferPoolManager();
        byte[] arr = manager.Rent(2114);
        try
        {
            Assert.True(arr.Length >= 2114, $"Expected >= 2114, got {arr.Length}");
        }
        finally
        {
            manager.Return(arr);
        }
    }
}
