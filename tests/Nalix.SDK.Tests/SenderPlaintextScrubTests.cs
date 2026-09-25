#if DEBUG
// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using Nalix.Abstractions.Security;
using Nalix.Environment.Memory;
using Nalix.SDK.Options;
using Nalix.SDK.Transport;
using Nalix.SDK.Transport.Internal.Tcp;
using Nalix.SDK.Transport.Internal.Ws;
using Xunit;

namespace Nalix.SDK.Tests;

/// <summary>
/// The pool no longer clears arrays on return (#370), so a lease that carries the plaintext of an
/// encrypted frame must be marked for scrubbing before any step of the send can fail. These tests
/// force a failure before the pipeline runs its own marking (encrypt requested, no cipher suite)
/// and hold an extra reference so the flag can be observed after the sender releases the lease.
/// </summary>
public sealed class SenderPlaintextScrubTests
{
    [Fact]
    public void TcpFrameSender_EncryptFailsEarly_MarksPlaintextLeaseForScrubbing()
    {
        TransportOptions options = new() { Algorithm = CipherSuiteType.None, CompressionEnabled = false };
        using TcpFrameSender sender = new(() => null!, options, new SessionState(), (_, _) => { });

        BufferLease lease = NewPlaintextLease();
        lease.Retain();

        Assert.False(sender.Send(lease, encrypt: true));
        Assert.True(lease.ZeroOnDispose);
        lease.Dispose();
    }

    [Fact]
    public void WsFrameSender_EncryptFailsEarly_MarksPlaintextLeaseForScrubbing()
    {
        TransportOptions options = new() { Algorithm = CipherSuiteType.None, CompressionEnabled = false };
        using WsFrameSender sender = new(() => null!, options, new SessionState(), (_, _) => { });

        BufferLease lease = NewPlaintextLease();
        lease.Retain();

        Assert.False(sender.Send(lease, encrypt: true));
        Assert.True(lease.ZeroOnDispose);
        lease.Dispose();
    }

    [Fact]
    public void TcpFrameSender_Unencrypted_DoesNotForceScrubbing()
    {
        TransportOptions options = new() { Algorithm = CipherSuiteType.None, CompressionEnabled = false };
        using TcpFrameSender sender = new(() => null!, options, new SessionState(), (_, _) => { });

        BufferLease lease = NewPlaintextLease();
        lease.Retain();

        _ = sender.Send(lease, encrypt: false);
        Assert.False(lease.ZeroOnDispose);
        lease.Dispose();
    }

    private static BufferLease NewPlaintextLease()
    {
        byte[] frame = new byte[64];
        frame.AsSpan(16).Fill(0x5A);
        return BufferLease.CopyFrom(frame);
    }
}
#endif
