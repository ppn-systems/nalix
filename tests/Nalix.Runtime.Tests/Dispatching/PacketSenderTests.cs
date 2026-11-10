// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions;
using Nalix.Abstractions.Injection;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Security;
using Nalix.Codec.Transforms;
using Nalix.Environment.Memory;
using Nalix.Framework.Memory.Objects;
using Nalix.Runtime.Dispatching;
using Xunit;

namespace Nalix.Runtime.Tests.Dispatching;

public sealed class PacketSenderTests
{
    [Fact]
    public async Task ReplyAsync_WithRequestSequenceId_EchoesItOntoResponse()
    {
        FakeConnection connection = new();
        TestPacket request = new() { Header = new PacketHeader { SequenceId = 42 } };
        TestPacket response = new() { Header = new PacketHeader { SequenceId = 0 } };

        PacketSender sender = new();
        sender.Initialize(new FakeContext(connection, request));

        await sender.ReplyAsync(response);

        Assert.Equal((ushort)42, response.Header.SequenceId);
    }

    [Fact]
    public async Task SendAsync_WithBatch_SendsEveryPacketInOrder()
    {
        FakeConnection connection = new();
        PacketSender sender = new();
        sender.Initialize(new FakeContext(connection, new TestPacket()));

        MarkerPacket[] packets = [new(1), new(2), new(3), new(4)];

        await sender.SendAsync<MarkerPacket>(packets);

        Assert.Equal(4, connection.FakeTcp.SentMessages.Count);
        for (int i = 0; i < packets.Length; i++)
        {
            Assert.Equal(MarkerPacket.Size, connection.FakeTcp.SentMessages[i].Length);
            Assert.Equal((byte)(i + 1), connection.FakeTcp.SentMessages[i][^1]);
        }
    }

    [Fact]
    public async Task SendAsync_WithBatch_HoldsSendLockOnceForWholeBatch()
    {
        FakeConnection connection = new();
        PacketSender sender = new();
        sender.Initialize(new FakeContext(connection, new TestPacket()));

        await sender.SendAsync<MarkerPacket>([new(1), new(2), new(3)]);

        Assert.Equal(1, connection.FakeTcp.LockAcquisitions);
        Assert.Equal(1, connection.FakeTcp.LockReleases);
    }

    [Fact]
    public async Task SendAsync_WithEmptyBatch_SendsNothing()
    {
        FakeConnection connection = new();
        PacketSender sender = new();
        sender.Initialize(new FakeContext(connection, new TestPacket()));

        await sender.SendAsync(ReadOnlySpan<MarkerPacket>.Empty);

        Assert.Empty(connection.FakeTcp.SentMessages);
    }

    [Fact]
    public async Task ReplyAsync_WithBatch_EchoesRequestSequenceIdOntoEveryResponse()
    {
        FakeConnection connection = new();
        TestPacket request = new() { Header = new PacketHeader { SequenceId = 42 } };
        PacketSender sender = new();
        sender.Initialize(new FakeContext(connection, request));

        MarkerPacket[] responses = [new(1), new(2), new(3)];

        await sender.ReplyAsync<MarkerPacket>(responses);

        Assert.All(responses, static r => Assert.Equal((ushort)42, r.Header.SequenceId));
        Assert.Equal(3, connection.FakeTcp.SentMessages.Count);
    }

    [Fact]
    public async Task SendAsync_WithBatch_OnDefaultInterfaceImplementation_SendsOneByOne()
    {
        LoopingSender sender = new();

        await ((IPacketSender)sender).SendAsync<MarkerPacket>([new(1), new(2)]);
        await ((IPacketSender)sender).ReplyAsync<MarkerPacket>([new(3)]);

        Assert.Equal(2, sender.Sent);
        Assert.Equal(1, sender.Replied);
    }

