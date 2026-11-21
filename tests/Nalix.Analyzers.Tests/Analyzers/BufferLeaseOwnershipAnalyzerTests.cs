// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Threading.Tasks;
using Nalix.Analyzers.Tests.TestSupport;
using Xunit;

namespace Nalix.Analyzers.Tests.Analyzers;

public sealed class BufferLeaseOwnershipAnalyzerTests
{
    private const string Header = """
namespace Demo;
using System;
using Nalix.Abstractions;

[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property | AttributeTargets.Field)]
public sealed class BorrowedAttribute : Attribute { }

public sealed class Lease : IBufferLease, IDisposable
{
    public static Lease Rent() => new();
    public int Length => 0;
    public void Retain() { }
    public void Dispose() { }
}

public static class Api
{
    public static void Take(Lease owned) => owned.Dispose();
    public static void Peek([Borrowed] Lease lease) { _ = lease.Length; }
    public static bool TryTake(Lease owned) { owned.Dispose(); return true; }
}

""";

    private static Task AssertAsync(string body, params string[] expectedIds)
        => AnalyzerTestHarness.AssertDiagnosticIdsAsync(Header + body, expectedIds);

    // ── NALIX108: released twice ────────────────────────────────────────────

    [Fact]
    public Task DisposeTwice_ReportsNalix108() => AssertAsync("""
public sealed class C
{
    public void M()
    {
        Lease lease = Lease.Rent();
        lease.Dispose();
        lease.Dispose();
    }
}
""", "NALIX108");

    [Fact]
    public Task DisposeAfterOwnershipTransfer_ReportsNalix108() => AssertAsync("""
public sealed class C
{
    public void M()
    {
        Lease lease = Lease.Rent();
        Api.Take(lease);
        lease.Dispose();
    }
}
""", "NALIX108");

    [Fact]
    public Task ConditionalDisposeTwice_ReportsNalix108() => AssertAsync("""
public sealed class C
{
    public void M()
    {
        Lease? lease = Lease.Rent();
        lease?.Dispose();
        lease?.Dispose();
    }
}
""", "NALIX108");

    [Fact]
    public Task ExplicitDisposeOfUsingLocal_ReportsNalix108() => AssertAsync("""
public sealed class C
{
    public void M()
    {
        using Lease lease = Lease.Rent();
        lease.Dispose();
    }
}
""", "NALIX108");

    [Fact]
    public Task TransferOfUsingLocal_ReportsNalix108() => AssertAsync("""
public sealed class C
{
    public void M()
    {
        using Lease lease = Lease.Rent();
        Api.Take(lease);
    }
}
""", "NALIX108");

    // ── NALIX109: used after release ────────────────────────────────────────

    [Fact]
    public Task UseAfterDispose_ReportsNalix109() => AssertAsync("""
public sealed class C
{
    public int M()
    {
        Lease lease = Lease.Rent();
        lease.Dispose();
        return lease.Length;
    }
}
""", "NALIX109");

    [Fact]
    public Task UseAfterOwnershipTransfer_ReportsNalix109() => AssertAsync("""
public sealed class C
{
    public void M()
    {
        Lease lease = Lease.Rent();
        Api.Take(lease);
        Api.Peek(lease);
    }
}
""", "NALIX109");

    // ── NALIX110: borrowed lease released or stored ─────────────────────────

    [Fact]
    public Task BorrowedParameterDisposed_ReportsNalix110() => AssertAsync("""
public sealed class C
{
    public void M([Borrowed] Lease lease) => lease.Dispose();
}
""", "NALIX110");

    [Fact]
    public Task BorrowedParameterPassedToOwner_ReportsNalix110() => AssertAsync("""
public sealed class C
{
    public void M([Borrowed] Lease lease) => Api.Take(lease);
}
""", "NALIX110");

    [Fact]
    public Task BorrowedParameterStoredInField_ReportsNalix110() => AssertAsync("""
public sealed class C
{
    private Lease? _lease;
    public void M([Borrowed] Lease lease) => _lease = lease;
    public void Clear() => _lease = null;
}
""", "NALIX110");

