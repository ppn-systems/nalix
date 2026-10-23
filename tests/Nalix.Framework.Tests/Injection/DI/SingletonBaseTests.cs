// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using Nalix.Framework.Injection.DI;
using Xunit;

namespace Nalix.Framework.Tests.Injection.DI;

public sealed class SingletonBaseTests
{
    [Fact]
    public void TryGetInstance_BeforeAndAfterEnsureCreated_ReturnsFalseThenTrue()
    {
        bool before = FreshSingleton.TryGetInstance(out FreshSingleton? instanceBefore);
        FreshSingleton.EnsureCreated();
        bool after = FreshSingleton.TryGetInstance(out FreshSingleton? instanceAfter);

        Assert.False(before);
        Assert.Null(instanceBefore);
        Assert.True(after);
        Assert.NotNull(instanceAfter);
        Assert.Same(FreshSingleton.Instance, instanceAfter);
    }

    [Fact]
    public void Dispose_WhenCalledTwice_CallsDisposeManagedOnce()
    {
        DisposableSingleton singleton = DisposableSingleton.Instance;

        singleton.Dispose();
        singleton.Dispose();

        Assert.Equal(1, singleton.DisposeManagedCount);
    }

    [Fact]
    public void Instance_WhenTypeHasNoParameterlessConstructor_ThrowsTypeInitializationException()
    {
        TypeInitializationException ex = Assert.Throws<TypeInitializationException>(() =>
        {
            _ = NoDefaultCtorSingleton.Instance;
        });

        Assert.NotNull(ex.InnerException);
        Assert.Contains("generated activator", ex.InnerException!.Message, StringComparison.OrdinalIgnoreCase);
    }

    internal sealed class FreshSingleton : SingletonBase<FreshSingleton>
    {
        internal FreshSingleton() { }
    }

    internal sealed class DisposableSingleton : SingletonBase<DisposableSingleton>
    {
        internal DisposableSingleton() { }
        public int DisposeManagedCount { get; private set; }
        protected override void DisposeManaged() => DisposeManagedCount++;
    }

    #pragma warning disable NALIX065 // Intentionally missing parameterless ctor for error-path testing
    internal sealed class NoDefaultCtorSingleton : SingletonBase<NoDefaultCtorSingleton>
    {
        internal NoDefaultCtorSingleton(int _) { }
    }
    #pragma warning restore NALIX065
}
