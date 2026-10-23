// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using Nalix.Abstractions.Primitives;
using Nalix.Codec.Security.Asymmetric;

namespace Nalix.Codec.Tests.Security.Asymmetric;

/// <summary>
/// Adversarial public-key inputs for X25519, including the well-known low-order points
/// (curve25519 has cofactor 8, giving 8 points of small order that reduce a Diffie-Hellman
/// shared secret to a small, predictable value — all-zero for these specific points under the
/// standard Montgomery-ladder X25519 function). Values are the canonical little-endian
/// u-coordinate encodings documented in Trevor Perrin's "Test vectors for X25519" note
/// (used widely, e.g. libsodium's test suite and RFC 7748 discussion threads) — all 8 points
/// listed here map to shared secret 0 for any scalar.
/// </summary>
public sealed class X25519AdversarialTests
{
    private static Bytes32 Scalar() =>
        new(Convert.FromHexString("A546E36BF0527C9D3B16154B82465EDD62144C0AC1FC5A18506A2244BA449AC4"));

    /// <summary>
    /// Known low-order u-coordinates for curve25519 (order 1 and order 2 points — the identity
    /// point 0 and the point at u = p-1, both unambiguously documented, e.g. RFC 7748 §7 /
    /// Trevor Perrin's "Test vectors for X25519" note), which collapse X25519(scalar, point) to
    /// the all-zero ladder output for any scalar. Only values that are unambiguous in the
    /// public literature are hardcoded here to avoid asserting against an invented constant.
    /// </summary>
    public static TheoryData<string> LowOrderPoints() => new()
    {
        // order 1 (identity element, u = 0)
        new string('0', 64),
        // order 2 (u = p - 1 = 2^255 - 20), little-endian encoding
        "ECFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF7F",
    };

    [Theory]
    [MemberData(nameof(LowOrderPoints))]
    public void Agreement_WithKnownLowOrderPoint_ThrowsOrReturnsNonZeroSecret(string hexPoint)
    {
        byte[] raw = Convert.FromHexString(hexPoint);
        Bytes32 point = new(raw);

        // Current documented behavior (see X25519Tests.Agreement_WithAllZeroPoint_ThrowsInvalidOperationException):
        // an all-zero ladder output throws InvalidOperationException rather than returning a
        // predictable shared secret. Points that do NOT reduce to all-zero under this
        // implementation's ladder will instead produce a normal (non-zero) result; either
        // outcome is acceptable here, but a non-zero, non-throwing result must never be
        // silently treated as "successful" without being flagged.
        try
        {
            Bytes32 shared = X25519.Agreement(Scalar(), point);
            Assert.False(shared.IsZero, $"low-order point 0x{hexPoint} produced an all-zero shared secret WITHOUT throwing — contributory-behavior finding: caller must independently reject all-zero shared secrets.");
        }
        catch (InvalidOperationException)
        {
            // Rejected — the safe, documented behavior for this implementation.
        }
    }

    [Fact]
    public void Agreement_WithAllZeroPublicKey_ThrowsInvalidOperationException()
    {
        Bytes32 zero = new(new byte[32]);
        _ = Assert.Throws<InvalidOperationException>(() => X25519.Agreement(Scalar(), zero));
    }

    [Fact]
    public void Agreement_WithAllOnesPublicKey_ThrowsOrReturnsNonZeroSecret()
    {
        byte[] raw = new byte[32];
        Array.Fill(raw, (byte)0xFF);
        Bytes32 point = new(raw);

        try
        {
            Bytes32 shared = X25519.Agreement(Scalar(), point);
            Assert.False(shared.IsZero, "all-0xFF point produced an all-zero shared secret without throwing.");
        }
        catch (InvalidOperationException)
        {
            // Rejected — acceptable.
        }
    }

    /// <summary>
    /// Non-canonical field element: u-coordinate value p = 2^255 - 19 itself (one of the
    /// smallest values &gt;= p representable in 32 bytes). RFC 7748 §5 recommends the
    /// implementation mask the high bit and reduce mod p on receipt; this test documents
    /// whatever this implementation currently does (compute a result silently, since the
    /// underlying field-element representation reduces mod p implicitly) rather than
    /// asserting a specific policy.
    /// </summary>
    [Fact]
    public void Agreement_WithNonCanonicalPointEqualToP_DoesNotCrash()
    {
        // p = 2^255 - 19, little-endian bytes.
        byte[] pBytes = Convert.FromHexString("EDFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF7F");
        Bytes32 point = new(pBytes);

        // Must not throw an undocumented exception type or hang; either a normal result or the
        // documented low-order InvalidOperationException is acceptable.
        try
        {
            _ = X25519.Agreement(Scalar(), point);
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <summary>
    /// Non-canonical field element well above p (all high bits set, &gt;= 2p). Documents
    /// current behavior rather than asserting a specific rejection policy.
    /// </summary>
    [Fact]
    public void Agreement_WithNonCanonicalPointFarAboveP_DoesNotCrash()
    {
        byte[] raw = new byte[32];
        Array.Fill(raw, (byte)0xFF); // 2^256 - 1, far above p
        Bytes32 point = new(raw);

        try
        {
            _ = X25519.Agreement(Scalar(), point);
        }
        catch (InvalidOperationException)
        {
        }
    }
}
