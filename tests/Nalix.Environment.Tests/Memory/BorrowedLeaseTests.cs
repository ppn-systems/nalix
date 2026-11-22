// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions;
using Nalix.Environment.Memory;

namespace Nalix.Environment.Tests.Memory;

public sealed class BorrowedLeaseTests
{
    [Fact]
    public void Borrow_ExposesPayloadWithoutTakingAReference()
    {
        using BufferLease lease = BufferLease.CopyFrom([1, 2, 3]);
        lease.EncryptedOnWire = true;

        BorrowedLease view = lease.Borrow();

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
        BorrowedLease view = lease.Borrow();
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
        Assert.True(lease.Borrow().IsAlive);

        lease.Dispose();
    }

    [Fact]
    public void Retain_ReturnsAnOwnedReferenceThatKeepsTheBufferAlive()
    {
        BufferLease lease = BufferLease.CopyFrom([7, 8]);

        IBufferLease owned = lease.Borrow().Retain();
        lease.Dispose();

        Assert.Same(lease, owned);
        Assert.Equal(2, owned.Length);
        Assert.Equal(7, owned.Span[0]);

        owned.Dispose();
    }

    [Fact]
    public void DefaultView_IsNotAliveAndThrowsOnAccess()
    {
        Assert.True(default(BorrowedLease).IsDefault);
        Assert.False(default(BorrowedLease).IsAlive);
        _ = Assert.Throws<InvalidOperationException>(static () => default(BorrowedLease).Length);
    }

    [Fact]
    public void Borrow_WhenLeaseIsNull_ThrowsArgumentNullException()
        => _ = Assert.Throws<ArgumentNullException>(static () => ((IBufferLease)null!).Borrow());
}
