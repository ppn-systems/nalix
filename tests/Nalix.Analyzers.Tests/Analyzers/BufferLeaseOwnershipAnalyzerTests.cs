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
    public ReadOnlyMemory<byte> Memory => default;
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

    [Fact]
    public Task BorrowedInheritedFromInterface_Disposed_ReportsNalix110() => AssertAsync("""
public interface ISink { void Handle([Borrowed] Lease lease); }
public sealed class C : ISink
{
    public void Handle(Lease lease) => lease.Dispose();
}
""", "NALIX110");

    [Fact]
    public Task BorrowedInheritedFromBaseMethod_Disposed_ReportsNalix110() => AssertAsync("""
public abstract class Base { public abstract void Handle([Borrowed] Lease lease); }
public sealed class C : Base
{
    public override void Handle(Lease lease) => lease.Dispose();
}
""", "NALIX110");

    [Fact]
    public Task BorrowedParameterReleasedTwiceAfterOneRetain_ReportsNalix110() => AssertAsync("""
public sealed class C
{
    public void M([Borrowed] Lease lease)
    {
        lease.Retain();
        Api.Take(lease);
        lease.Dispose();
    }
}
""", "NALIX110");

    [Fact]
    public Task BorrowedParameterDisposedBeforeRetain_ReportsNalix110() => AssertAsync("""
public sealed class C
{
    public void M([Borrowed] Lease lease, bool reject)
    {
        if (reject)
        {
            lease.Dispose();
            return;
        }

        lease.Retain();
        Api.Take(lease);
    }
}
""", "NALIX110");

    [Fact]
    public Task BorrowedParameterReturned_ReportsNalix110() => AssertAsync("""
public sealed class C
{
    public Lease M([Borrowed] Lease lease) => lease;
}
""", "NALIX110");

    [Fact]
    public Task BorrowedParameterMemoryStoredInField_ReportsNalix110() => AssertAsync("""
public sealed class C
{
    private ReadOnlyMemory<byte> _data;
    public void M([Borrowed] Lease lease) => _data = lease.Memory;
    public int Size => _data.Length;
}
""", "NALIX110");

    [Fact]
    public Task BorrowedParameterCapturedByQueuedLambda_ReportsNalix110() => AssertAsync("""
public sealed class C
{
    public void M([Borrowed] Lease lease)
        => _ = System.Threading.Tasks.Task.Run(() => lease.Length);
}
""", "NALIX110");

    // ── NALIX119: [Borrowed] only on the implementation ─────────────────────

    [Fact]
    public Task BorrowedOnlyOnImplementation_ReportsNalix119() => AssertAsync("""
public interface ISink { void Handle(Lease lease); }
public sealed class C : ISink
{
    public void Handle([Borrowed] Lease lease) { _ = lease.Length; }
}
""", "NALIX119");

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
    public Task CallerOfInheritedBorrowedParameter_KeepsOwnership_NoDiagnostic() => AssertAsync("""
public interface ISink { void Handle([Borrowed] Lease lease); }
public sealed class Sink : ISink
{
    public void Handle(Lease lease) { _ = lease.Length; }
}
public sealed class C
{
    public void M(Sink sink)
    {
        Lease lease = Lease.Rent();
        sink.Handle(lease);
        lease.Dispose();
    }
}
""");

    [Fact]
    public Task BorrowedParameterRetainedThenReleasedOnExclusiveBranches_NoDiagnostic() => AssertAsync("""
public sealed class C
{
    public void M([Borrowed] Lease lease, bool queue)
    {
        lease.Retain();
        if (queue)
        {
            Api.Take(lease);
            return;
        }

        lease.Dispose();
    }
}
""");

    [Fact]
    public Task BorrowedParameterRetainedThenCapturedByQueuedLambda_NoDiagnostic() => AssertAsync("""
public sealed class C
{
    public void M([Borrowed] Lease lease)
    {
        lease.Retain();
        _ = System.Threading.Tasks.Task.Run(() => lease.Dispose());
    }
}
""");

    [Fact]
    public Task BorrowedParameterCapturedBySynchronousLambda_NoDiagnostic() => AssertAsync("""
public sealed class C
{
    public int M([Borrowed] Lease lease)
    {
        Func<int> length = () => lease.Length;
        return length();
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

    // ── Flow-sensitive analysis ─────────────────────────────────────────────

    [Fact]
    public Task DisposeOnOneBranchThenDisposeAfter_ReportsNalix108() => AssertAsync("""
public sealed class C
{
    public void M(bool early)
    {
        Lease lease = Lease.Rent();
        if (early)
        {
            lease.Dispose();
        }

        lease.Dispose();
    }
}
""", "NALIX108");

    [Fact]
    public Task UseAfterReleaseOnOneBranch_ReportsNalix109() => AssertAsync("""
public sealed class C
{
    public int M(bool early)
    {
        Lease lease = Lease.Rent();
        if (early)
        {
            Api.Take(lease);
        }

        return lease.Length;
    }
}
""", "NALIX109");

    [Fact]
    public Task DisposeInsideLoop_ReportsNalix108() => AssertAsync("""
public sealed class C
{
    public void M(int count)
    {
        Lease lease = Lease.Rent();
        for (int i = 0; i < count; i++)
        {
            lease.Dispose();
        }
    }
}
""", "NALIX108");

    [Fact]
    public Task DisposeThroughAliasThenOriginal_ReportsNalix108() => AssertAsync("""
public sealed class C
{
    public void M()
    {
        Lease lease = Lease.Rent();
        Lease alias = lease;
        alias.Dispose();
        lease.Dispose();
    }
}
""", "NALIX108");

    [Fact]
    public Task DisposeThenThrowingCallThenDisposeInCatch_ReportsNalix108() => AssertAsync("""
public sealed class C
{
    public void M()
    {
        Lease lease = Lease.Rent();
        try
        {
            lease.Dispose();
            Console.WriteLine();
        }
        catch (InvalidOperationException)
        {
            lease.Dispose();
        }
    }
}
""", "NALIX108");

    [Fact]
    public Task AliasOfBorrowedParameterDisposed_ReportsNalix110() => AssertAsync("""
public sealed class C
{
    public void M([Borrowed] Lease lease)
    {
        Lease alias = lease;
        alias.Dispose();
    }
}
""", "NALIX110");

    [Fact]
    public Task LocalNeverReleased_ReportsNalix039() => AssertAsync("""
public sealed class C
{
    public void M()
    {
        Lease lease = Lease.Rent();
        Api.Peek(lease);
    }
}
""", "NALIX039");

    [Fact]
    public Task LocalNotReleasedOnEarlyReturn_ReportsNalix039() => AssertAsync("""
public sealed class C
{
    public void M(bool skip)
    {
        Lease lease = Lease.Rent();
        if (skip)
        {
            return;
        }

        lease.Dispose();
    }
}
""", "NALIX039");

    [Fact]
    public Task LocalOverwrittenWhileOwned_ReportsNalix039() => AssertAsync("""
public sealed class C
{
    public void M()
    {
        Lease lease = Lease.Rent();
        lease = Lease.Rent();
        lease.Dispose();
    }
}
""", "NALIX039");

    [Fact]
    public Task DisposeThroughAlias_NoDiagnostic() => AssertAsync("""
public sealed class C
{
    public int M()
    {
        Lease lease = Lease.Rent();
        Lease alias = lease;
        int length = lease.Length;
        alias.Dispose();
        return length;
    }
}
""");

    [Fact]
    public Task DisposeInFinally_NoDiagnostic() => AssertAsync("""
public sealed class C
{
    public void M()
    {
        Lease? lease = null;
        try
        {
            lease = Lease.Rent();
            Api.Peek(lease);
        }
        finally
        {
            lease?.Dispose();
        }
    }
}
""");

    [Fact]
    public Task ReturnOrDisposeInCatch_NoDiagnostic() => AssertAsync("""
public sealed class C
{
    public Lease M()
    {
        Lease lease = Lease.Rent();
        try
        {
            Api.Peek(lease);
            return lease;
        }
        catch (InvalidOperationException)
        {
            lease.Dispose();
            throw;
        }
    }
}
""");

    [Fact]
    public Task EarlyReturnInsideLockThenDisposeAfter_NoDiagnostic() => AssertAsync("""
public sealed class C
{
    private readonly object _sync = new();

    public void M(bool early)
    {
        Lease lease = Lease.Rent();
        lock (_sync)
        {
            if (early)
            {
                lease.Dispose();
                return;
            }
        }

        lease.Dispose();
    }
}
""");

    [Fact]
    public Task RentAndDisposeInsideLoop_NoDiagnostic() => AssertAsync("""
public sealed class C
{
    public void M(int count)
    {
        Lease? lease = null;
        for (int i = 0; i < count; i++)
        {
            lease?.Dispose();
            lease = Lease.Rent();
        }

        lease?.Dispose();
    }
}
""");

    [Fact]
    public Task LeaseHandedToCollectionOutParameterOrCaller_NoDiagnostic() => AssertAsync("""
public sealed class C
{
    private readonly System.Collections.Generic.List<Lease> _queue = new();

    public void Enqueue()
    {
        Lease lease = Lease.Rent();
        _queue.Add(lease);
    }

    public void Make(out Lease? result)
    {
        Lease lease = Lease.Rent();
        result = lease;
    }

    public Lease Make()
    {
        Lease lease = Lease.Rent();
        return lease;
    }
}
""");

    [Fact]
    public Task ConditionalTransferMethodKeepsLeaseOnFailure_NoDiagnostic() => AssertAsync("""
public sealed class C
{
    private Lease? _slot;

    public bool Offer(Lease lease)
    {
        if (_slot is not null)
        {
            return false;
        }

        _slot = lease;
        return true;
    }

    public void M()
    {
        Lease lease = Lease.Rent();
        if (!this.Offer(lease))
        {
            lease.Dispose();
        }
    }

    public void Clear() => _slot?.Dispose();
}
""");
}
