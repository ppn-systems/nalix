// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Net.Sockets;
using System.Net;
using System.Reflection;
using Nalix.Abstractions;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Security;
using Nalix.Codec.DataFrames;
using Nalix.Codec.ProtocolFrames;
using Nalix.Codec.Security;
using Nalix.Codec.Transforms;
using Nalix.Environment.Hashing;
using Nalix.Environment.Memory;
using Nalix.Environment.Sequencing;
using Nalix.Framework.Injection;
using Nalix.Hosting;
using Nalix.SDK.Options;
using Nalix.SDK.Transport;
using Nalix.SDK.Transport.Extensions;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Nalix.SDK.Tests.TestSupport;

namespace Nalix.SDK.Tests.Transport.Internal;

public sealed class FramePipelineTests
{
    [Fact]
    public void TryProcessInbound_WhenDecompressFails_DoesNotLeakSequenceNumber()
    {
        byte[] secret = new byte[32];
        secret.AsSpan().Fill(0x42);

        // Craft an inner frame with COMPRESSED flag set, but invalid LZ4 payload
        byte[] malformedInner = new byte[PacketHeader.Size + 8];
        malformedInner[2] = (byte)PacketFlags.COMPRESSED; // Flags offset = 2
        // Set uncompressed length in LZ4 header to something invalid (negative or mismatched)
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(malformedInner.AsSpan(PacketHeader.Size, 4), 1024);

        using BufferLease plainLease = BufferLease.CopyFrom(malformedInner);
        IBufferLease encrypted = FrameCipher.EncryptFrame(plainLease, secret, seq: 99u, CipherSuiteType.Chacha20Poly1305);
        try
        {
            bool ok = FramePipeline.TryProcessInbound(ref encrypted, secret, CipherSuiteType.Chacha20Poly1305, out uint? seq);
            Assert.False(ok);
            Assert.Null(seq);
        }
        finally
        {
            encrypted.Dispose();
        }
    }
}
