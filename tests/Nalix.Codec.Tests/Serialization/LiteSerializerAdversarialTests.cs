// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.
using Nalix.Abstractions.Exceptions;
using Nalix.Codec.Serialization;

namespace Nalix.Codec.Tests.Serialization;

/// <summary>
/// Adversarial tests feeding malformed/hostile bytes to <see cref="LiteSerializer"/> deserialization paths.
/// Goal: catch DoS (unbounded allocation, hangs) and memory-safety bugs (OOB read/write).
/// </summary>
public sealed class LiteSerializerAdversarialTests
{
    // ---- Truncation sweep: every prefix length of a valid serialized value must fail cleanly ----

    [Fact]
    public void Deserialize_TruncatedIntArray_EveryPrefixFailsCleanly()
    {
        int[] input = [1, 2, 3, 4, 5, 6, 7, 8];
        byte[] full = LiteSerializer.Serialize(input);

        for (int len = 0; len < full.Length; len++)
        {
            byte[] truncated = full[..len];
            int[]? output = null;

            try
            {
                _ = LiteSerializer.Deserialize(truncated, ref output);
                // A truncated buffer must never yield a fully-populated array equal to the original.
                Assert.False(output is not null && output.Length == input.Length && output.AsSpan().SequenceEqual(input),
                    $"Truncated buffer of length {len}/{full.Length} unexpectedly reproduced full array " +
                    $"(hex={System.Convert.ToHexString(truncated)}).");
            }
            catch (SerializationFailureException)
            {
                // Documented, acceptable outcome.
            }
        }
    }

    [Fact]
    public void Deserialize_TruncatedString_EveryPrefixFailsCleanly()
    {
        string input = "The quick brown fox jumps over the lazy dog";
        byte[] full = LiteSerializer.Serialize(input);

        for (int len = 0; len < full.Length; len++)
        {
            byte[] truncated = full[..len];
            string? output = null;

            try
            {
                _ = LiteSerializer.Deserialize(truncated, ref output);
                Assert.False(output == input,
                    $"Truncated string buffer of length {len}/{full.Length} unexpectedly reproduced full string " +
                    $"(hex={System.Convert.ToHexString(truncated)}).");
            }
            catch (SerializationFailureException)
            {
                // Documented, acceptable outcome.
            }
        }
    }

    [Fact]
    public void Deserialize_TruncatedObject_EveryPrefixFailsCleanly()
    {
        TestObject input = new() { Id = 42, Name = "Bob", Tags = ["a", "b", "c"] };
        byte[] full = LiteSerializer.Serialize(input);

        for (int len = 0; len < full.Length; len++)
        {
            byte[] truncated = full[..len];
            TestObject? output = null;

            try
            {
                _ = LiteSerializer.Deserialize(truncated, ref output);
                bool fullyEqual = output is not null
                    && output.Id == input.Id
                    && output.Name == input.Name
                    && output.Tags.SequenceEqual(input.Tags);

                Assert.False(fullyEqual,
                    $"Truncated object buffer of length {len}/{full.Length} unexpectedly fully reproduced object " +
                    $"(hex={System.Convert.ToHexString(truncated)}).");
            }
            catch (SerializationFailureException)
            {
                // Documented, acceptable outcome.
            }
        }
    }

    // ---- Corrupted length/count fields: DoS check — no huge allocation from a tiny hostile blob ----

    [Theory]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    [InlineData(-2)] // negative-as-signed (not the null sentinel -1)
    public void Deserialize_IntArray_HostileLengthField_RejectsWithoutHugeAllocation(int hostileLength)
    {
        // A tiny 4-byte blob claiming an element count with no backing element bytes. If the array
        // formatter allocated `hostileLength` elements before checking against remaining bytes,
        // this would OOM. CollectionGuard.TryEnsureCan must reject before allocation.
        byte[] blob = System.BitConverter.GetBytes(hostileLength);
        int[]? output = null;

        _ = Assert.ThrowsAny<SerializationFailureException>(() => LiteSerializer.Deserialize(blob, ref output));

        Assert.Null(output);
    }

    [Fact]
    public void Deserialize_IntArray_LengthOnePastReal_RejectsCleanly()
    {
        int[] input = [1, 2, 3];
        byte[] full = LiteSerializer.Serialize(input);

        byte[] mutated = (byte[])full.Clone();
        // First 4 bytes are the element count; bump by one past the real count.
        System.BitConverter.GetBytes(input.Length + 1).CopyTo(mutated, 0);

        int[]? output = null;
        _ = Assert.ThrowsAny<SerializationFailureException>(() => LiteSerializer.Deserialize(mutated, ref output));

        Assert.Null(output);
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(-2)]
    public void Deserialize_StringHostileLength_RejectsWithoutHugeAllocation(int hostileLength)
    {
        byte[] blob = System.BitConverter.GetBytes(hostileLength);
        string? output = null;

        _ = Assert.ThrowsAny<SerializationFailureException>(() => LiteSerializer.Deserialize(blob, ref output));

        Assert.Null(output);
    }

    [Fact]
    public void Deserialize_StringLengthExceedsRemainingBuffer_RejectsCleanly()
    {
        // Claims 1000 bytes of UTF-8 payload, but only supplies 3.
        byte[] blob = new byte[4 + 3];
        System.BitConverter.GetBytes(1000).CopyTo(blob, 0);
        blob[4] = (byte)'a';
        blob[5] = (byte)'b';
        blob[6] = (byte)'c';

        string? output = null;
        _ = Assert.ThrowsAny<SerializationFailureException>(() => LiteSerializer.Deserialize(blob, ref output));

        Assert.Null(output);
    }

