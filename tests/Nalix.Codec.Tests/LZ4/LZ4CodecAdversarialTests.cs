// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Exceptions;
using Nalix.Codec.LZ4;

namespace Nalix.Codec.Tests.LZ4;

/// <summary>
/// Adversarial tests feeding hostile/malformed compressed input into the LZ4 decoder.
/// Goal: catch DoS (unbounded allocation, hangs) and memory-safety bugs (OOB read/write).
/// </summary>
public sealed class LZ4CodecAdversarialTests
{
    private static byte[] CreateSamplePayload(int length, System.Random rng)
    {
        byte[] data = new byte[length];
        rng.NextBytes(data);
        return data;
    }

    private static byte[] CompressValid(byte[] original)
    {
        int maxCompressedLength = LZ4BlockEncoder.GetMaxLength(original.Length);
        byte[] compressed = new byte[maxCompressedLength];
        int written = LZ4Codec.Encode(original, compressed);
        return compressed[..written];
    }

    // ---- Decompression bomb: declared OriginalLength far exceeds real payload / MaxBlockSize ----

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(LZ4CompressionConstants.MaxBlockSize + 1)]
    [InlineData(LZ4CompressionConstants.MaxBlockSize * 4)]
    public void Decode_DeclaredOriginalLengthExceedsMaxBlockSize_RejectsWithoutAllocating(int declaredOriginalLength)
    {
        // Header: originalLength (huge), compressedLength (small, consistent with actual buffer)
        byte[] input = new byte[LZ4BlockHeader.Size + 4];
        System.BitConverter.GetBytes(declaredOriginalLength).CopyTo(input, 0);
        System.BitConverter.GetBytes(input.Length).CopyTo(input, 4);

        byte[] output = new byte[16]; // tiny destination; a real bug would blow past this
        _ = Assert.ThrowsAny<LZ4Exception>(() => LZ4Codec.Decode(input, output));

        bool tryResult = LZ4Codec.TryDecode(input, output, out int written);
        Assert.False(tryResult);
        Assert.Equal(0, written);
    }

    [Fact]
    public void Decode_HugeOriginalLength_DoesNotAllocateHugeLeaseBuffer()
    {
        // A tiny hostile blob claiming a huge decompressed size must be rejected
        // by header validation (bounded against MaxBlockSize) before any lease is rented.
        int declared = LZ4CompressionConstants.MaxBlockSize + 1;
        byte[] input = new byte[LZ4BlockHeader.Size];
        System.BitConverter.GetBytes(declared).CopyTo(input, 0);
        System.BitConverter.GetBytes(input.Length).CopyTo(input, 4);

        bool result = LZ4Codec.TryDecode(input, out var lease, out int bytesWritten);
        Assert.False(result);
        Assert.Null(lease);
        Assert.Equal(0, bytesWritten);
    }

    [Theory]
    [InlineData(0, 1)]    // 0 payload bytes cannot produce 1 byte
    [InlineData(1, 1)]    // 1 payload byte (token only) cannot produce 1 byte
    [InlineData(2, 2)]    // 2 payload bytes (token + 1 literal) can produce at most 1 byte
    [InlineData(3, 3)]    // 3 payload bytes (token + 2 literals) can produce at most 2 bytes
    [InlineData(4, 20)]   // 4 payload bytes (token + 1 literal + 2B offset) can produce at most 19 bytes
    [InlineData(5, 275)]  // 5 payload bytes can produce at most 274 bytes
    [InlineData(4, LZ4CompressionConstants.MaxBlockSize)] // 4-byte payload claiming 256 KB
    public void Decode_OriginalLengthExceedsMathematicalMaxExpansion_RejectsWithoutAllocating(int payloadLength, int declaredOriginalLength)
    {
        byte[] input = new byte[LZ4BlockHeader.Size + payloadLength];
        System.BitConverter.GetBytes(declaredOriginalLength).CopyTo(input, 0);
        System.BitConverter.GetBytes(input.Length).CopyTo(input, 4);

        bool result = LZ4Codec.TryDecode(input, out var lease, out int bytesWritten);
        Assert.False(result);
        Assert.Null(lease);
        Assert.Equal(0, bytesWritten);
    }

    [Fact]
    public void Decode_ZeroOriginalLengthWithTrailingPayload_RejectsCleanly()
    {
        byte[] input = new byte[LZ4BlockHeader.Size + 4];
        System.BitConverter.GetBytes(0).CopyTo(input, 0);
        System.BitConverter.GetBytes(input.Length).CopyTo(input, 4);

        byte[] output = new byte[16];
        _ = Assert.ThrowsAny<LZ4Exception>(() => LZ4Codec.Decode(input, output));
        Assert.False(LZ4Codec.TryDecode(input, output, out _));
    }

    // ---- Malformed match offsets ----

    [Fact]
    public void Decode_MatchOffsetZero_RejectsCleanly()
    {
        // token: literalLen=1 (high nibble), matchLen=0 (low nibble) -> one literal byte, then offset bytes, then match
        // Build: [token=0x10][literal 'A'][offset=0x0000][no more bytes -> would need matchLen decode]
        // literalLength=1, then offset read as ushort = 0 => invalid (offset==0 check)
        byte[] payload = [0x10, (byte)'A', 0x00, 0x00];
        byte[] compressed = BuildBlock(originalLength: 5, payload);

        byte[] output = new byte[5];
        _ = Assert.ThrowsAny<LZ4Exception>(() => LZ4Codec.Decode(compressed, output));
    }

    [Fact]
    public void Decode_MatchOffsetPointsBeforeStartOfOutput_RejectsCleanly()
    {
        // literalLength=1 writes 1 byte to output (outputPtr - outputBase == 1).
        // offset=2 means matchSourcePtr = outputPtr - 2 = outputBase - 1 -> before start. Must be rejected.
        byte[] payload = [0x10, (byte)'A', 0x02, 0x00];
        byte[] compressed = BuildBlock(originalLength: 5, payload);

        byte[] output = new byte[5];
        _ = Assert.ThrowsAny<LZ4Exception>(() => LZ4Codec.Decode(compressed, output));
    }

    [Fact]
    public void Decode_MatchLengthExceedsRemainingOutput_RejectsCleanly()
    {
        // literal of 4 bytes, then a match with offset=1 but huge extra length (0xF continuation) that
        // would overrun the tiny output buffer.
        byte[] payload =
        [
            0x4F, // literalLen=4, matchLen=0xF (needs continuation)
            (byte)'A', (byte)'B', (byte)'C', (byte)'D',
            0x01, 0x00, // offset = 1
            0xFF, 0xFF, 0xFF, 0x7F, // huge continuation length
        ];
        byte[] compressed = BuildBlock(originalLength: 8, payload);

        byte[] output = new byte[8];
        _ = Assert.ThrowsAny<LZ4Exception>(() => LZ4Codec.Decode(compressed, output));
    }

    // ---- Malformed literal lengths ----

    [Fact]
    public void Decode_LiteralLengthExceedsInput_RejectsCleanly()
    {
        // token says literalLen=15 (0xF continuation) with a length far larger than remaining input.
        byte[] payload =
        [
            0xF0, // literalLen=0xF continuation, matchLen=0
            0xFF, 0xFF, 0xFF, 0x7F, // huge extra length
            (byte)'A', // not nearly enough literal bytes follow
        ];
        byte[] compressed = BuildBlock(originalLength: 64, payload);

        byte[] output = new byte[64];
        _ = Assert.ThrowsAny<LZ4Exception>(() => LZ4Codec.Decode(compressed, output));
    }

    [Fact]
    public void Decode_LiteralLengthExceedsOutput_RejectsCleanly()
    {
        byte[] payload =
        [
            0x40, // literalLen=4, matchLen=0
            (byte)'A', (byte)'B', (byte)'C', (byte)'D',
        ];
        byte[] compressed = BuildBlock(originalLength: 2, payload); // output too small for 4 literal bytes

        byte[] output = new byte[2];
        _ = Assert.ThrowsAny<LZ4Exception>(() => LZ4Codec.Decode(compressed, output));
    }

    // ---- Truncated blocks: every prefix length of a valid compressed block must fail cleanly ----

    [Fact]
    public void Decode_TruncatedValidBlock_EveryPrefixFailsCleanlyOrRejects()
    {
        System.Random rng = new(20260705);
        byte[] original = CreateSamplePayload(512, rng);
        byte[] compressed = CompressValid(original);

        for (int len = 0; len < compressed.Length; len++)
        {
            byte[] truncated = compressed[..len];
            byte[] output = new byte[original.Length];

            bool ok = LZ4Codec.TryDecode(truncated, output, out int written);
            if (ok)
            {
                // Only acceptable "success" for a truncated buffer is exact full decode
                // (which can't happen for len < compressed.Length), so this should never trigger.
                Assert.Fail(
                    $"Truncated block of length {len}/{compressed.Length} unexpectedly decoded successfully " +
                    $"(seed=20260705, hex={System.Convert.ToHexString(truncated)}).");
            }
            Assert.Equal(0, written);
        }
    }

    // ---- Header validation: mutate each field ----

    [Fact]
    public void Decode_InputShorterThanHeaderSize_ThrowsLZ4Exception()
    {
        for (int len = 0; len < LZ4BlockHeader.Size; len++)
        {
            byte[] input = new byte[len];
            byte[] output = new byte[16];
            _ = Assert.ThrowsAny<LZ4Exception>(() => LZ4Codec.Decode(input, output));
        }
    }

    [Fact]
    public void Decode_NegativeOriginalLength_ThrowsLZ4Exception()
    {
        byte[] input = new byte[LZ4BlockHeader.Size];
        System.BitConverter.GetBytes(-1).CopyTo(input, 0);
        System.BitConverter.GetBytes(input.Length).CopyTo(input, 4);

        byte[] output = new byte[16];
        _ = Assert.ThrowsAny<LZ4Exception>(() => LZ4Codec.Decode(input, output));
    }

    [Fact]
    public void Decode_CompressedLengthLessThanHeaderSize_ThrowsLZ4Exception()
    {
        byte[] input = new byte[LZ4BlockHeader.Size];
        System.BitConverter.GetBytes(0).CopyTo(input, 0);
        System.BitConverter.GetBytes(LZ4BlockHeader.Size - 1).CopyTo(input, 4);

        byte[] output = new byte[16];
        _ = Assert.ThrowsAny<LZ4Exception>(() => LZ4Codec.Decode(input, output));
    }

    [Fact]
    public void Decode_CompressedLengthInconsistentWithActualInputLength_ThrowsLZ4Exception()
    {
        byte[] input = new byte[LZ4BlockHeader.Size + 10];
        System.BitConverter.GetBytes(0).CopyTo(input, 0);
        System.BitConverter.GetBytes(input.Length + 1).CopyTo(input, 4); // claims more than actually provided

        byte[] output = new byte[16];
        _ = Assert.ThrowsAny<LZ4Exception>(() => LZ4Codec.Decode(input, output));
    }

    [Fact]
    public void Decode_WhenOriginalLengthFieldIsMutated_RejectsCleanly()
    {
        System.Random rng = new(777);
        byte[] original = CreateSamplePayload(200, rng);
        byte[] compressed = CompressValid(original);

        byte[] mutated = (byte[])compressed.Clone();
        System.BitConverter.GetBytes(original.Length + 1).CopyTo(mutated, 0);

        byte[] output = new byte[original.Length + 16];
        bool ok = LZ4Codec.TryDecode(mutated, output, out int written);
        Assert.False(ok, $"Mutated OriginalLength field should be rejected (seed=777, hex={System.Convert.ToHexString(mutated)}).");
    }

    [Fact]
    public void Decode_WhenCompressedLengthFieldIsMutated_RejectsCleanly()
    {
        System.Random rng = new(778);
        byte[] original = CreateSamplePayload(200, rng);
        byte[] compressed = CompressValid(original);

        byte[] mutated = (byte[])compressed.Clone();
        System.BitConverter.GetBytes(compressed.Length + 5).CopyTo(mutated, 4);

        byte[] output = new byte[original.Length];
        bool ok = LZ4Codec.TryDecode(mutated, output, out int written);
        Assert.False(ok, $"Mutated CompressedLength field should be rejected (seed=778, hex={System.Convert.ToHexString(mutated)}).");
    }

    // ---- Round-trip property: 200+ seeded random inputs of varied sizes/compressibility ----

    [Fact]
    public void RoundTrip_RandomInputs_AlwaysEqualsOriginalExactly()
    {
        const int Seed = 424242;
        System.Random rng = new(Seed);

        for (int i = 0; i < 200; i++)
        {
            int length = rng.Next(0, 8192);
            byte[] original = new byte[length];

            // Vary compressibility: all-zeros, fully random, repetitive.
            int mode = i % 3;
            switch (mode)
            {
                case 0:
                    // all zeros already
                    break;
                case 1:
                    rng.NextBytes(original);
                    break;
                case 2:
                    byte fill = (byte)rng.Next(256);
                    System.Array.Fill(original, fill);
                    break;
            }

            byte[] compressed = CompressValid(original);
            byte[] decompressed = new byte[original.Length];
            int writtenDecompressed = LZ4Codec.Decode(compressed, decompressed);

            Assert.True(
                original.Length == writtenDecompressed && original.AsSpan().SequenceEqual(decompressed),
                $"Round-trip mismatch at iteration {i} (seed={Seed}, mode={mode}, length={length}, " +
                $"originalHex={System.Convert.ToHexString(original)[..System.Math.Min(64, original.Length * 2)]}).");
        }
    }

    // ---- Random fuzz: feed raw random bytes straight into the decompressor ----

    [Fact]
    public void Decode_RandomFuzzBlobs_OnlyDocumentedErrorsOrCleanSuccess()
    {
        const int Seed = 13371337;
        System.Random rng = new(Seed);

        for (int i = 0; i < 500; i++)
        {
            int length = rng.Next(0, 512);
            byte[] blob = new byte[length];
            rng.NextBytes(blob);

            byte[] output = new byte[4096];

            try
            {
                bool ok = LZ4Codec.TryDecode(blob, output, out int written);
                if (ok)
                {
                    Assert.True(written >= 0 && written <= output.Length,
                        $"Fuzz iteration {i}: successful decode reported invalid length {written} " +
                        $"(seed={Seed}, hex={System.Convert.ToHexString(blob)}).");
                }
            }
            catch (LZ4Exception)
            {
                // Documented, acceptable outcome.
            }
            catch (System.Exception ex)
            {
                Assert.Fail(
                    $"Fuzz iteration {i}: undocumented exception {ex.GetType().Name}: {ex.Message} " +
                    $"(seed={Seed}, hex={System.Convert.ToHexString(blob)}).");
            }
        }
    }

    /// <summary>
    /// Builds a raw LZ4 block (header + token stream) around a hand-crafted payload,
    /// so tests can exercise specific token/offset/length shapes.
    /// </summary>
    private static byte[] BuildBlock(int originalLength, byte[] payload)
    {
        byte[] compressed = new byte[LZ4BlockHeader.Size + payload.Length];
        System.BitConverter.GetBytes(originalLength).CopyTo(compressed, 0);
        System.BitConverter.GetBytes(compressed.Length).CopyTo(compressed, 4);
        payload.CopyTo(compressed, LZ4BlockHeader.Size);
        return compressed;
    }
}
