// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Codec.DataFrames;
using Nalix.Runtime.Routing;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Injection;

namespace Nalix.Runtime.Tests.TestSupport;

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
    public const ushort SyncValueTaskOfTOpCode = 0x210C;
    public const ushort SyncTaskOfTOpCode = 0x210D;
    public const ushort SyncDirectValueOpCode = 0x210E;
    public const ushort SyncThrowingOpCode = 0x210F;

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

    [PacketOpcode(SyncValueTaskOfTOpCode)]
    public ValueTask<EchoPacket> HandleSyncValueTaskOfT(IPacketContext<EchoPacket> context)
    {
        return new ValueTask<EchoPacket>(new EchoPacket { Header = new PacketHeader { OpCode = SyncValueTaskOfTOpCode, SequenceId = context.Packet.Header.SequenceId } });
    }

    [PacketOpcode(SyncTaskOfTOpCode)]
    public Task<EchoPacket> HandleSyncTaskOfT(IPacketContext<EchoPacket> context)
    {
        return Task.FromResult(new EchoPacket { Header = new PacketHeader { OpCode = SyncTaskOfTOpCode, SequenceId = context.Packet.Header.SequenceId } });
    }

    [PacketOpcode(SyncDirectValueOpCode)]
    public EchoPacket HandleSyncDirectValue(IPacketContext<EchoPacket> context)
    {
        return new EchoPacket { Header = new PacketHeader { OpCode = SyncDirectValueOpCode, SequenceId = context.Packet.Header.SequenceId } };
    }

    [PacketOpcode(SyncThrowingOpCode)]
    public ValueTask HandleSyncThrowing(IPacketContext<EchoPacket> context)
    {
        throw new InvalidOperationException("intentional synchronous test failure");
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
