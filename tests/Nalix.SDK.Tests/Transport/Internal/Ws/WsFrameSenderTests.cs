// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using Nalix.Abstractions.Security;
using Nalix.Environment.Memory;
using Nalix.SDK.Options;
using Nalix.SDK.Transport;
using Nalix.SDK.Transport.Internal.Ws;
using Xunit;

namespace Nalix.SDK.Tests.Transport.Internal.Ws;

/// <summary>
/// See <c>TcpFrameSenderTests</c>: a lease holding the plaintext of an encrypted frame must be marked for
/// scrubbing even when the send fails before the pipeline runs.
/// </summary>
public sealed class WsFrameSenderTests
{
    [Fact]
    public void Send_WhenEncryptFailsEarly_MarksPlaintextLeaseForScrubbing()
    {
        TransportOptions options = new() { Algorithm = CipherSuiteType.None, CompressionEnabled = false };
        using WsFrameSender sender = new(() => null!, options, new SessionState(), (_, _) => { });

        byte[] frame = new byte[64];
        frame.AsSpan(16).Fill(0x5A);
        BufferLease lease = BufferLease.CopyFrom(frame);
        lease.Retain();

        Assert.False(sender.Send(lease, encrypt: true));
        Assert.True(lease.ZeroOnDispose);
        lease.Dispose();
    }
}
