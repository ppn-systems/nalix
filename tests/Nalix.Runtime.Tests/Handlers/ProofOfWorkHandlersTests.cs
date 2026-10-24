// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using FluentAssertions;
using Nalix.Abstractions.Security;
using Nalix.Codec.ProtocolFrames;
using Nalix.Runtime.Dispatching;
using Nalix.Runtime.Handlers;
using Nalix.Runtime.Tests.TestSupport;
using Xunit;

namespace Nalix.Runtime.Tests.Handlers;

public sealed class ProofOfWorkHandlersTests
{
    [Fact]
    public void HandleAsync_WhenAlreadyVerified_IgnoresRepeatedOrInvalidProofWithoutDisconnecting()
    {
        using FakeConnection established = new() { Level = PermissionLevel.ESTABLISHED, IdleTimeoutMs = 15_000 };

        ProofOfWorkProof replayOrInvalidProof = new();

        using PacketContext<ProofOfWorkProof> ctx = PacketContextFactory.Create(replayOrInvalidProof, established);
        ProofOfWorkHandlers.HandleAsync(ctx);

        established.Disconnected.Should().BeFalse("an already POW_VERIFIED / ESTABLISHED connection must ignore redundant POW_PROOF frames without running PoW verification or disconnecting");
        established.Level.Should().Be(PermissionLevel.ESTABLISHED);
    }
}
