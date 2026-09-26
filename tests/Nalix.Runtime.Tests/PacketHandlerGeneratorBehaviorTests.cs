// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nalix.Abstractions;
using Nalix.Abstractions.Injection;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Security;
using Nalix.Codec.DataFrames;
using Nalix.Framework.Memory.Objects;
using Nalix.Runtime.Dispatching;
using Nalix.Runtime.Internal.Compilation;
using Nalix.Runtime.Routing;
using Xunit;

namespace Nalix.Runtime.Tests;

/// <summary>
/// End-to-end tests that dispatch real packets through source-generated
/// <see cref="PacketHandlerAttribute"/> controllers, verifying the actual generated code
/// (not just that it compiles) awaits async results and sends responses.
/// <para>
/// This class specifically targets the regression at
/// analyzers/Nalix.Analyzers.Generators/PacketHandlerGenerator.cs:435, where
/// <c>ReturnTypeStr.StartsWith("System.Threading.Tasks.ValueTask")</c> never matched because
/// <c>ReturnTypeStr</c> is fully-qualified (<c>global::System.Threading.Tasks.ValueTask&lt;T&gt;</c>),
/// causing <c>Task&lt;T&gt;</c>/<c>ValueTask&lt;T&gt;</c> handlers to emit <c>return handler(...)</c>
/// instead of <c>return await handler(...)</c> — silently never sending a response.
/// </para>
/// </summary>
#if DEBUG
public sealed class PacketHandlerGeneratorBehaviorTests
{
    private static PacketDispatchOptions<EchoPacket> BuildDispatcher(EchoController controller)
        => new PacketDispatchOptions<EchoPacket>().WithHandler(controller);

    private static EchoPacket MakePacket(ushort opCode) => new() { Header = new PacketHeader { OpCode = opCode, SequenceId = 1 } };

    [Fact]
    public async Task VoidHandler_DoesNotSendResponse()
    {
        EchoController controller = new();
        PacketDispatchOptions<EchoPacket> options = BuildDispatcher(controller);
        FakeConnection connection = new();

        _ = options.TryResolveHandler(EchoController.VoidOpCode, out PacketHandler<EchoPacket> descriptor);
        await options.ExecuteResolvedHandlerAsync(descriptor, MakePacket(EchoController.VoidOpCode), connection, reliable: true, encryptedOnWire: false);

        controller.VoidInvoked.Should().BeTrue();
        connection.FakeTcp.SentMessages.Should().BeEmpty("a void handler never produces an outbound payload");
    }

    [Fact]
    public async Task ValueTaskHandler_AwaitsButSendsNoResponse()
    {
        EchoController controller = new();
        PacketDispatchOptions<EchoPacket> options = BuildDispatcher(controller);
        FakeConnection connection = new();

        _ = options.TryResolveHandler(EchoController.ValueTaskOpCode, out PacketHandler<EchoPacket> descriptor);
        await options.ExecuteResolvedHandlerAsync(descriptor, MakePacket(EchoController.ValueTaskOpCode), connection, reliable: true, encryptedOnWire: false);

        controller.ValueTaskInvoked.Should().BeTrue("the generated invoker must await the ValueTask before returning");
        connection.FakeTcp.SentMessages.Should().BeEmpty();
    }

    [Fact]
    public async Task TaskHandler_AwaitsButSendsNoResponse()
    {
        EchoController controller = new();
        PacketDispatchOptions<EchoPacket> options = BuildDispatcher(controller);
        FakeConnection connection = new();

        _ = options.TryResolveHandler(EchoController.TaskOpCode, out PacketHandler<EchoPacket> descriptor);
        await options.ExecuteResolvedHandlerAsync(descriptor, MakePacket(EchoController.TaskOpCode), connection, reliable: true, encryptedOnWire: false);

        controller.TaskInvoked.Should().BeTrue();
        connection.FakeTcp.SentMessages.Should().BeEmpty();
    }

    /// <summary>
    /// The regression test: a <c>ValueTask&lt;EchoPacket&gt;</c> handler only round-trips a
    /// response if the generated invoker does <c>return await handler(...)</c>. If the
    /// StartsWith/Contains check in PacketHandlerGenerator.cs regresses, the generated code
    /// instead does <c>return handler(...)</c>, which returns an unawaited
    /// <c>ValueTask&lt;EchoPacket&gt;</c> boxed as <c>object</c> — never an <see cref="IPacket"/> —
    /// so <c>SendHandlerResponseAsync</c>'s <c>result is IPacket</c> check fails and nothing is sent.
    /// This test fails under that regression and passes with the Contains-based fix.
    /// </summary>
    [Fact]
    public async Task ValueTaskOfTHandler_AwaitsResultAndSendsResponse()
    {
        EchoController controller = new();
        PacketDispatchOptions<EchoPacket> options = BuildDispatcher(controller);
        FakeConnection connection = new();

        _ = options.TryResolveHandler(EchoController.ValueTaskOfTOpCode, out PacketHandler<EchoPacket> descriptor);
        await options.ExecuteResolvedHandlerAsync(descriptor, MakePacket(EchoController.ValueTaskOfTOpCode), connection, reliable: true, encryptedOnWire: false);

        connection.FakeTcp.SentMessages.Should().NotBeEmpty(
            "a ValueTask<T> handler's result must be awaited and the resulting packet actually sent");
    }

