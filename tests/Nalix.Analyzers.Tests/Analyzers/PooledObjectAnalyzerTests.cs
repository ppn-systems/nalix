// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Threading.Tasks;
using Nalix.Analyzers.Tests.TestSupport;
using Xunit;

namespace Nalix.Analyzers.Tests.Analyzers;

public sealed class PooledObjectAnalyzerTests
{
    private const string Header = """
namespace Demo;
using System;
using System.Collections.Generic;
using Nalix.Abstractions;

public sealed class Item : IPoolable
{
    public int Value;
    public void ResetForPool() { Value = 0; }
    public void Return() { }
}

public sealed class Pool
{
    public Item Get() => new();
    public void Return(Item item) { }
}

""";

    private static Task AssertAsync(string body, params string[] expectedIds)
        => AnalyzerTestHarness.AssertDiagnosticIdsAsync(Header + body, expectedIds);

    // ── NALIX121: used after return to the pool ─────────────────────────────

    [Fact]
    public Task UseAfterReturn_ReportsNalix121() => AssertAsync("""
public sealed class C
{
    public int M(Pool pool)
    {
        Item item = pool.Get();
        pool.Return(item);
        return item.Value;
    }
}
""", "NALIX121");

    [Fact]
    public Task ReturnTwice_ReportsNalix121() => AssertAsync("""
public sealed class C
{
    public void M(Pool pool)
    {
        Item item = pool.Get();
        pool.Return(item);
        pool.Return(item);
    }
}
""", "NALIX121");

    [Fact]
    public Task ReturnOnOneBranchThenUse_ReportsNalix121() => AssertAsync("""
public sealed class C
{
    public int M(Pool pool, bool early)
    {
        Item item = pool.Get();
        if (early)
        {
            pool.Return(item);
        }

        return item.Value;
    }
}
""", "NALIX121");

    [Fact]
    public Task SelfReturnThenUse_ReportsNalix121() => AssertAsync("""
public sealed class C
{
    public int M(Item item)
    {
        item.Return();
        return item.Value;
    }
}
""", "NALIX121");

    [Fact]
    public Task ReturnThroughAliasThenUseOriginal_ReportsNalix121() => AssertAsync("""
public sealed class C
{
    public int M(Pool pool)
    {
        Item item = pool.Get();
        Item alias = item;
        pool.Return(alias);
        return item.Value;
    }
}
""", "NALIX121");

    [Fact]
    public Task ReturnInsideLoopThenUseLoopVariable_ReportsNalix121() => AssertAsync("""
public sealed class C
{
    public int M(Pool pool, List<Item> items)
    {
        int total = 0;
        foreach (Item item in items)
        {
            pool.Return(item);
            total += item.Value;
        }

        return total;
    }
}
""", "NALIX121");

    [Fact]
    public Task ReadThenReturn_NoDiagnostic() => AssertAsync("""
public sealed class C
{
    public int M(Pool pool)
    {
        Item item = pool.Get();
        int value = item.Value;
        pool.Return(item);
        return value;
    }
}
""");

    [Fact]
    public Task ReturnInFinally_NoDiagnostic() => AssertAsync("""
public sealed class C
{
    public int M(Pool pool)
    {
        Item item = pool.Get();
        try
        {
            return item.Value;
        }
        finally
        {
            pool.Return(item);
        }
    }
}
""");

    [Fact]
    public Task RentAgainAfterReturn_NoDiagnostic() => AssertAsync("""
public sealed class C
{
    public int M(Pool pool)
    {
        Item item = pool.Get();
        pool.Return(item);
        item = pool.Get();
        int value = item.Value;
        pool.Return(item);
        return value;
    }
}
""");

    [Fact]
    public Task ReturnOnEveryBranch_NoDiagnostic() => AssertAsync("""
public sealed class C
{
    public void M(Pool pool, bool early)
    {
        Item item = pool.Get();
        if (early)
        {
            pool.Return(item);
            return;
        }

        item.Value = 1;
        pool.Return(item);
    }
}
""");

    // ── NALIX120: ResetForPool does not reset a mutable member ──────────────

    [Fact]
    public Task ResetForPoolSkipsField_ReportsNalix120() => AssertAsync("""
public sealed class State : IPoolable
{
    private object? _owner;
    private int _count;

    public void Initialize(object owner) { _owner = owner; _count++; }

    public void ResetForPool() { _count = 0; }
}
""", "NALIX120");

    [Fact]
    public Task ResetForPoolResetsEveryMember_NoDiagnostic() => AssertAsync("""
public sealed class State : IPoolable
{
    private readonly object _sync = new();
    private object? _owner;

    public int Count { get; private set; }

    public void Initialize(object owner) { lock (_sync) { _owner = owner; this.Count++; } }

    public void ResetForPool() { _owner = null; this.Count = 0; }
}
""");

    [Fact]
    public Task MemberInitializedInOnRent_NoDiagnostic() => AssertAsync("""
public sealed class State : IPoolRentable
{
    private int _version;
    private object? _owner;

    public void Initialize(object owner) { _owner = owner; }

    public void OnRent() { _version++; }

    public void ResetForPool() { _owner = null; }
}
""");

    [Fact]
    public Task MemberMarkedSkipClean_NoDiagnostic() => AssertAsync("""
public sealed class State : IPoolable
{
    [SkipClean]
    private int _generation;

    private object? _owner;

    public void Initialize(object owner) { _owner = owner; _generation++; }

    public void ResetForPool() { _owner = null; }
}
""");
}
