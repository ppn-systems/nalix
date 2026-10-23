// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nalix.Abstractions;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Framework.Memory.Objects;
using Nalix.Runtime.Dispatching;
using Nalix.Runtime.Tests.TestSupport;
using Xunit;
using Nalix.Abstractions.Primitives;
using Nalix.Runtime.Routing;

namespace Nalix.Runtime.Tests.Dispatching;

[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
public sealed class PacketDispatchChannelTests
{
    [Fact]
    public async Task ExecutePacketAsync_WhenHandlerIsAsynchronous_RemainsNonBlockingAndContinuationReleasesResources()
    {
        TaskCompletionSource handlerGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int secondPacketExecuted = 0;

        PacketDispatchChannel channel = new(options =>
        {
            RegisterAsyncMemoryPacketHandler(options, opcode: 0x0101, async () =>
            {
                await handlerGate.Task;
                return null;
            });

            RegisterAsyncMemoryPacketHandler(options, opcode: 0x0102, () =>
            {
                Interlocked.Exchange(ref secondPacketExecuted, 1);
                return Task.FromResult<object?>(null);
            });
        });

        MethodInfo? executePacketMethod = typeof(PacketDispatchChannel).GetMethod(
            "ExecutePacketAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        executePacketMethod.Should().NotBeNull();

        using FakeConnection connection = new();
        FakeBufferLease lease1 = CreateRawPacketLease(opcode: 0x0101, sequence: 1, priority: PacketPriority.NONE);
        FakeBufferLease lease2 = CreateRawPacketLease(opcode: 0x0102, sequence: 2, priority: PacketPriority.NONE);

        // 1. First packet is async and incomplete: ExecutePacketAsync must return immediately (non-blocking)
        ValueTask executionTask1 = (ValueTask)executePacketMethod!.Invoke(channel, [connection, lease1, CancellationToken.None])!;
        executionTask1.IsCompleted.Should().BeTrue(
            "ExecutePacketAsync must return immediately via DispatchContinuation without blocking the worker loop");
        lease1.IsDisposed.Should().BeFalse("lease1 must remain alive while the async handler is still pending");

        // 2. Worker can immediately execute the next packet while the first packet is still awaiting handlerGate
        ValueTask executionTask2 = (ValueTask)executePacketMethod.Invoke(channel, [connection, lease2, CancellationToken.None])!;
        await executionTask2;
        Volatile.Read(ref secondPacketExecuted).Should().Be(1);
        lease2.IsDisposed.Should().BeTrue();

        // 3. Complete the first handler and verify DispatchContinuation.UnsafeOnCompleted disposes lease1
        handlerGate.SetResult();

        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        while (!lease1.IsDisposed && sw.ElapsedMilliseconds < 2000)
        {
            await Task.Delay(5);
        }

        lease1.IsDisposed.Should().BeTrue("DispatchContinuation must dispose the buffer lease once the async task completes");

        // 4. Verify DispatchContinuation directly records IncrementErrorCount() and disposes packet/lease on faulted task
        Type continuationType = typeof(PacketDispatchChannel).Assembly
            .GetType("Nalix.Runtime.Internal.Routing.DispatchContinuation")!;
        MethodInfo getContinuation = typeof(ObjectPoolManager)
            .GetMethod(nameof(ObjectPoolManager.Get), BindingFlags.Public | BindingFlags.Instance)!
            .MakeGenericMethod(continuationType);
        MethodInfo initContinuation = continuationType.GetMethod("Initialize", BindingFlags.Instance | BindingFlags.Public)!;

        TaskCompletionSource faultedGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeBufferLease faultLease = CreateRawPacketLease(opcode: 0x0101, sequence: 3, priority: PacketPriority.NONE);
        DisposableDummyPacket disposablePacket = new();

        object workItem = getContinuation.Invoke(ObjectPoolManager.Shared, null)!;
        initContinuation.Invoke(workItem, [connection, faultLease, disposablePacket, faultedGate.Task, CancellationToken.None]);

        faultedGate.SetException(new InvalidOperationException("simulated async failure"));

        sw.Restart();
        while (!faultLease.IsDisposed && sw.ElapsedMilliseconds < 2000)
        {
            await Task.Delay(5);
        }

        faultLease.IsDisposed.Should().BeTrue();
        disposablePacket.Disposed.Should().BeTrue();
        connection.ErrorCount.Should().BeGreaterThan(0, "DispatchContinuation must increment connection error count when the awaited task faults");
    }

    private static void RegisterAsyncMemoryPacketHandler(
        PacketDispatchOptions<IPacket> options,
        ushort opcode,
        Func<Task<object?>> asyncCallback)
    {
        Type handlerType = typeof(PacketDispatchChannel).Assembly
            .GetType("Nalix.Runtime.Internal.Compilation.PacketHandler`1")!
            .MakeGenericType(typeof(IPacket));

        PacketMetadata metadata = new(
            new PacketOpcodeAttribute(opcode),
            timeout: null,
            permission: null,
            encryption: null,
            rateLimit: null,
            transport: null);

        Func<object?, PacketContext<IPacket>, ValueTask<object?>> invoker = (_, _) => new ValueTask<object?>(asyncCallback());

        ConstructorInfo ctor = handlerType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)[0];
        object handlerStruct = ctor.Invoke([opcode, metadata, null, "AsyncMemoryHandler", typeof(ValueTask), invoker, typeof(MemoryPacket)]);

        FieldInfo tableField = typeof(PacketDispatchOptions<IPacket>).GetField("_handlerTable", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Array table = (Array)tableField.GetValue(options)!;
        Type slotType = typeof(PacketDispatchOptions<>).GetNestedType("HandlerSlot", BindingFlags.NonPublic)!.MakeGenericType(typeof(IPacket));
        object slot = Activator.CreateInstance(slotType, handlerStruct)!;
        table.SetValue(slot, (int)opcode);
    }

    private static FakeBufferLease CreateRawPacketLease(ushort opcode, ushort sequence, PacketPriority priority)
    {
        byte[] buffer = new byte[PacketHeader.Size];
        PacketHeader header = default;
        header.OpCode = opcode;
        header.Priority = priority;
        header.SequenceId = sequence;
        MemoryMarshal.Write(buffer, in header);
        return new FakeBufferLease(buffer) { IsReliable = true };
    }

    private sealed class DisposableDummyPacket : IPacket, IDisposable
    {
        public bool Disposed { get; private set; }

        public int Length => PacketHeader.Size;

        public PacketHeader Header { get; set; }

        public byte[] Serialize() => new byte[PacketHeader.Size];

        public int Serialize(Span<byte> buffer) => PacketHeader.Size;

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeBufferLease(byte[] buffer) : IBufferLease
    {
        private int _disposed;

        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public int Length => buffer.Length;

        public bool IsReliable { get; set; }

        public bool EncryptedOnWire { get; set; }

        public int Capacity => buffer.Length;

        public Span<byte> Span => _disposed != 0 ? throw new ObjectDisposedException(nameof(FakeBufferLease)) : buffer;

        public Span<byte> SpanFull => Span;

        public ReadOnlyMemory<byte> Memory => buffer;

        public void Retain() { }

        public void CommitLength(int length) { }

        public bool ReleaseOwnership(out byte[]? buf, out int start, out int length)
        {
            buf = buffer;
            start = 0;
            length = buffer.Length;
            return true;
        }

        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
    }
}
