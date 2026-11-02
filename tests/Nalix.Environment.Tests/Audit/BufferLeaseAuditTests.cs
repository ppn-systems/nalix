// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Environment.Memory;
using Xunit;

namespace Nalix.Environment.Tests.Audit;

public sealed class BufferLeaseAuditTests
{
    [Fact]
    [Trait("Category", "Audit")]
    public void Dispose_WhenStaleHandleReleasedAfterShellReuse_KeepsNewOwnerBuffer()
    {
        BufferLease first = BufferLease.Rent(16);
        first.Dispose();

        BufferLease second = BufferLease.Rent(16);
        Assert.Same(first, second);

        first.Dispose();

        Assert.True(second.RawCapacity > 0, "A stale second Dispose released the buffer owned by the new lease.");
    }
}
