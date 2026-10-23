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
using Nalix.SDK.Transport.Internal.Tcp;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Nalix.SDK.Tests.TestSupport;

namespace Nalix.SDK.Tests.Transport.Internal.Tcp;

[Collection(TestCollections.RealServer)]
public sealed class TcpFrameReaderTests
{
    [Fact]
    public void ProcessNormalFrame_WhenSequenceIsRejected_DoesNotUpdateSequenceCounter()
    {
        // TcpFrameReader caches its SequenceOptions instance in a static readonly field the first time the type is
        // used. Mutating a fresh ConfigurationManager.Get<SequenceOptions>() result would miss that instance whenever
        // the configuration was reloaded after the reader type was initialized, so change the exact object the reader
        // reads and restore it afterwards.
        FieldInfo optionsField = typeof(TcpFrameReader).GetField("s_sequenceOptions", BindingFlags.Static | BindingFlags.NonPublic)!;
        Nalix.Environment.Options.SequenceOptions seqOpts = (Nalix.Environment.Options.SequenceOptions)optionsField.GetValue(null)!;
        uint prevTcpWindow = seqOpts.TcpWindow;
        seqOpts.TcpWindow = 0;
        try
        {
            TransportOptions options = new() { Algorithm = CipherSuiteType.Chacha20Poly1305 };
            using TcpSession session = new(options);
            int delivered = 0;
            session.OnMessageReceived += (_, _) =>
            {
                delivered++;
            };

            Span<byte> secretBytes = stackalloc byte[32];
            secretBytes.Fill(0x33);
            session.State.Secret = new Bytes32(secretBytes);
            session.State.EncryptionEnabled = true;

            object reader = typeof(TcpSession).GetField("_reader", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
            MethodInfo processNormal = reader.GetType().GetMethod("PROCESS_NORMAL_FRAME", BindingFlags.Instance | BindingFlags.NonPublic)!;
            SequenceCounter recvSeq = (SequenceCounter)reader.GetType().GetProperty("Sequence", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(reader)!;

            byte[] validPlain = new byte[PacketHeader.Size + 4];

            using (BufferLease plain10 = BufferLease.CopyFrom(validPlain))
            {
                IBufferLease enc10 = FrameCipher.EncryptFrame(plain10, session.State.Secret.AsSpan(), seq: 10u, CipherSuiteType.Chacha20Poly1305);
                processNormal.Invoke(reader, [enc10]);
            }

            Assert.Equal(1, delivered);
            Assert.Equal(10u, recvSeq.Current());

            // Now send seq = 8. Since TcpWindow == 0 (strict ordering), IsValid(8, 0) returns false.
            // The rejected packet must NOT call UpdateTo(8) in the finally block!
            using (BufferLease plain8 = BufferLease.CopyFrom(validPlain))
            {
                IBufferLease enc8 = FrameCipher.EncryptFrame(plain8, session.State.Secret.AsSpan(), seq: 8u, CipherSuiteType.Chacha20Poly1305);
                processNormal.Invoke(reader, [enc8]);
            }

            Assert.Equal(1, delivered);
            Assert.True(recvSeq.IsValid(8u, window: 32), "Rejected packet must not mark bit in SequenceCounter via finally block.");
        }
        finally
        {
            seqOpts.TcpWindow = prevTcpWindow;
        }
    }
}
