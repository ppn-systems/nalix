// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using Nalix.Environment.Random;
using Xunit;

namespace Nalix.Environment.Tests.Random;

public sealed class OsCsprngTests
{
    [Fact]
    public void Fill_WhenBufferIsValid_FillsWithData()
    {
        byte[] buffer = new byte[32];
        OsCsprng.Fill(buffer);

        // Very unlikely to be all zeros
        bool allZero = true;
        foreach (byte b in buffer)
        {
            if (b != 0)
            {
                allZero = false;
                break;
            }
        }
        Assert.False(allZero);
    }

    [Fact]
    public void Fill_WhenBufferIsEmpty_DoesNotThrow()
    {
        OsCsprng.Fill(Span<byte>.Empty);
    }

    [Fact]
    public void Fill_WhenCalledTwice_ReturnsDifferentData()
    {
        byte[] b1 = new byte[16];
        byte[] b2 = new byte[16];

        OsCsprng.Fill(b1);
        OsCsprng.Fill(b2);

        Assert.NotEqual(b1, b2);
    }
}
