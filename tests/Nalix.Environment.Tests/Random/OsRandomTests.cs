using System;
using System.Threading.Tasks;
using Nalix.Environment.Random;
using Xunit;

namespace Nalix.Environment.Tests.Random;

public class OsRandomTests
{
    [Fact]
    public void Fill_FillsBufferWithNonZeroData()
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
    public async Task Fill_Multithreaded_DoesNotCrash()
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
    public void Fill_Unaligned_DoesNotCrash()
    {
        byte[] buffer = new byte[100];
        // Test various unaligned offsets
        for (int i = 1; i < 8; i++)
        {
            OsCsprng.Fill(buffer.AsSpan(i));
        }
    }
}