    // ---- String edge cases ----

    [Fact]
    public void Deserialize_String_NullVsEmpty_AreDistinct()
    {
        string? nullOutput = null;
        byte[] nullBuffer = LiteSerializer.Serialize<string?>(null);
        _ = LiteSerializer.Deserialize(nullBuffer, ref nullOutput);
        Assert.Null(nullOutput);

        string? emptyOutput = null;
        byte[] emptyBuffer = LiteSerializer.Serialize(string.Empty);
        _ = LiteSerializer.Deserialize(emptyBuffer, ref emptyOutput);
        Assert.NotNull(emptyOutput);
        Assert.Equal(string.Empty, emptyOutput);
    }

    [Fact]
    public void Deserialize_String_EmbeddedNulls_RoundTrip()
    {
        string input = "abc\0def\0";
        byte[] buffer = LiteSerializer.Serialize(input);
        string? output = null;
        _ = LiteSerializer.Deserialize(buffer, ref output);

        Assert.Equal(input, output);
    }

    [Fact]
    public void Deserialize_String_InvalidUtf8Bytes_DoesNotThrowUnhandled()
    {
        // 0xFF and 0xFE are never valid UTF-8 lead bytes; 0xC0 0x80 is an overlong encoding.
        byte[] invalidUtf8 = [0xFF, 0xFE, 0xC0, 0x80, 0x80];
        byte[] blob = new byte[4 + invalidUtf8.Length];
        System.BitConverter.GetBytes(invalidUtf8.Length).CopyTo(blob, 0);
        invalidUtf8.CopyTo(blob, 4);

        string? output = null;

        // UTF8.GetString uses replacement characters by default rather than throwing;
        // this must not crash or hang regardless of decoder behavior.
        int bytesRead = LiteSerializer.Deserialize(blob, ref output);
        Assert.Equal(blob.Length, bytesRead);
        Assert.NotNull(output);
    }

    // ---- Type/layout mismatch ----

    [Fact]
    public void Deserialize_BytesForOneUnmanagedStruct_IntoDifferentSizedStruct_ThrowsOrRejects()
    {
        // ComplexStruct (int + short + byte, likely padded) serialized then read back as SmallStruct (1 byte).
        // Unmanaged fast-path deserialization only checks buffer.Length >= sizeof(T); reading a smaller
        // struct from a larger buffer succeeds (extra bytes ignored) which is documented/expected behavior
        // for the unmanaged fast path -- not a bug. We assert no exception escapes and no OOB read happens
        // (verified by the fact the call returns cleanly for either direction).
        ComplexStruct big = new() { I32 = 123456, I16 = 42, B = 7 };
        byte[] bigBuffer = LiteSerializer.Serialize(big);

        SmallStruct small = default;
        int bytesRead = LiteSerializer.Deserialize(bigBuffer, ref small);
        Assert.True(bytesRead > 0);

        // Reverse direction: small buffer read as a larger struct must fail cleanly (EndOfStream), never OOB read.
        byte[] smallBuffer = LiteSerializer.Serialize(new SmallStruct { A = 9 });
        ComplexStruct big2 = default;
        _ = Assert.ThrowsAny<SerializationFailureException>(() => LiteSerializer.Deserialize(smallBuffer, ref big2));
    }

    // ---- Random fuzz across supported shapes ----

    [Fact]
    public void Deserialize_IntArray_RandomFuzzBlobs_OnlyDocumentedErrors()
    {
        const int Seed = 90210;
        System.Random rng = new(Seed);

        for (int i = 0; i < 500; i++)
        {
            int length = rng.Next(0, 64);
            byte[] blob = new byte[length];
            rng.NextBytes(blob);

            int[]? output = null;
            RunFuzzIteration(i, Seed, blob, () => LiteSerializer.Deserialize(blob, ref output));
        }
    }

    [Fact]
    public void Deserialize_String_RandomFuzzBlobs_OnlyDocumentedErrors()
    {
        const int Seed = 90211;
        System.Random rng = new(Seed);

        for (int i = 0; i < 500; i++)
        {
            int length = rng.Next(0, 64);
            byte[] blob = new byte[length];
            rng.NextBytes(blob);

            string? output = null;
            RunFuzzIteration(i, Seed, blob, () => LiteSerializer.Deserialize(blob, ref output));
        }
    }

    [Fact]
    public void Deserialize_Object_RandomFuzzBlobs_OnlyDocumentedErrors()
    {
        const int Seed = 90212;
        System.Random rng = new(Seed);

        for (int i = 0; i < 500; i++)
        {
            int length = rng.Next(0, 128);
            byte[] blob = new byte[length];
            rng.NextBytes(blob);

            TestObject? output = null;
            RunFuzzIteration(i, Seed, blob, () => LiteSerializer.Deserialize(blob, ref output));
        }
    }

    private static void RunFuzzIteration(int iteration, int seed, byte[] blob, System.Action action)
    {
        try
        {
            action();
        }
        catch (SerializationFailureException)
        {
            // Documented, acceptable outcome.
        }
        catch (System.Exception ex)
        {
            Assert.Fail(
                $"Fuzz iteration {iteration}: undocumented exception {ex.GetType().Name}: {ex.Message} " +
                $"(seed={seed}, hex={System.Convert.ToHexString(blob)}).");
        }
    }
}