    [Fact]
    public async Task SendAsync_WithEncryptedBatch_UsesConsecutiveSequencesAndEveryFrameDecrypts()
    {
        FakeConnection connection = CreateEncryptedConnection();
        PacketSender sender = new();
        sender.Initialize(new FakeContext(connection, new TestPacket(), encrypted: true));

        await sender.SendAsync<MarkerPacket>([new(1), new(2), new(3), new(4), new(5)]);

        Assert.Equal(5, connection.FakeTcp.SentMessages.Count);
        for (int i = 0; i < 5; i++)
        {
            IBufferLease frame = BufferLease.CopyFrom(connection.FakeTcp.SentMessages[i]);
            IBufferLease original = frame;
            try
            {
                Assert.True(FramePipeline.TryProcessInbound(ref frame, connection.Secret.AsSpan(), connection.Algorithm, out uint? seq));
                Assert.Equal((uint)(i + 1), seq);
                Assert.Equal(MarkerPacket.Size, frame.Length);
                Assert.Equal((byte)(i + 1), frame.Span[^1]);
            }
            finally
            {
                if (!ReferenceEquals(frame, original))
                {
                    frame.Dispose();
                }

                original.Dispose();
            }
        }
    }

    [Fact]
    public async Task SendAsync_WithBatch_WhenPacketFailsToSerialize_SendsNothingAndNeverTakesLock()
    {
        FakeConnection connection = new();
        PacketSender sender = new();
        sender.Initialize(new FakeContext(connection, new TestPacket()));

        IPacket[] packets = [new MarkerPacket(1), new ThrowingPacket(), new MarkerPacket(3)];

        _ = await Assert.ThrowsAsync<InvalidOperationException>(async () => await sender.SendAsync<IPacket>(packets));

        Assert.Empty(connection.FakeTcp.SentMessages);
        Assert.Equal(0, connection.FakeTcp.LockAcquisitions);
    }

    [Fact]
    public async Task SendAsync_WithBatch_WhenPacketIsNull_Throws()
    {
        FakeConnection connection = new();
        PacketSender sender = new();
        sender.Initialize(new FakeContext(connection, new TestPacket()));

        IPacket[] packets = [new MarkerPacket(1), null!];

        _ = await Assert.ThrowsAsync<ArgumentNullException>(async () => await sender.SendAsync<IPacket>(packets));

        Assert.Empty(connection.FakeTcp.SentMessages);
    }

    [Fact]
    public async Task SendAsync_WithBatch_WhenTransformFailsMidBatch_SendsEarlierFramesThenThrowsAndReleasesLock()
    {
        FakeConnection connection = CreateEncryptedConnection();
        // The cipher is read once per frame: the third frame finds none configured.
        connection.AlgorithmReadsBeforeNone = 2;
        PacketSender sender = new();
        sender.Initialize(new FakeContext(connection, new TestPacket(), encrypted: true));

        _ = await Assert.ThrowsAnyAsync<Exception>(async () => await sender.SendAsync<MarkerPacket>([new(1), new(2), new(3), new(4)]));

        Assert.Equal(2, connection.FakeTcp.SentMessages.Count);
        Assert.Equal(1, connection.FakeTcp.LockAcquisitions);
        Assert.Equal(1, connection.FakeTcp.LockReleases);
    }

    [Fact]
    public async Task SendAsync_WithBatch_WhenSequenceApproachesOverflow_SendsPreparedFramesAndDisconnects()
    {
        FakeConnection connection = CreateEncryptedConnection();
        ((FakeSequenceCounter)connection.FakeTcp.SendSequence).OverflowAfter = 2;
        PacketSender sender = new();
        sender.Initialize(new FakeContext(connection, new TestPacket(), encrypted: true));

        await sender.SendAsync<MarkerPacket>([new(1), new(2), new(3), new(4)]);

        Assert.Equal(2, connection.FakeTcp.SentMessages.Count);
        Assert.Equal(1, connection.Disconnects);
        Assert.Equal(1, connection.FakeTcp.LockReleases);
    }

