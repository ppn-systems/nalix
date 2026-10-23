// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Linq;
using System.Threading.Tasks;
using Nalix.Abstractions.Security;
using Nalix.Codec.Security;

namespace Nalix.Codec.Tests.Security;

/// <summary>
/// Concurrency stress test for <see cref="Nalix.Codec.Security.EnvelopeCipher"/>, which is documented as stateless and
/// safe for concurrent use: a single shared key is exercised from many threads.
/// </summary>
[Trait("Category", "Stress")]
public sealed class EnvelopeCipherConcurrencyTests
{
    private static byte[] SequentialBytes(int length, int start = 0)
    {
        byte[] data = new byte[length];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(start + i);
        }

        return data;
    }

    [Theory]
    [InlineData(CipherSuiteType.Chacha20Poly1305)]
    [InlineData(CipherSuiteType.Salsa20Poly1305)]
    public void EncryptDecrypt_With10kRoundTripsAcrossEightThreads_RoundTripsEveryPayload(CipherSuiteType suite)
    {
        const int threadCount = 8;
        const int opsPerThread = 1250; // 8 * 1250 = 10,000
        byte[] key = SequentialBytes(32, 1);

        int size = EnvelopeCipher.HeaderSize + EnvelopeCipher.GetNonceLength(suite)
                   + 32 + EnvelopeCipher.GetTagLength(suite);

        Parallel.For(0, threadCount, threadIndex =>
        {
            for (int i = 0; i < opsPerThread; i++)
            {
                byte[] plaintext = SequentialBytes(32, threadIndex * 1000 + i);
                byte[] envelope = new byte[size];
                byte[] decrypted = new byte[32];

                EnvelopeCipher.Encrypt(key, plaintext, envelope, seq: (uint)(threadIndex * opsPerThread + i), suite, out int written);
                EnvelopeCipher.Decrypt(key, new ReadOnlySpan<byte>(envelope, 0, written), decrypted, suite, out int decWritten, out _);

                Assert.Equal(plaintext.Length, decWritten);
                Assert.True(plaintext.SequenceEqual(decrypted),
                    $"thread={threadIndex} op={i}: round-trip mismatch under concurrent EnvelopeCipher use.");
            }
        });
    }
}
