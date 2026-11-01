// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Exceptions;
using Nalix.Codec.Transforms;

namespace Nalix.Codec.Tests.Audit;

/// <summary>
/// Reproduction tests for confirmed findings from the Nalix.Codec security review
/// (see docs/audit/codec-security.review.md).
/// </summary>
[Trait("Category", "Audit")]
public sealed class CodecSecurityReviewAuditTests
{
    /// <summary>
    /// <see cref="FrameTransformer.GetPlaintextLength"/> guards with <c>envelope.Length &lt; Offset</c>,
    /// while <see cref="FrameTransformer.TryGetPlaintextLength"/> guards with <c>&lt;= Offset</c>.
    /// An input of exactly <c>Offset</c> bytes (header only, no nonce or ciphertext) therefore
    /// passes the first guard and reaches the envelope parser, which throws <c>CipherException</c>
    /// instead of the <c>SourceTooSmall</c> path used for shorter inputs.
    /// </summary>
    [Fact]
    public void GetPlaintextLength_WhenEnvelopeIsExactlyHeaderOffset_ThrowsSourceTooSmall()
    {
        byte[] headerOnly = new byte[FrameTransformer.Offset];

        _ = Assert.Throws<InternalErrorException>(() => FrameTransformer.GetPlaintextLength(headerOnly));
    }
}