    // ── No diagnostics ─────────────────────────────────────────────────────

    [Fact]
    public Task SingleDispose_NoDiagnostic() => AssertAsync("""
public sealed class C
{
    public void M()
    {
        Lease lease = Lease.Rent();
        Api.Peek(lease);
        lease.Dispose();
    }
}
""");

    [Fact]
    public Task BorrowedPassThenDispose_NoDiagnostic() => AssertAsync("""
public sealed class C
{
    public void M()
    {
        Lease lease = Lease.Rent();
        Api.Peek(lease);
        Api.Peek(lease);
        lease.Dispose();
    }
}
""");

    [Fact]
    public Task DisposeInBranchThenDisposeAfter_NoDiagnostic() => AssertAsync("""
public sealed class C
{
    public void M(bool fail)
    {
        Lease lease = Lease.Rent();
        if (fail)
        {
            lease.Dispose();
            return;
        }

        Api.Take(lease);
    }
}
""");

    [Fact]
    public Task TryTransferThenDisposeOnFailure_NoDiagnostic() => AssertAsync("""
public sealed class C
{
    public void M()
    {
        Lease lease = Lease.Rent();
        bool ok = Api.TryTake(lease);
        if (!ok)
        {
            lease.Dispose();
        }
    }
}
""");

    [Fact]
    public Task RetainThenDisposeTwice_NoDiagnostic() => AssertAsync("""
public sealed class C
{
    public void M()
    {
        Lease lease = Lease.Rent();
        lease.Retain();
        lease.Dispose();
        lease.Dispose();
    }
}
""");

    [Fact]
    public Task ReassignedAfterDispose_NoDiagnostic() => AssertAsync("""
public sealed class C
{
    public void M()
    {
        Lease lease = Lease.Rent();
        lease.Dispose();
        lease = Lease.Rent();
        lease.Dispose();
    }
}
""");

    [Fact]
    public Task CapturedByLambda_NoDiagnostic() => AssertAsync("""
public sealed class C
{
    public void M()
    {
        Lease lease = Lease.Rent();
        Action dispose = () => lease.Dispose();
        dispose();
        lease.Dispose();
    }
}
""");

    [Fact]
    public Task BorrowedParameterStoredInBorrowedField_NoDiagnostic() => AssertAsync("""
public sealed class C
{
    [Borrowed] private Lease? _lease;
    public void M([Borrowed] Lease lease) => _lease = lease;
    public void Clear() => _lease = null;
}
""");

    [Fact]
    public Task BorrowedParameterPassedToBorrowed_NoDiagnostic() => AssertAsync("""
public sealed class C
{
    public void M([Borrowed] Lease lease) => Api.Peek(lease);
}
""");

    [Fact]
    public Task BorrowedParameterRetainedThenDisposed_NoDiagnostic() => AssertAsync("""
public sealed class C
{
    public void M([Borrowed] Lease lease)
    {
        lease.Retain();
        lease.Dispose();
    }
}
""");

    [Fact]
    public Task BorrowedParameterPassedToDelegate_NoDiagnostic() => AssertAsync("""
public sealed class C
{
    public event EventHandler<Lease>? Received;
    public void M([Borrowed] Lease lease) => this.Received?.Invoke(this, lease);
}
""");

    [Fact]
    public Task BorrowedParameterPassedToGenericMethod_NoDiagnostic() => AssertAsync("""
public sealed class C
{
    public object M([Borrowed] Lease lease) => Tuple.Create(this, lease);
}
""");

    [Fact]
    public Task OwnedParameterConditionallyDisposed_NoNalix039() => AssertAsync("""
public sealed class C
{
    public void M(Lease? lease) => lease?.Dispose();
}
""");

    [Fact]
    public Task OwnedParameterStoredInField_NoNalix039() => AssertAsync("""
public sealed class C
{
    private Lease? _lease;
    public void M(Lease lease) { _lease = lease; }
    public void Clear() => _lease?.Dispose();
}
""");
}
