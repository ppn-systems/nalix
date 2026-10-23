// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Environment.Memory;

namespace Nalix.Environment.Tests.Memory;

public sealed class DataWriterTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Ctor_WhenInitialSizeIsNotPositive_ThrowsArgumentOutOfRangeException(int size)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new DataWriter(size));

    [Fact]
    public void Expand_WhenWriterOwnsRentedBuffer_GrowsAndPreservesContent()
    {
        DataWriter writer = new(2);
        writer.FreeBuffer[0] = 1;
        writer.FreeBuffer[1] = 2;
        writer.Advance(2);

        writer.Expand(3);
        ref byte freeRef = ref writer.GetFreeBufferReference();
        freeRef = 3;
        writer.FreeBuffer[1] = 4;
        writer.FreeBuffer[2] = 5;
        writer.Advance(3);
        byte[] result = writer.ToArray();
        int freeLengthAfterWrite = writer.FreeBuffer.Length;
        int writtenCount = writer.WrittenCount;
        writer.Dispose();

        Assert.Equal([1, 2, 3, 4, 5], result);
        Assert.Equal(5, writtenCount);
        Assert.True(freeLengthAfterWrite >= 0);
    }
}
