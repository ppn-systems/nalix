// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using Nalix.Abstractions.Security;
using Nalix.Codec.Security;
using Xunit;

namespace Nalix.Codec.Tests.Security;

/// <summary>
/// <c>EnvelopeCipher.TryDecrypt</c> must reject an attacker-controlled NONCE_LEN that does not match the suite
/// instead of letting the primitive throw.
/// </summary>
public sealed class EnvelopeCipherNonceLengthTests
{
    private const int NonceLenOffset = 7;

    [Theory]
    [InlineData(CipherSuiteType.Chacha20Poly1305, 13)]
    [InlineData(CipherSuiteType.Chacha20Poly1305, 11)]
    [InlineData(CipherSuiteType.Salsa20Poly1305, 9)]
    [InlineData(CipherSuiteType.Chacha20, 8)]
    [InlineData(CipherSuiteType.Chacha20, 13)]
    [InlineData(CipherSuiteType.Salsa20, 12)]
    public void TryDecrypt_NonceLengthDiffersFromSuite_ReturnsFalseWithoutThrowing(CipherSuiteType suite, int forgedNonceLen)
    {
        byte[] key = new byte[32];
        for (int i = 0; i < key.Length; i++)
        {
            key[i] = (byte)(i + 1);
        }

        byte[] plaintext = new byte[24];
        int realNonce = EnvelopeCipher.GetNonceLength(suite);
        int tag = EnvelopeCipher.GetTagLength(suite);
        byte[] valid = new byte[EnvelopeCipher.HeaderSize + realNonce + plaintext.Length + tag];
        EnvelopeCipher.Encrypt(key, plaintext, valid, seq: 7u, suite, out int written);

        // Re-frame the body with a forged nonce length so the blob stays long enough for the declared layout.
        byte[] forged = new byte[EnvelopeCipher.HeaderSize + forgedNonceLen + plaintext.Length + tag];
        Array.Copy(valid, forged, Math.Min(written, forged.Length));
        forged[NonceLenOffset] = (byte)forgedNonceLen;

        byte[] output = new byte[plaintext.Length + 16];
        bool ok = false;
        Exception? thrown = Record.Exception(() => ok = EnvelopeCipher.TryDecrypt(key, forged, output, suite, out _, out _));

        Assert.Null(thrown);
        Assert.False(ok);
    }
}
