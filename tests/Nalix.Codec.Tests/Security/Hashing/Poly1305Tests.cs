// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Security.Cryptography;
using System.Text;
using Nalix.Abstractions.Primitives;
using Nalix.Codec.Security.Aead;
using Nalix.Codec.Security.Asymmetric;
using Nalix.Codec.Security.Hashing;
using Nalix.Codec.Security.Symmetric;
using Org.BouncyCastle.Crypto.Parameters;
using BCPoly1305 = Org.BouncyCastle.Crypto.Macs.Poly1305;

namespace Nalix.Codec.Tests.Security.Hashing;

public sealed class Poly1305Tests
{
    private static byte[] HexToBytes(string hex)
    {
        byte[] result = new byte[hex.Length / 2];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }

        return result;
    }

    private static byte[] BuildKey(string rHex16Bytes, string sHex16Bytes)
    {
        byte[] key = new byte[Poly1305.KeySize];
        HexToBytes(rHex16Bytes).CopyTo(key, 0);
        HexToBytes(sHex16Bytes).CopyTo(key, 16);
        return key;
    }

    private static byte[] GenerateRandom(int size)
    {
        byte[] data = new byte[size];
        RandomNumberGenerator.Fill(data);
        return data;
    }

    /// <summary>
    /// RFC 8439 §2.5.2 example tag.
    /// </summary>
    [Fact]
    public void Compute_WithRfc8439Section252Vector_MatchesExpectedTag()
    {
        byte[] key = HexToBytes(
            "85d6be7857556d337f4452fe42d506a8" +
            "0103808afb0db2fd4abff6af4149f51b");
        byte[] message = Encoding.ASCII.GetBytes("Cryptographic Forum Research Group");
        byte[] expectedTag = HexToBytes("a8061dc1305136c6c22b8baf0c0127a9");

        byte[] tag = Poly1305.Compute(key, message);

        Assert.Equal(expectedTag, tag);
    }

    /// <summary>
    /// RFC 8439 Appendix A.3, Test Vector #1: all-zero key, all-zero 64-byte message.
    /// </summary>
    [Fact]
    public void Compute_WithRfc8439AppendixA3Vector1_MatchesExpectedTag()
    {
        byte[] key = new byte[Poly1305.KeySize];
        byte[] message = new byte[64];
        byte[] expectedTag = new byte[Poly1305.TagSize];

        byte[] tag = Poly1305.Compute(key, message);

        Assert.Equal(expectedTag, tag);
    }

    /// <summary>
    /// RFC 8439 Appendix A.3, Test Vector #2: r = 0 weak-key case.
    /// </summary>
    [Fact]
    public void Compute_WithRfc8439AppendixA3Vector2_MatchesExpectedTag()
    {
        byte[] key = HexToBytes(
            "00000000000000000000000000000000" +
            "36e5f6b5c5e06070f0efca96227a863e");
        byte[] message = Encoding.ASCII.GetBytes(
            "Any submission to the IETF intended by the Contributor for " +
            "publication as all or part of an IETF Internet-Draft or RFC " +
            "and any statement made within the context of an IETF " +
            "activity is considered an \"IETF Contribution\". Such " +
            "statements include oral statements in IETF sessions, as well " +
            "as written and electronic communications made at any time or " +
            "place, which are addressed to");
        byte[] expectedTag = HexToBytes("36e5f6b5c5e06070f0efca96227a863e");

        byte[] tag = Poly1305.Compute(key, message);

        Assert.Equal(expectedTag, tag);
    }

    /// <summary>
    /// RFC 8439 Appendix A.3, Test Vector #3.
    /// </summary>
    [Fact]
    public void Compute_WithRfc8439AppendixA3Vector3_MatchesExpectedTag()
    {
        byte[] key = HexToBytes(
            "36e5f6b5c5e06070f0efca96227a863e" +
            "00000000000000000000000000000000");
        byte[] message = Encoding.ASCII.GetBytes(
            "Any submission to the IETF intended by the Contributor for " +
            "publication as all or part of an IETF Internet-Draft or RFC " +
            "and any statement made within the context of an IETF " +
            "activity is considered an \"IETF Contribution\". Such " +
            "statements include oral statements in IETF sessions, as well " +
            "as written and electronic communications made at any time or " +
            "place, which are addressed to");
        byte[] expectedTag = HexToBytes("f3477e7cd95417af89a6b8794c310cf0");

        byte[] tag = Poly1305.Compute(key, message);

        Assert.Equal(expectedTag, tag);
    }

    /// <summary>
    /// RFC 8439 Appendix A.3, Test Vector #4.
    /// </summary>
    [Fact]
    public void Compute_WithRfc8439AppendixA3Vector4_MatchesExpectedTag()
    {
        byte[] key = HexToBytes(
            "1c9240a5eb55d38af333888604f6b5f0" +
            "473917c1402b80099dca5cbc207075c0");
        byte[] message = Encoding.ASCII.GetBytes(
            "'Twas brillig, and the slithy toves\n" +
            "Did gyre and gimble in the wabe:\n" +
            "All mimsy were the borogoves,\n" +
            "And the mome raths outgrabe.");
        byte[] expectedTag = HexToBytes("4541669a7eaaee61e708dc7cbcc5eb62");

        byte[] tag = Poly1305.Compute(key, message);

        Assert.Equal(expectedTag, tag);
    }

    /// <summary>
    /// RFC 8439 Appendix A.3, Test Vector #5: 130-bit partial reduction edge case.
    /// </summary>
    [Fact]
    public void Compute_WithRfc8439AppendixA3Vector5_MatchesExpectedTag()
    {
        byte[] key = BuildKey(
            "02000000000000000000000000000000",
            "00000000000000000000000000000000");
        byte[] message = HexToBytes("ffffffffffffffffffffffffffffffff");
        byte[] expectedTag = HexToBytes("03000000000000000000000000000000");

        byte[] tag = Poly1305.Compute(key, message);

        Assert.Equal(expectedTag, tag);
    }

    /// <summary>
    /// RFC 8439 Appendix A.3, Test Vector #6: addition of s overflows modulo 2^128.
    /// </summary>
    [Fact]
    public void Compute_WithRfc8439AppendixA3Vector6_MatchesExpectedTag()
    {
        byte[] key = BuildKey(
            "02000000000000000000000000000000",
            "ffffffffffffffffffffffffffffffff");
        byte[] message = HexToBytes("02000000000000000000000000000000");
        byte[] expectedTag = HexToBytes("03000000000000000000000000000000");

        byte[] tag = Poly1305.Compute(key, message);

        Assert.Equal(expectedTag, tag);
    }

    /// <summary>
    /// RFC 8439 Appendix A.3, Test Vector #7: data limb all-ones with carry from lower limb.
    /// </summary>
    [Fact]
    public void Compute_WithRfc8439AppendixA3Vector7_MatchesExpectedTag()
    {
        byte[] key = BuildKey(
            "01000000000000000000000000000000",
            "00000000000000000000000000000000");
        byte[] message = HexToBytes(
            "ffffffffffffffffffffffffffffffff" +
            "f0ffffffffffffffffffffffffffffff" +
            "11000000000000000000000000000000");
        byte[] expectedTag = HexToBytes("05000000000000000000000000000000");

        byte[] tag = Poly1305.Compute(key, message);

        Assert.Equal(expectedTag, tag);
    }

    /// <summary>
    /// RFC 8439 Appendix A.3, Test Vector #8: polynomial part equals exactly 2^130-5.
    /// </summary>
    [Fact]
    public void Compute_WithRfc8439AppendixA3Vector8_MatchesExpectedTag()
    {
        byte[] key = BuildKey(
            "01000000000000000000000000000000",
            "00000000000000000000000000000000");
        byte[] message = HexToBytes(
            "ffffffffffffffffffffffffffffffff" +
            "fbfefefefefefefefefefefefefefefe" +
            "01010101010101010101010101010101");
        byte[] expectedTag = new byte[Poly1305.TagSize];

        byte[] tag = Poly1305.Compute(key, message);

        Assert.Equal(expectedTag, tag);
    }

    /// <summary>
    /// RFC 8439 Appendix A.3, Test Vector #9: polynomial part equals exactly 2^130-6.
    /// </summary>
    [Fact]
    public void Compute_WithRfc8439AppendixA3Vector9_MatchesExpectedTag()
    {
        byte[] key = BuildKey(
            "02000000000000000000000000000000",
            "00000000000000000000000000000000");
        byte[] message = HexToBytes("fdffffffffffffffffffffffffffffff");
        byte[] expectedTag = HexToBytes("faffffffffffffffffffffffffffffff");

        byte[] tag = Poly1305.Compute(key, message);

        Assert.Equal(expectedTag, tag);
    }

    /// <summary>
    /// RFC 8439 Appendix A.3, Test Vector #10: 5*H+L-type reduction, 131-bit intermediate.
    /// </summary>
    [Fact]
    public void Compute_WithRfc8439AppendixA3Vector10_MatchesExpectedTag()
    {
        byte[] key = BuildKey(
            "01000000000000000400000000000000",
            "00000000000000000000000000000000");
        byte[] message = HexToBytes(
            "e33594d7505e43b90000000000000000" +
            "3394d7505e4379cd0100000000000000" +
            "00000000000000000000000000000000" +
            "01000000000000000000000000000000");
        byte[] expectedTag = HexToBytes("14000000000000005500000000000000");

        byte[] tag = Poly1305.Compute(key, message);

        Assert.Equal(expectedTag, tag);
    }

    /// <summary>
    /// RFC 8439 Appendix A.3, Test Vector #11: 5*H+L-type reduction, 131-bit final result.
    /// </summary>
    [Fact]
    public void Compute_WithRfc8439AppendixA3Vector11_MatchesExpectedTag()
    {
        byte[] key = BuildKey(
            "01000000000000000400000000000000",
            "00000000000000000000000000000000");
        byte[] message = HexToBytes(
            "e33594d7505e43b90000000000000000" +
            "3394d7505e4379cd0100000000000000" +
            "00000000000000000000000000000000");
        byte[] expectedTag = HexToBytes("13000000000000000000000000000000");

        byte[] tag = Poly1305.Compute(key, message);

        Assert.Equal(expectedTag, tag);
    }

    [Fact]
    public void Compute_WithSameInput_IsDeterministic()
    {
        byte[] key = HexToBytes("85d6be7857556d337f4452fe42d506a80103808afb0db2fd4abff6af4149f51b");
        byte[] message = Encoding.ASCII.GetBytes("Cryptographic Forum Research Group");
        byte[] tag1 = Poly1305.Compute(key, message);
        byte[] tag2 = Poly1305.Compute(key, message);

        Assert.Equal(Poly1305.TagSize, tag1.Length);
        Assert.Equal(tag1, tag2);
        Assert.NotEqual(new byte[Poly1305.TagSize], tag1);
    }

    [Fact]
    public void UpdateFinalizeTag_WithChunkedInput_MatchesOneShotCompute()
    {
        byte[] key = HexToBytes("85d6be7857556d337f4452fe42d506a80103808afb0db2fd4abff6af4149f51b");
        byte[] message = Encoding.ASCII.GetBytes("Cryptographic Forum Research Group");

        byte[] oneShot = Poly1305.Compute(key, message);
        byte[] incremental = new byte[Poly1305.TagSize];

        Poly1305 poly = new(key);
        try
        {
            poly.Update(message.AsSpan(0, 10));
            poly.Update(message.AsSpan(10, 7));
            poly.Update(message.AsSpan(17));
            poly.FinalizeTag(incremental);
        }
        finally
        {
            poly.Clear();
        }

        Assert.Equal(oneShot, incremental);
    }

    [Fact]
    public void Verify_WithValidAndTamperedTag_ReturnsTrueThenFalse()
    {
        byte[] key = HexToBytes("85d6be7857556d337f4452fe42d506a80103808afb0db2fd4abff6af4149f51b");
        byte[] message = Encoding.ASCII.GetBytes("Cryptographic Forum Research Group");
        byte[] tag = Poly1305.Compute(key, message);

        Assert.True(Poly1305.Verify(key, message, tag));

        tag[0] ^= 0xFF;
        Assert.False(Poly1305.Verify(key, message, tag));
    }

    [Fact]
    public void Verify_WhenTagLengthIsInvalid_ThrowsArgumentException()
    {
        byte[] key = new byte[Poly1305.KeySize];
        byte[] message = [1, 2, 3];
        byte[] invalidTag = new byte[Poly1305.TagSize - 1];

        _ = Assert.Throws<ArgumentException>(() => Poly1305.Verify(key, message, invalidTag));
    }

    [Fact]
    public void Compute_WhenKeyOrDestinationIsInvalid_ThrowsArgumentException()
    {
        byte[] invalidKey = new byte[Poly1305.KeySize - 1];
        byte[] message = [1, 2, 3];
        byte[] tooSmallDestination = new byte[Poly1305.TagSize - 1];

        _ = Assert.Throws<ArgumentException>(() => Poly1305.Compute(invalidKey, message, new byte[Poly1305.TagSize]));
        _ = Assert.Throws<ArgumentException>(() => Poly1305.Compute(new byte[Poly1305.KeySize], message, tooSmallDestination));
    }

    [Fact]
    public void FinalizeTag_WhenCalledTwiceOrFollowedByUpdate_ThrowsInvalidOperationException()
    {
        byte[] key = new byte[Poly1305.KeySize];
        byte[] output = new byte[Poly1305.TagSize];

        Poly1305 poly = new(key);
        try
        {
            poly.Update([1, 2, 3]);
            poly.FinalizeTag(output);

            bool threw1 = false;
            try { poly.FinalizeTag(output); }
            catch (InvalidOperationException) { threw1 = true; }
            Assert.True(threw1);

            bool threw2 = false;
            try { poly.Update([4, 5]); }
            catch (InvalidOperationException) { threw2 = true; }
            Assert.True(threw2);
        }
        finally
        {
            poly.Clear();
        }
    }

    [Fact]
    public void Operations_AfterClear_ThrowObjectDisposedException()
    {
        byte[] key = new byte[Poly1305.KeySize];
        byte[] output = new byte[Poly1305.TagSize];

        Poly1305 poly = new(key);
        poly.Clear();

        bool threw1 = false;
        try { poly.ComputeTag([1, 2], output); }
        catch (ObjectDisposedException) { threw1 = true; }
        Assert.True(threw1);

        bool threw2 = false;
        try { poly.Update([1, 2]); }
        catch (ObjectDisposedException) { threw2 = true; }
        Assert.True(threw2);

        bool threw3 = false;
        try { poly.FinalizeTag(output); }
        catch (ObjectDisposedException) { threw3 = true; }
        Assert.True(threw3);
    }

    [Fact]
    public void Compute_WithRandomInput_MatchesBouncyCastle()
    {
        byte[] key = GenerateRandom(32);
        byte[] message = GenerateRandom(1024);

        // Nalix
        byte[] nalixTag = Nalix.Codec.Security.Hashing.Poly1305.Compute(key, message);

        // BouncyCastle
        var bc = new BCPoly1305();
        bc.Init(new KeyParameter(key));
        bc.BlockUpdate(message, 0, message.Length);
        byte[] bcTag = new byte[16];
        bc.DoFinal(bcTag, 0);

        Assert.Equal(bcTag, nalixTag);
    }
}
