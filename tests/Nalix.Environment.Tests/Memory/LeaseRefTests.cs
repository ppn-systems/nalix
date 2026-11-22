// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions;
using Nalix.Environment.Memory;

namespace Nalix.Environment.Tests.Memory;

public sealed class LeaseRefTests
{
    [Fact]
    public void AsRef_ExposesPayloadWithoutTakingAReference()
    {
        using BufferLease lease = BufferLease.CopyFrom([1, 2, 3]);
        lease.EncryptedOnWire = true;

        LeaseRef view = lease.AsRef();

        Assert.False(view.IsDefault);
        Assert.True(view.IsAlive);
        Assert.Equal(3, view.Length);
        Assert.True(view.EncryptedOnWire);
        Assert.True(view.Span.SequenceEqual<byte>([1, 2, 3]));
    }

    /// <summary>
    /// A view must report the lease as gone once its last reference is released, also when the pooled
    /// instance has been rented again and looks valid.
    /// </summary>
    [Fact]
    public void IsAlive_AfterTheLeaseIsReleasedAndReused_IsFalse()
    {
        BufferLease lease = BufferLease.CopyFrom([1, 2, 3]);
        LeaseRef view = lease.AsRef();
        int generation = lease.Generation;

        lease.Dispose();
        using BufferLease next = BufferLease.CopyFrom([4, 5, 6]);

        Assert.NotEqual(generation, lease.Generation);
        Assert.False(view.IsAlive);
    }

    [Fact]
    public void Generation_WhileAnotherReferenceIsHeld_DoesNotChange()
    {
        BufferLease lease = BufferLease.CopyFrom([1]);
        int generation = lease.Generation;

        lease.Retain();
        lease.Dispose();

        Assert.Equal(generation, lease.Generation);
        Assert.True(lease.AsRef().IsAlive);

        lease.Dispose();
    }

    [Fact]
    public void Retain_ReturnsAnOwnedReferenceThatKeepsTheBufferAlive()
    {
        BufferLease lease = BufferLease.CopyFrom([7, 8]);

        IBufferLease owned = lease.AsRef().Retain();
        lease.Dispose();

        Assert.Same(lease, owned);
        Assert.Equal(2, owned.Length);
        Assert.Equal(7, owned.Span[0]);

        owned.Dispose();
    }

    [Fact]
    public void DefaultView_IsNotAliveAndThrowsOnAccess()
    {
        Assert.True(default(LeaseRef).IsDefault);
        Assert.False(default(LeaseRef).IsAlive);
        _ = Assert.Throws<InvalidOperationException>(static () => default(LeaseRef).Length);
    }

    [Fact]
    public void AsRef_WhenLeaseIsNull_ThrowsArgumentNullException()
        => _ = Assert.Throws<ArgumentNullException>(static () => ((IBufferLease)null!).AsRef());

    /// <summary>
    /// A lease that is never pooled keeps the default generation, so a reference to it stays alive.
    /// </summary>
    [Fact]
    public void Generation_OfALeaseThatDoesNotTrackIt_IsZeroAndTheReferenceStaysAlive()
    {
        IBufferLease lease = new UnpooledLease();

        Assert.Equal(0, lease.Generation);
        Assert.True(lease.AsRef().IsAlive);
    }

    private sealed class UnpooledLease : IBufferLease
    {
        private readonly byte[] _buffer = new byte[4];

        public int Length => _buffer.Length;

        public bool EncryptedOnWire { get; set; }

        public int Capacity => _buffer.Length;

        public Span<byte> Span => _buffer;

        public Span<byte> SpanFull => _buffer;

        public ReadOnlyMemory<byte> Memory => _buffer;

        public void Retain() { }

        public void CommitLength(int length) { }

        public bool ReleaseOwnership(out byte[]? buffer, out int start, out int length)
        {
            buffer = null;
            start = 0;
            length = 0;
            return false;
        }

        public void Dispose() { }
    }
}