    [Fact]
    public async Task SendAsync_WithBatch_OverUdp_SendsEveryDatagramWithItsTag()
    {
        FakeConnection connection = new() { FakeUdp = new FakeTransport() };
        PacketSender sender = new();
        sender.Initialize(new FakeContext(connection, new TestPacket(), isReliable: false));

        await sender.SendAsync<MarkerPacket>([new(1), new(2), new(3)]);

        Assert.Empty(connection.FakeTcp.SentMessages);
        Assert.Equal(3, connection.FakeUdp.SentMessages.Count);
        for (int i = 0; i < 3; i++)
        {
            // Payload followed by the 4-byte datagram tag.
            Assert.Equal(MarkerPacket.Size + sizeof(uint), connection.FakeUdp.SentMessages[i].Length);
            Assert.Equal((byte)(i + 1), connection.FakeUdp.SentMessages[i][MarkerPacket.Size - 1]);
        }
    }

    [Fact]
    public async Task SendAsync_WithSinglePacketBatch_SendsIt()
    {
        FakeConnection connection = new();
        PacketSender sender = new();
        sender.Initialize(new FakeContext(connection, new TestPacket()));

        await sender.SendAsync<MarkerPacket>([new(7)]);

        byte[] message = Assert.Single(connection.FakeTcp.SentMessages);
        Assert.Equal((byte)7, message[^1]);
    }

    private static FakeConnection CreateEncryptedConnection()
    {
        byte[] key = new byte[32];
        for (int i = 0; i < key.Length; i++)
        {
            key[i] = (byte)(i + 1);
        }

        return new FakeConnection { Secret = new Bytes32(key), Algorithm = CipherSuiteType.Chacha20Poly1305 };
    }

    private sealed class ThrowingPacket : IPacket
    {
        public int Length => 16;
        public PacketHeader Header { get; set; }
        public byte[] Serialize() => throw new InvalidOperationException("serialize failed");
        public int Serialize(Span<byte> buffer) => throw new InvalidOperationException("serialize failed");
    }

    private sealed class LoopingSender : IPacketSender
    {
        public int Sent { get; private set; }
        public int Replied { get; private set; }
        public void Initialize<TPacket>(IPacketContext<TPacket> context) where TPacket : IPacket { }
        public void ResetForPool() { }
        public ValueTask SendAsync(IPacket packet, CancellationToken ct = default) { Sent++; return ValueTask.CompletedTask; }
        public ValueTask SendAsync(IPacket packet, bool forceEncrypt, CancellationToken ct = default) { Sent++; return ValueTask.CompletedTask; }
        public ValueTask ReplyAsync(IPacket response, CancellationToken ct = default) { Replied++; return ValueTask.CompletedTask; }
    }

    private sealed class MarkerPacket(byte marker) : IPacket
    {
        public const int Size = 32;
        public int Length => Size;
        public PacketHeader Header { get; set; }
        public byte[] Serialize() => throw new NotSupportedException();
        public int Serialize(Span<byte> buffer)
        {
            buffer[..Size].Clear();
            buffer[Size - 1] = marker;
            return Size;
        }
    }

    private sealed class FakeContext(FakeConnection connection, TestPacket packet, bool encrypted = false, bool isReliable = true) : IPacketContext<TestPacket>
    {
        public bool IsReliable => isReliable;
        public bool EncryptedOnWire => false;
        public bool SkipOutbound => false;
        public TestPacket Packet => packet;
        public IConnection Connection => connection;
        public PacketMetadata Attributes => new(
            opCode: new PacketOpcodeAttribute((ushort)1),
            timeout: null,
            permission: null,
            encryption: encrypted ? new PacketEncryptionAttribute() : null,
            rateLimit: null,
            transport: null);
        public IPacketSender Sender => null!;
        public IPacketScope Scope => null!;
        public CancellationToken CancellationToken => default;
        public void ResetForPool() { }
    }

