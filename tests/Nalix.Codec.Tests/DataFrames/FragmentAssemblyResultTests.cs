// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text;
using Nalix.Environment.Fragments;
using Nalix.Environment.Memory;

namespace Nalix.Codec.Tests.DataFrames;

public sealed class FragmentAssemblyResultTests
{
    [Fact]
    public void Ctor_WhenLeaseProvided_ExposesSameLeaseAndMetadataViews()
    {
        byte[] payload = Encoding.UTF8.GetBytes("fragment-result");
        using BufferLease lease = BufferLease.CopyFrom(payload);
        FragmentAssemblyResult result = new(lease);

        Assert.Same(lease, result.Lease);
        Assert.Equal(payload.Length, result.Length);
        Assert.Equal(payload, result.Span.ToArray());
        Assert.Equal(payload, result.Memory.ToArray());
    }

    [Fact]
    public void Length_WhenLeaseIsUpdated_ReflectsNewLengthAndContent()
    {
        using BufferLease lease = BufferLease.Rent(32);
        Encoding.UTF8.GetBytes("abc").AsSpan().CopyTo(lease.SpanFull);
        lease.CommitLength(3);

        FragmentAssemblyResult result = new(lease);
        Assert.Equal(3, result.Length);
        Assert.Equal("abc", Encoding.UTF8.GetString(result.Span));

        Encoding.UTF8.GetBytes("abcdef").AsSpan().CopyTo(lease.SpanFull);
        lease.CommitLength(6);

        Assert.Equal(6, result.Length);
        Assert.Equal("abcdef", Encoding.UTF8.GetString(result.Memory.Span));
    }
}



















