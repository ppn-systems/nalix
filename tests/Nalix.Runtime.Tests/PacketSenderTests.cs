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
using Nalix.Framework.Memory.Objects;
using Nalix.Runtime.Dispatching;
using Xunit;

namespace Nalix.Runtime.Tests;

public sealed class PacketSenderTests
{
    [Fact]
    public async Task ReplyAsync_EchoesRequestSequenceIdOntoResponse()
    {
        FakeConnection connection = new();
        TestPacket request = new() { Header = new PacketHeader { SequenceId = 42 } };
        TestPacket response = new() { Header = new PacketHeader { SequenceId = 0 } };

        PacketSender sender = new();
        sender.Initialize(new FakeContext(connection, request));

        await sender.ReplyAsync(response);

        Assert.Equal((ushort)42, response.Header.SequenceId);
    }

    private sealed class FakeContext(FakeConnection connection, TestPacket packet) : IPacketContext<TestPacket>
    {
        public bool IsReliable => true;
        public bool EncryptedOnWire => false;
        public bool SkipOutbound => false;
        public TestPacket Packet => packet;
        public IConnection Connection => connection;
        public PacketMetadata Attributes => new(
            opCode: new PacketOpcodeAttribute((ushort)1),
            timeout: null,
            permission: null,
            encryption: null,
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
        public bool IsApproachingOverflow(uint margin = 1_000_000) => false;
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
        public CipherSuiteType Algorithm { get; set; }
        public IConnection.ITransport TCP => FakeTcp;
        public IConnection.ITransport? UDP => null;
        public event EventHandler<IConnectionEventArgs>? ConnectionClosed;
        public event EventHandler<IConnectionEventArgs>? MessageProcessing;
        public event EventHandler<IConnectionEventArgs>? MessageProcessed;
        public void Disconnect(string? reason = null) { }
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