    [Fact]
    public async Task TaskOfTHandler_AwaitsResultAndSendsResponse()
    {
        EchoController controller = new();
        PacketDispatchOptions<EchoPacket> options = BuildDispatcher(controller);
        FakeConnection connection = new();

        _ = options.TryResolveHandler(EchoController.TaskOfTOpCode, out PacketHandler<EchoPacket> descriptor);
        await options.ExecuteResolvedHandlerAsync(descriptor, MakePacket(EchoController.TaskOfTOpCode), connection, reliable: true, encryptedOnWire: false);

        connection.FakeTcp.SentMessages.Should().NotBeEmpty(
            "a Task<T> handler's result must be awaited and the resulting packet actually sent");
    }

    [Fact]
    public async Task GenericContextHandler_ReceivesConcretePacketAndAwaitsValueTaskOfT()
    {
        EchoController controller = new();
        PacketDispatchOptions<EchoPacket> options = BuildDispatcher(controller);
        FakeConnection connection = new();

        _ = options.TryResolveHandler(EchoController.ContextOpCode, out PacketHandler<EchoPacket> descriptor);
        await options.ExecuteResolvedHandlerAsync(descriptor, MakePacket(EchoController.ContextOpCode), connection, reliable: true, encryptedOnWire: false);

        controller.ContextInvoked.Should().BeTrue();
        connection.FakeTcp.SentMessages.Should().NotBeEmpty(
            "a context-style ValueTask<T> handler must also be awaited and its result sent");
    }

    [Fact]
    public async Task ThrowingHandler_ExceptionSurfacesAsFailDirective_NotSwallowed()
    {
        EchoController controller = new();
        PacketDispatchOptions<EchoPacket> options = BuildDispatcher(controller);
        FakeConnection connection = new();

        _ = options.TryResolveHandler(EchoController.ThrowingOpCode, out PacketHandler<EchoPacket> descriptor);
        await options.ExecuteResolvedHandlerAsync(descriptor, MakePacket(EchoController.ThrowingOpCode), connection, reliable: true, encryptedOnWire: false);

        connection.FakeTcp.SentMessages.Should().NotBeEmpty(
            "an exception thrown asynchronously inside a generated handler must be converted to a FAIL control, not swallowed silently");
    }

    /// <summary>
    /// Single-shape policy: only a single <c>IPacketContext&lt;T&gt;</c> parameter is a supported
    /// handler signature. A legacy 2-parameter <c>(TPacket packet, IConnection connection)</c> shape
    /// is now reported at compile time as NALIX003 by <c>NalixUsageAnalyzer</c> (see
    /// <c>HasSupportedParameterSignature</c>) — the generator itself still skips it without
    /// registering an invoker, but the caller is no longer left with a silent failure: a diagnostic
    /// is surfaced during the build, not just an unresolved handler at dispatch time.
    /// </summary>
    [Fact]
    public void TwoParameterHandler_IsSkippedAtGeneration_ButFlaggedByAnalyzer()
    {
        EchoController controller = new();
        PacketDispatchOptions<EchoPacket> options = BuildDispatcher(controller);

        bool resolved = options.TryResolveHandler(EchoController.TwoParamOpCode, out PacketHandler<EchoPacket> descriptor);

        resolved.Should().BeFalse("a 2-parameter handler is not a supported shape and must not be registered; " +
            "NalixUsageAnalyzer reports NALIX003 for this signature at compile time");
    }

