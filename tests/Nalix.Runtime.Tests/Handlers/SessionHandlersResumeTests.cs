// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using Nalix.Abstractions;
using Nalix.Abstractions.Identity;
using Nalix.Framework.Identifiers;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Sessions;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Security;
using Nalix.Codec.ProtocolFrames;
using Nalix.Codec.Security.Hashing;
using Nalix.Environment.Sequencing;
using Nalix.Environment.Time;
using Nalix.Framework.Injection;
using Nalix.Framework.Memory.Objects;
using Nalix.Runtime.Dispatching;
using Nalix.Runtime.Handlers;
using Nalix.Runtime.Sessions;
using Nalix.Runtime.Tests.TestSupport;
using Xunit;

namespace Nalix.Runtime.Tests.Handlers;

[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
public sealed class SessionHandlersResumeTests : IDisposable
{
    private const uint SavedSendSequence = 7;
    private const uint SavedReceiveSequence = 10;
    private const uint TcpWindow = 32;

    private readonly InMemorySessionStore _store = new();
    private readonly SessionService _sessionService;
    private readonly ulong _sessionToken = Snowflake.NewId(SnowflakeType.Session).ToUInt64();

    public SessionHandlersResumeTests()
    {
        _sessionService = new SessionService(store: _store);
        InstanceManager.Instance.Register<ISessionService>(_sessionService);
        SessionHandlers.__InitializeDependencies();
    }

    public void Dispose()
    {
        _ = InstanceManager.Instance.RemoveInstance(typeof(ISessionService));
        _sessionService.Dispose();
    }

    [Fact]
    public async Task HandleAsync_WhenResumed_RejectsFramesReceivedBeforeResume()
    {
        Bytes32 secret = NewSecret(0xA5);
        SequenceCounter receive = new();
        receive.UpdateTo(SavedReceiveSequence);
        FakeConnection connection = new(new FakeTransport(new SequenceCounter(), receive));

        await StoreSessionAsync(secret);
        await ResumeAsync(connection, secret);

        // Frames at or below the last accepted sequence were already processed before the resume.
        for (uint seq = 1; seq <= SavedReceiveSequence; seq++)
        {
            Assert.False(connection.Transport.ReceiveSequence.IsValid(seq, window: TcpWindow), $"seq {seq} must be rejected as replay");
        }

        Assert.True(connection.Transport.ReceiveSequence.IsValid(SavedReceiveSequence + 1, window: TcpWindow));
    }

    [Fact]
    public async Task HandleAsync_WhenResumed_ContinuesSendSequenceFromSnapshot()
    {
        Bytes32 secret = NewSecret(0x5A);
        FakeConnection connection = new(new FakeTransport(new SequenceCounter(), new SequenceCounter()));

        await StoreSessionAsync(secret);
        await ResumeAsync(connection, secret);

        Assert.Equal(SavedSendSequence, connection.Transport.SendSequence.Current());
        Assert.Equal(SavedSendSequence + 1, connection.Transport.SendSequence.Next());
    }

    private static Bytes32 NewSecret(byte first)
    {
        byte[] bytes = new byte[Bytes32.Size];
        bytes[0] = first;
        bytes[1] = 0x42;
        return new Bytes32(bytes);
    }

    private async Task StoreSessionAsync(Bytes32 secret)
    {
        ObjectMap<AttributeKey, object> attributes = ObjectMap<AttributeKey, object>.Rent();
        attributes[ConnectionAttributes.SequenceState] = new ConnectionSequenceState
        {
            TcpSendSequence = SavedSendSequence,
            TcpReceiveSequence = SavedReceiveSequence,
        };

        SessionEntry entry = new(new SessionSnapshot
        {
            SessionToken = _sessionToken,
            Secret = secret,
            Algorithm = CipherSuiteType.Chacha20Poly1305,
            ExpiresAtUnixMilliseconds = long.MaxValue,
            Attributes = attributes,
        }, connectionId: 0UL);

        await _store.StoreAsync(entry);
    }

    private async Task ResumeAsync(FakeConnection connection, Bytes32 secret)
    {
        using SessionResume request = new();
        request.Initialize(SessionResumeStage.REQUEST, _sessionToken);

        Span<byte> messageBytes = stackalloc byte[16];
        Span<byte> proofBytes = stackalloc byte[32];
        BinaryPrimitives.WriteUInt64LittleEndian(messageBytes, _sessionToken);
        BinaryPrimitives.WriteInt64LittleEndian(messageBytes[8..], Clock.UnixSecondsNow() / 30);
        HmacKeccak256.Compute(secret.AsSpan(), messageBytes, proofBytes);
        request.Proof = new Bytes32(proofBytes);

        PacketContext<SessionResume> context = PacketContextFactory.Create(request, connection, encryptedOnWire: false);
        await SessionHandlers.HandleAsync(context);
    }
}
