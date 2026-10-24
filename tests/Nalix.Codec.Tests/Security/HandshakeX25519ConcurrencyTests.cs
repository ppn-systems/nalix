// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Linq;
using System.Threading.Tasks;
using Nalix.Abstractions.Security;
using Nalix.Codec.Security;

namespace Nalix.Codec.Tests.Security;

/// <summary>
/// Concurrency stress test for <see cref="Nalix.Codec.Security.HandshakeX25519"/>, whose public methods are pure
/// functions with no shared mutable state.
/// </summary>
[Trait("Category", "Stress")]
public sealed class HandshakeX25519ConcurrencyTests
{
    /// <summary>
    /// <see cref="HandshakeX25519"/>'s public methods are pure functions with no shared mutable
    /// state; verify parallel handshake derivations from independent key material never produce
    /// torn/cross-contaminated results.
    /// </summary>
    [Fact]
    public void DeriveRekeySecret_UnderConcurrentLoad_IsConsistentAcrossThreads()
    {
        const int threadCount = 8;
        const int opsPerThread = 500; // 8 * 500 = 4,000

        Parallel.For(0, threadCount, threadIndex =>
        {
            for (int i = 0; i < opsPerThread; i++)
            {
                byte[] raw = new byte[32];
                for (int b = 0; b < 32; b++)
                {
                    raw[b] = (byte)(threadIndex * 31 + i + b);
                }

                Nalix.Abstractions.Primitives.Bytes32 secret = new(raw);
                Nalix.Abstractions.Primitives.Bytes32 expected = HandshakeX25519.DeriveRekeySecret(secret);
                Nalix.Abstractions.Primitives.Bytes32 actual = HandshakeX25519.DeriveRekeySecret(secret);

                Assert.True(expected.AsSpan().SequenceEqual(actual.AsSpan()),
                    $"thread={threadIndex} op={i}: HandshakeX25519.DeriveRekeySecret produced inconsistent output under concurrent load.");
            }
        });
    }
}