    /// <summary>
    /// #391 regression test: a handler with a bridge (generic) context returning
    /// <c>IAsyncEnumerable&lt;EchoPacket&gt;</c> must still have a valid, non-reused context by the
    /// time its body actually runs (after the first <c>await</c> inside the iterator). Before the
    /// fix, the generator's synchronous <c>try/finally</c> returned the bridge context to the object
    /// pool the instant this method was CALLED — before the iterator body ever executed — so this
    /// test either crashed (uncontended: a cleared context) or silently sent nothing, since the
    /// dispatch pipeline swallows the resulting exception. With the fix
    /// (<see cref="Nalix.Runtime.Dispatching.PacketContextBridge.WrapStream{TConcrete, TChunk}"/>),
    /// the context stays rented for the whole enumeration and the handler's own request SequenceId
    /// round-trips correctly.
    /// </summary>
    [Fact]
    public async Task StreamHandlerWithBridgeContext_ContextStaysValidAfterFirstAwait()
    {
        EchoController controller = new();
        PacketDispatchOptions<EchoPacket> options = BuildDispatcher(controller);
        FakeConnection connection = new();

        const ushort requestSeq = 4242;
        _ = options.TryResolveHandler(EchoController.StreamOpCode, out PacketHandler<EchoPacket> descriptor);
        await options.ExecuteResolvedHandlerAsync(
            descriptor,
            new EchoPacket { Header = new PacketHeader { OpCode = EchoController.StreamOpCode, SequenceId = requestSeq } },
            connection,
            reliable: true,
            encryptedOnWire: false);

        connection.FakeTcp.SentMessages.Should().HaveCount(1,
            "the stream handler's single yielded item must reach the wire — a cleared or reused pooled context " +
            "before the fix would throw inside the iterator (reading context.Packet.Header.SequenceId AFTER the " +
            "first await), which the dispatch pipeline swallows, silently sending nothing");
    }

    [Fact]
    public async Task FromScopeHandler_ResolvesServiceAndExecutesSuccessfully()
    {
        EchoController controller = new();
        PacketDispatchOptions<EchoPacket> options = BuildDispatcher(controller);
        FakeConnection connection = new();

        TestScopedEchoService service = new();
        ScopedServiceRegistry.Instance.RegisterScoped<IScopedEchoService>(_ => service);

        _ = options.TryResolveHandler(EchoController.FromScopeOpCode, out PacketHandler<EchoPacket> descriptor);
        await options.ExecuteResolvedHandlerAsync(descriptor, MakePacket(EchoController.FromScopeOpCode), connection, reliable: true, encryptedOnWire: false);

        controller.FromScopeInvoked.Should().BeTrue();
        controller.InjectedService.Should().BeSameAs(service);
        connection.FakeTcp.SentMessages.Should().NotBeEmpty();
    }

    [Fact]
    public async Task MultipleFromScopeHandler_ResolvesAllServicesInOrder()
    {
        EchoController controller = new();
        PacketDispatchOptions<EchoPacket> options = BuildDispatcher(controller);
        FakeConnection connection = new();

        TestScopedEchoService service1 = new();
        TestScopedEchoService2 service2 = new();
        ScopedServiceRegistry.Instance.RegisterScoped<IScopedEchoService>(_ => service1);
        ScopedServiceRegistry.Instance.RegisterScoped<IScopedEchoService2>(_ => service2);

        _ = options.TryResolveHandler(EchoController.MultipleFromScopeOpCode, out PacketHandler<EchoPacket> descriptor);
        await options.ExecuteResolvedHandlerAsync(descriptor, MakePacket(EchoController.MultipleFromScopeOpCode), connection, reliable: true, encryptedOnWire: false);

        controller.FromScopeInvoked.Should().BeTrue();
        controller.InjectedService.Should().BeSameAs(service1);
        controller.InjectedService2.Should().BeSameAs(service2);
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
}
#endif

/// <summary>
/// Test-only packet handled by <see cref="EchoController"/>, real generator output flows through it.
/// </summary>
public sealed partial class EchoPacket : PacketBase<EchoPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 9998;

    public static new EchoPacket Deserialize(ReadOnlySpan<byte> buffer) => PacketBase<EchoPacket>.Deserialize(buffer);
}

/// <summary>
/// Test-only controller scanned by the real <c>PacketHandlerGenerator</c>. Every return-type
/// shape the generator supports is represented here so the generated invoker code is exercised
/// end-to-end (not merely compiled).
/// </summary>
[PacketHandler]
public sealed class EchoController
{
    public const ushort VoidOpCode = 0x2101;
    public const ushort ValueTaskOpCode = 0x2102;
    public const ushort TaskOpCode = 0x2103;
    public const ushort ValueTaskOfTOpCode = 0x2104;
    public const ushort TaskOfTOpCode = 0x2105;
    public const ushort ContextOpCode = 0x2106;
    public const ushort ThrowingOpCode = 0x2107;
    public const ushort StreamOpCode = 0x210B;

    public bool VoidInvoked { get; private set; }
    public bool ValueTaskInvoked { get; private set; }
    public bool TaskInvoked { get; private set; }
    public bool ContextInvoked { get; private set; }

    // Note: the generator only recognizes single-parameter handlers whose parameter
    // is a generic IPacketContext<TPacket> (see PacketHandlerGenerator.cs
    // GET_CONTROLLER_MODEL: `if (method.Parameters.Length != 1) continue;`). A
    // two-parameter (packet, connection) shape is silently skipped — no diagnostic,
    // no handler registered — so every handler here uses the context-style shape.

