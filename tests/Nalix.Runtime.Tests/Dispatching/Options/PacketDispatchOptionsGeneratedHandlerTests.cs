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
using Nalix.Runtime.Tests.TestSupport;
using Xunit;

namespace Nalix.Runtime.Tests.Dispatching.Options;

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
[Collection(TestCollections.ProcessWideServices)]
public sealed class PacketDispatchOptionsGeneratedHandlerTests : IDisposable
{
    private static PacketDispatchOptions<EchoPacket> BuildDispatcher(EchoController controller)
        => new PacketDispatchOptions<EchoPacket>().WithHandler(controller);

    private static EchoPacket MakePacket(ushort opCode) => new() { Header = new PacketHeader { OpCode = opCode, SequenceId = 1 } };

    [Fact]
    public async Task ExecuteResolvedHandlerAsync_WithVoidHandler_SendsNoResponse()
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
    public async Task ExecuteResolvedHandlerAsync_WithValueTaskHandler_AwaitsButSendsNoResponse()
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
    public async Task ExecuteResolvedHandlerAsync_WithTaskHandler_AwaitsButSendsNoResponse()
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
    public async Task ExecuteResolvedHandlerAsync_WithValueTaskOfTHandler_AwaitsResultAndSendsResponse()
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
    public async Task ExecuteResolvedHandlerAsync_WithTaskOfTHandler_AwaitsResultAndSendsResponse()
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
    public async Task ExecuteResolvedHandlerAsync_WithContextHandlerReturningValueTaskOfT_AwaitsResultAndSendsResponse()
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
    public async Task ExecuteResolvedHandlerAsync_WithAsyncThrowingHandler_SendsFailDirectiveInsteadOfSwallowing()
    {
        EchoController controller = new();
        PacketDispatchOptions<EchoPacket> options = BuildDispatcher(controller);
        FakeConnection connection = new();

        _ = options.TryResolveHandler(EchoController.ThrowingOpCode, out PacketHandler<EchoPacket> descriptor);
        await options.ExecuteResolvedHandlerAsync(descriptor, MakePacket(EchoController.ThrowingOpCode), connection, reliable: true, encryptedOnWire: false);

        connection.FakeTcp.SentMessages.Should().NotBeEmpty(
            "an exception thrown asynchronously inside a generated handler must be converted to a FAIL control, not swallowed silently");
    }

    [Fact]
    public async Task ExecuteResolvedHandlerAsync_WithSyncValueTaskOfTHandler_SendsResponseViaFastPath()
    {
        EchoController controller = new();
        PacketDispatchOptions<EchoPacket> options = BuildDispatcher(controller);
        FakeConnection connection = new();

        _ = options.TryResolveHandler(EchoController.SyncValueTaskOfTOpCode, out PacketHandler<EchoPacket> descriptor);
        await options.ExecuteResolvedHandlerAsync(descriptor, MakePacket(EchoController.SyncValueTaskOfTOpCode), connection, reliable: true, encryptedOnWire: false);

        connection.FakeTcp.SentMessages.Should().NotBeEmpty(
            "a synchronous ValueTask<T> handler must return result and send response via fast-path");
    }

    [Fact]
    public async Task ExecuteResolvedHandlerAsync_WithSyncTaskOfTHandler_SendsResponseViaFastPath()
    {
        EchoController controller = new();
        PacketDispatchOptions<EchoPacket> options = BuildDispatcher(controller);
        FakeConnection connection = new();

        _ = options.TryResolveHandler(EchoController.SyncTaskOfTOpCode, out PacketHandler<EchoPacket> descriptor);
        await options.ExecuteResolvedHandlerAsync(descriptor, MakePacket(EchoController.SyncTaskOfTOpCode), connection, reliable: true, encryptedOnWire: false);

        connection.FakeTcp.SentMessages.Should().NotBeEmpty(
            "a completed Task<T> handler must return result and send response via fast-path");
    }

    [Fact]
    public async Task ExecuteResolvedHandlerAsync_WithSyncDirectValueHandler_SendsResponseViaFastPath()
    {
        EchoController controller = new();
        PacketDispatchOptions<EchoPacket> options = BuildDispatcher(controller);
        FakeConnection connection = new();

        _ = options.TryResolveHandler(EchoController.SyncDirectValueOpCode, out PacketHandler<EchoPacket> descriptor);
        await options.ExecuteResolvedHandlerAsync(descriptor, MakePacket(EchoController.SyncDirectValueOpCode), connection, reliable: true, encryptedOnWire: false);

        connection.FakeTcp.SentMessages.Should().NotBeEmpty(
            "a synchronous direct IPacket handler must return result and send response");
    }

    [Fact]
    public async Task ExecuteResolvedHandlerAsync_WithSyncThrowingHandler_SendsFailDirectiveInsteadOfSwallowing()
    {
        EchoController controller = new();
        PacketDispatchOptions<EchoPacket> options = BuildDispatcher(controller);
        FakeConnection connection = new();

        _ = options.TryResolveHandler(EchoController.SyncThrowingOpCode, out PacketHandler<EchoPacket> descriptor);
        await options.ExecuteResolvedHandlerAsync(descriptor, MakePacket(EchoController.SyncThrowingOpCode), connection, reliable: true, encryptedOnWire: false);

        connection.FakeTcp.SentMessages.Should().NotBeEmpty(
            "an exception thrown synchronously inside a generated handler must be converted to a FAIL control");
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
    public void TryResolveHandler_WithTwoParameterHandler_ReturnsFalse()
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
    public async Task ExecuteResolvedHandlerAsync_WithStreamHandlerAndBridgeContext_KeepsContextValidAfterFirstAwait()
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
    public async Task ExecuteResolvedHandlerAsync_WithFromScopeParameter_ResolvesServiceAndExecutes()
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
    public async Task ExecuteResolvedHandlerAsync_WithMultipleFromScopeParameters_ResolvesAllServicesInOrder()
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

    public void Dispose() => ScopedServiceRegistry.Instance.Clear();
}
