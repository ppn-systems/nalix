// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.SDK.Tests.TestSupport;
using Nalix.SDK.Transport.Extensions;

namespace Nalix.SDK.Tests.Transport.Extensions;

public sealed class HandshakeExtensionsTimeoutTests
{
    [Fact]
    public async Task HandshakeAsync_WhenConnectTimeoutIsZeroAndServerSilent_KeepsWaitingInsteadOfCancelling()
    {
        using RaisableFakeSession session = new();
        session.EnsureRegistry();
        session.Options.ServerPublicKey = new string('0', 64);
        session.Options.ConnectTimeoutMillis = 0; // documented as "no timeout"

        using CancellationTokenSource caller = new();
        Task handshake = session.HandshakeAsync(caller.Token).AsTask();

        Task winner = await Task.WhenAny(handshake, Task.Delay(500));

        // Fail on the pre-fix behavior: CancelAfter(0) cancels immediately, so the handshake is already faulted.
        Assert.NotSame(handshake, winner);

        await caller.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handshake);
    }
}
