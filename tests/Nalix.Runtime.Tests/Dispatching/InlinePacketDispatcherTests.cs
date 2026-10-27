// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Runtime.Dispatching;
using Nalix.Runtime.Tests.TestSupport;
using Xunit;

namespace Nalix.Runtime.Tests.Dispatching;

[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
public sealed class InlinePacketDispatcherTests
{
    /// <summary>
    /// A malformed frame can make the opcode extractor throw. The dispatcher runs on a thread-pool work item, so the
    /// exception must be contained: the lease released and the connection's error count incremented.
    /// </summary>
    [Fact]
    public void ExecutePacketAsync_WhenOpCodeExtractorThrows_DisposesLeaseAndCountsError()
    {
        InlinePacketDispatcher dispatcher = new();
        MethodInfo execute = typeof(InlinePacketDispatcher).GetMethod("ExecutePacketAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        using FakeConnection connection = new() { PacketClassifier = new ThrowingExtractor() };
        CountingLease lease = new(new byte[16]);

        ValueTask result = default;
        Exception? thrown = null;
        try
        {
            result = (ValueTask)execute.Invoke(dispatcher, [connection, lease, CancellationToken.None])!;
        }
        catch (TargetInvocationException ex)
        {
            thrown = ex.InnerException;
        }

        Assert.Null(thrown);
        Assert.True(result.IsCompletedSuccessfully);
        Assert.Equal(1, lease.DisposeCount);
        Assert.Equal(1, connection.ErrorCount);
    }

    private sealed class ThrowingExtractor : IOpCodeExtractor
    {
        public ushort Extract(ReadOnlySpan<byte> payload) => throw new InvalidOperationException("malformed frame");
    }

    private sealed class CountingLease(byte[] buffer) : IBufferLease
    {
        private int _disposeCount;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public int Length => buffer.Length;

        public bool IsReliable { get; set; } = true;

        public bool EncryptedOnWire { get; set; }

        public int Capacity => buffer.Length;

        public Span<byte> Span => buffer;

        public Span<byte> SpanFull => buffer;

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

        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }
}