    [PacketOpcode(VoidOpCode)]
    public void HandleVoid(IPacketContext<EchoPacket> context) => VoidInvoked = true;

    [PacketOpcode(ValueTaskOpCode)]
    public ValueTask HandleValueTask(IPacketContext<EchoPacket> context)
    {
        ValueTaskInvoked = true;
        return ValueTask.CompletedTask;
    }

    [PacketOpcode(TaskOpCode)]
    public Task HandleTask(IPacketContext<EchoPacket> context)
    {
        TaskInvoked = true;
        return Task.CompletedTask;
    }

    [PacketOpcode(ValueTaskOfTOpCode)]
    public async ValueTask<EchoPacket> HandleValueTaskOfT(IPacketContext<EchoPacket> context)
    {
        await Task.Yield();
        return new EchoPacket { Header = new PacketHeader { OpCode = ValueTaskOfTOpCode, SequenceId = context.Packet.Header.SequenceId } };
    }

    [PacketOpcode(TaskOfTOpCode)]
    public async Task<EchoPacket> HandleTaskOfT(IPacketContext<EchoPacket> context)
    {
        await Task.Yield();
        return new EchoPacket { Header = new PacketHeader { OpCode = TaskOfTOpCode, SequenceId = context.Packet.Header.SequenceId } };
    }

    [PacketOpcode(ContextOpCode)]
    public async ValueTask<EchoPacket> HandleContext(IPacketContext<EchoPacket> context)
    {
        ContextInvoked = true;
        await Task.Yield();
        return new EchoPacket { Header = new PacketHeader { OpCode = ContextOpCode, SequenceId = context.Packet.Header.SequenceId } };
    }

    [PacketOpcode(ThrowingOpCode)]
    public async ValueTask HandleThrowing(IPacketContext<EchoPacket> context)
    {
        await Task.Yield();
        throw new InvalidOperationException("intentional test failure");
    }

    // #391 regression coverage: an IAsyncEnumerable<T>-returning handler with a bridge (generic)
    // context runs its body lazily — nothing in it executes until the first MoveNextAsync — so this
    // handler deliberately reads context.Packet.Header.SequenceId AFTER an `await Task.Yield()`
    // inside the iterator, exactly the shape that observed a cleared or reused pooled context before
    // the fix (the bridge context was returned to the pool the instant this method was CALLED, long
    // before the first yield ran).
    [PacketOpcode(StreamOpCode)]
    public static async IAsyncEnumerable<EchoPacket> HandleStream(IPacketContext<EchoPacket> context)
    {
        ushort requestSeq = context.Packet.Header.SequenceId;
        await Task.Yield();
        yield return new EchoPacket { Header = new PacketHeader { OpCode = StreamOpCode, SequenceId = requestSeq } };
    }

    public const ushort TwoParamOpCode = 0x2108;

    // Legacy (packet, connection) two-parameter shape. See the comment above: the generator
    // silently skips this — no diagnostic, no registration — per PacketHandlerGenerator.cs:243.
    [PacketOpcode(TwoParamOpCode)]
    public void HandleTwoParam(EchoPacket packet, IConnection connection) => VoidInvoked = true;

    public const ushort FromScopeOpCode = 0x2109;
    public const ushort MultipleFromScopeOpCode = 0x210A;

    public bool FromScopeInvoked { get; private set; }
    public IScopedEchoService? InjectedService { get; private set; }
    public IScopedEchoService2? InjectedService2 { get; private set; }

    [PacketOpcode(FromScopeOpCode)]
    public async ValueTask<EchoPacket> HandleFromScope(
        IPacketContext<EchoPacket> context,
        [FromScope] IScopedEchoService service)
    {
        FromScopeInvoked = true;
        InjectedService = service;
        await Task.Yield();
        return new EchoPacket { Header = new PacketHeader { OpCode = FromScopeOpCode, SequenceId = context.Packet.Header.SequenceId } };
    }

    [PacketOpcode(MultipleFromScopeOpCode)]
    public async ValueTask HandleMultipleFromScope(
        IPacketContext<EchoPacket> context,
        [FromScope] IScopedEchoService service1,
        [FromScope] IScopedEchoService2 service2)
    {
        FromScopeInvoked = true;
        InjectedService = service1;
        InjectedService2 = service2;
        await Task.Yield();
    }
}

public interface IScopedEchoService
{
    Guid Id { get; }
}

public sealed class TestScopedEchoService : IScopedEchoService
{
    public Guid Id { get; } = Guid.NewGuid();
}

public interface IScopedEchoService2
{
    Guid Id { get; }
}

public sealed class TestScopedEchoService2 : IScopedEchoService2
{
    public Guid Id { get; } = Guid.NewGuid();
}