    private sealed class FakeSequenceCounter : ISequenceCounter
    {
        private uint _value;
        public uint Next() => ++_value;
        public uint Current() => _value;
        public void Reset(uint newValue = 0) => _value = newValue;
        public bool IsValid(uint? receivedSeq, uint window = 0) => true;
        public void UpdateTo(uint receivedSeq) => _value = receivedSeq;
        public void ResumeFrom(uint lastKnownSeq, uint safetyGap = 1000) => _value = lastKnownSeq;
        public uint OverflowAfter { get; set; } = uint.MaxValue;
        public bool IsApproachingOverflow(uint margin = 1_000_000) => _value >= OverflowAfter;
    }

    private sealed class FakeTransport : IConnection.ITransport
    {
        public System.Collections.Generic.List<byte[]> SentMessages { get; } = [];
        public TransportFraming Framing => TransportFraming.UInt16LengthPrefixed;
        public ISequenceCounter SendSequence { get; } = new FakeSequenceCounter();
        public ISequenceCounter ReceiveSequence { get; } = new FakeSequenceCounter();
        public uint NextSendSequence() => SendSequence.Next();
        public uint NextReceiveSequence() => ReceiveSequence.Next();
        public uint CurrentSendSequence => SendSequence.Current();
        public uint CurrentReceiveSequence => ReceiveSequence.Current();
        public void Send(ReadOnlySpan<byte> message) => SentMessages.Add(message.ToArray());
        public ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
        {
            SentMessages.Add(message.ToArray());
            return ValueTask.CompletedTask;
        }
        public void BeginReceive(CancellationToken cancellationToken = default) { }
        public void UseFraming(TransportFraming framing) { }

        public int LockAcquisitions { get; private set; }
        public int LockReleases { get; private set; }

        public ValueTask<IAsyncDisposable> AcquireSendLockAsync(CancellationToken cancellationToken = default)
        {
            LockAcquisitions++;
            return ValueTask.FromResult<IAsyncDisposable>(new Scope(this));
        }

        private sealed class Scope(FakeTransport owner) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                owner.LockReleases++;
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class FakeConnection : IConnection
    {
        public FakeTransport FakeTcp { get; } = new();
        public bool IsDisposed { get; private set; }
        public bool IsUdpCreated => false;
        public ulong ConnectionId => 1;
        public string? UserId { get; set; }
        public long UpTime => 0;
        public long LastPingTime => 0;
        public bool ExcludeFromIdleTimeout { get; set; }
        public IOpCodeExtractor PacketClassifier => null!;
        public INetworkEndpoint NetworkEndpoint => null!;
        public IObjectMap<AttributeKey, object> Attributes { get; } = ObjectMap<AttributeKey, object>.Rent();
        public ConcurrentDictionary<ushort, object> RateLimitCache { get; } = new();
        public Bytes32 Secret { get; set; }
        public PermissionLevel Level { get; set; }
        private CipherSuiteType _algorithm;
        public int AlgorithmReadsBeforeNone { get; set; } = int.MaxValue;
        public CipherSuiteType Algorithm
        {
            get => AlgorithmReadsBeforeNone-- > 0 ? _algorithm : CipherSuiteType.None;
            set => _algorithm = value;
        }
        public FakeTransport? FakeUdp { get; init; }
        public int Disconnects { get; private set; }
        public IConnection.ITransport TCP => FakeTcp;
        public IConnection.ITransport? UDP => FakeUdp;
        public event EventHandler<IConnectionEventArgs>? ConnectionClosed;
        public event EventHandler<IConnectionEventArgs>? MessageProcessing;
        public event EventHandler<IConnectionEventArgs>? MessageProcessed;
        public void Disconnect(string? reason = null) => Disconnects++;
        public void Dispose() => IsDisposed = true;
        public int ErrorCount => 0;
        public void IncrementErrorCount() { }
        public int IdleTimeoutMs { get; set; } = 60000;
        public void UpdateIdleTimeout(int newTimeoutMs) => IdleTimeoutMs = newTimeoutMs;
    }

    private sealed class TestPacket : IPacket
    {
        public int Length => 0;
        public PacketHeader Header { get; set; }
        public byte[] Serialize() => [];
        public int Serialize(Span<byte> buffer) => 0;
    }
}
