// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Primitives;
using Nalix.Runtime.Dispatching;
using Nalix.Runtime.Tests.TestSupport;
using Xunit;

namespace Nalix.Runtime.Tests.Dispatching;

[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
public sealed class PacketDispatchChannelLifecycleTests
{
    /// <summary>
    /// Workers of a previous activation exit asynchronously. They must not be able to flip the new activation's
    /// running flag off through a shared loop counter, which would leave the dispatcher silently dead.
    /// </summary>
    [Fact]
    public async Task Activate_WhenCalledRightAfterDeactivate_KeepsDispatcherRunningAndProcessing()
    {
        PacketDispatchChannel channel = new(_ => { });

        try
        {
            // The outcome depends on when the old workers get to run, so repeat the cycle.
            for (int cycle = 0; cycle < 10; cycle++)
            {
                channel.Activate();
                channel.Deactivate();
                channel.Activate();

                // Give the workers of the previous activation time to wind down.
                await Task.Delay(100);

                Assert.True(
                    channel.GenerateReport().Contains("Running              : Yes", StringComparison.Ordinal),
                    $"cycle {cycle}: dispatcher stopped after re-activation");

                if (cycle < 9)
                {
                    channel.Deactivate();
                }
            }

            using FakeConnection connection = new();
            FakeBufferLease lease = CreateLease();
            channel.HandlePacket(lease, connection);

            Stopwatch sw = Stopwatch.StartNew();
            while (!lease.IsDisposed && sw.ElapsedMilliseconds < 3000)
            {
                await Task.Delay(10);
            }

            Assert.True(lease.IsDisposed, "A packet pushed after re-activation was never picked up by a dispatch worker.");
        }
        finally
        {
            channel.Deactivate();
            channel.Dispose();
        }
    }

    private static FakeBufferLease CreateLease()
    {
        byte[] buffer = new byte[PacketHeader.Size];
        PacketHeader header = default;
        header.OpCode = 0x0777;
        header.Priority = PacketPriority.NONE;
        header.SequenceId = 1;
        MemoryMarshal.Write(buffer, in header);
        return new FakeBufferLease(buffer) { IsReliable = true };
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

        public Span<byte> SpanFull => this.Span;

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
