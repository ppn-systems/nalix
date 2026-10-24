// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Threading.Tasks;
using Nalix.Environment.Random;
using Xunit;

namespace Nalix.Environment.Tests.Random;

public sealed class OsCsprngStressTests
{
    [Fact]
    public void Fill_WhenBufferIs100Bytes_FillsNonZeroData()
    {
        Span<byte> buffer = stackalloc byte[100];
        OsCsprng.Fill(buffer);

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
    public async Task Fill_WhenCalledFromManyThreads_DoesNotThrow()
    {
        const int ThreadCount = 10;
        const int Iterations = 1000;

        Task[] tasks = new Task[ThreadCount];
        for (int i = 0; i < ThreadCount; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                Span<byte> buffer = stackalloc byte[64];
                for (int j = 0; j < Iterations; j++)
                {
                    OsCsprng.Fill(buffer);
                }
            });
        }

        await Task.WhenAll(tasks);
    }

    [Fact]
    public void Fill_WhenBufferOffsetIsUnaligned_DoesNotThrow()
    {
        byte[] buffer = new byte[100];
        // Test various unaligned offsets
        for (int i = 1; i < 8; i++)
        {
            OsCsprng.Fill(buffer.AsSpan(i));
        }
    }
}
