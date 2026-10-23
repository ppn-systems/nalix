using System;
using Nalix.Environment.Random;
using Xunit;

namespace Nalix.Framework.Tests.Random;

public sealed class OsCsprngTests
{
    [Fact]
    public void Fill_ValidBuffer_FillsWithData()
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
    public void Fill_EmptyBuffer_DoesNotThrow()
    {
        OsCsprng.Fill(Span<byte>.Empty);
    }

    [Fact]
    public void Fill_MultipleCalls_ReturnsDifferentData()
    {
        byte[] b1 = new byte[16];
        byte[] b2 = new byte[16];

        OsCsprng.Fill(b1);
        OsCsprng.Fill(b2);

        Assert.NotEqual(b1, b2);
    }
}















