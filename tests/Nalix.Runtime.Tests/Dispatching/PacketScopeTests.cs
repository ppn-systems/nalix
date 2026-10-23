// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Nalix.Abstractions.Injection;
using Nalix.Framework.Injection;
using Nalix.Runtime.Dispatching;
using Nalix.Runtime.Routing;
using Xunit;

namespace Nalix.Runtime.Tests.Dispatching;

[Collection(TestCollections.ProcessWideServices)]
public sealed class PacketScopeTests
{
    private interface ITestScopedService
    {
        Guid Id { get; }
    }

    private sealed class TestScopedService : ITestScopedService, IDisposable, IAsyncDisposable
    {
        public Guid Id { get; } = Guid.NewGuid();
        public bool IsDisposedSync { get; private set; }
        public bool IsDisposedAsync { get; private set; }
        public List<string>? Log { get; set; }

        public void Dispose()
        {
            this.IsDisposedSync = true;
            this.Log?.Add($"Sync:{this.Id}");
        }

        public ValueTask DisposeAsync()
        {
            this.IsDisposedAsync = true;
            this.Log?.Add($"Async:{this.Id}");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SyncOnlyScopedService : IDisposable
    {
        public Guid Id { get; } = Guid.NewGuid();
        public bool IsDisposedSync { get; private set; }
        public List<string>? Log { get; set; }

        public void Dispose()
        {
            this.IsDisposedSync = true;
            this.Log?.Add($"Sync:{this.Id}");
        }
    }

    private sealed class SingletonService
    {
        public string Name { get; set; } = "Singleton";
    }

    [Fact]
    public void GetRequiredService_WithScopedServiceInSameScope_ReturnsSameInstance()
    {
        ScopedServiceRegistry registry = new();
        registry.RegisterScoped<ITestScopedService>(scope => new TestScopedService());

        using PacketScope scope = new(registry);

        ITestScopedService first = scope.GetRequiredService<ITestScopedService>();
        ITestScopedService second = scope.GetRequiredService<ITestScopedService>();

        first.Should().NotBeNull();
        second.Should().NotBeNull();
        first.Should().BeSameAs(second);
        first.Id.Should().Be(second.Id);
    }

    [Fact]
    public void GetRequiredService_WithDifferentScopes_ReturnsDifferentInstances()
    {
        ScopedServiceRegistry registry = new();
        registry.RegisterScoped<ITestScopedService>(scope => new TestScopedService());

        using PacketScope scope1 = new(registry);
        using PacketScope scope2 = new(registry);

        ITestScopedService first = scope1.GetRequiredService<ITestScopedService>();
        ITestScopedService second = scope2.GetRequiredService<ITestScopedService>();

        first.Should().NotBeSameAs(second);
        first.Id.Should().NotBe(second.Id);
    }

    [Fact]
    public void GetRequiredService_WhenNotRegisteredInScope_FallsBackToSingleton()
    {
        ScopedServiceRegistry registry = new();
        SingletonService singleton = new() { Name = "GlobalSingleton" };
        InstanceManager.Instance.Register<SingletonService>(singleton);

        try
        {
            using PacketScope scope = new(registry);
            SingletonService resolved = scope.GetRequiredService<SingletonService>();

            resolved.Should().NotBeNull();
            resolved.Should().BeSameAs(singleton);
        }
        finally
        {
            _ = InstanceManager.Instance.RemoveInstance(typeof(SingletonService));
        }
    }

    [Fact]
    public void GetRequiredService_WithUnregisteredService_ThrowsInvalidOperationException()
    {
        ScopedServiceRegistry registry = new();
        using PacketScope scope = new(registry);

        Action act = () => scope.GetRequiredService<ITestScopedService>();

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*No service for type*");
    }

    [Fact]
    public void GetService_WithUnregisteredService_ReturnsNull()
    {
        ScopedServiceRegistry registry = new();
        using PacketScope scope = new(registry);

        ITestScopedService? resolved = scope.GetService<ITestScopedService>();

        resolved.Should().BeNull();
    }

    [Fact]
    public async Task DisposeAsync_WithAsyncDisposables_DisposesInLifoOrder()
    {
        ScopedServiceRegistry registry = new();
        List<string> disposalLog = [];

        TestScopedService s1 = new() { Log = disposalLog };
        TestScopedService s2 = new() { Log = disposalLog };

        PacketScope scope = new(registry);
        scope.RegisterForDisposal((IAsyncDisposable)s1);
        scope.RegisterForDisposal((IAsyncDisposable)s2);

        await scope.DisposeAsync();

        s1.IsDisposedAsync.Should().BeTrue();
        s2.IsDisposedAsync.Should().BeTrue();

        // LIFO order: s2 was registered last, so it must be disposed first
        disposalLog.Should().ContainInOrder($"Async:{s2.Id}", $"Async:{s1.Id}");
    }

    [Fact]
    public void Dispose_WithAsyncDisposables_DisposesInLifoOrderViaSyncPath()
    {
        ScopedServiceRegistry registry = new();
        List<string> disposalLog = [];

        TestScopedService s1 = new() { Log = disposalLog };
        TestScopedService s2 = new() { Log = disposalLog };

        PacketScope scope = new(registry);
        scope.RegisterForDisposal((IAsyncDisposable)s1);
        scope.RegisterForDisposal((IAsyncDisposable)s2);

        scope.Dispose();

        s1.IsDisposedSync.Should().BeTrue();
        s2.IsDisposedSync.Should().BeTrue();

        // LIFO order: s2 was registered last, so it must be disposed first
        disposalLog.Should().ContainInOrder($"Sync:{s2.Id}", $"Sync:{s1.Id}");
    }

    [Fact]
    public async Task DisposeAsync_WithMixedSyncAndAsync_PreservesStrictLifoOrder()
    {
        ScopedServiceRegistry registry = new();
        List<string> disposalLog = [];

        TestScopedService s1Async = new() { Log = disposalLog };
        SyncOnlyScopedService s2Sync = new() { Log = disposalLog };

        PacketScope scope = new(registry);
        // Register async first, sync second
        scope.RegisterForDisposal((IAsyncDisposable)s1Async);
        scope.RegisterForDisposal((IDisposable)s2Sync);

        await scope.DisposeAsync();

        s1Async.IsDisposedAsync.Should().BeTrue();
        s2Sync.IsDisposedSync.Should().BeTrue();

        // Strict LIFO: s2Sync was registered second, so it MUST be disposed first!
        disposalLog[0].Should().Be($"Sync:{s2Sync.Id}");
        disposalLog[1].Should().Be($"Async:{s1Async.Id}");
    }

    [Fact]
    public void Connection_WhenNotAttached_ThrowsInvalidOperationException()
    {
        ScopedServiceRegistry registry = new();
        using PacketScope scope = new(registry);

        Action act = () => _ = scope.Connection;

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void GetOrAdd_WithSameFactory_ReturnsSameInstanceWithinScope()
    {
        ScopedServiceRegistry registry = new();
        using PacketScope scope = new(registry);

        int calls = 0;
        SingletonService First() { calls++; return new SingletonService(); }

        SingletonService first = scope.GetOrAdd(First);
        SingletonService second = scope.GetOrAdd(First);

        calls.Should().Be(1);
        first.Should().BeSameAs(second);
    }

    [Fact]
    public void GetOrAdd_WithDifferentScopes_ReturnsDifferentInstances()
    {
        ScopedServiceRegistry registry = new();
        using PacketScope scope1 = new(registry);
        using PacketScope scope2 = new(registry);

        SingletonService first = scope1.GetOrAdd(() => new SingletonService());
        SingletonService second = scope2.GetOrAdd(() => new SingletonService());

        first.Should().NotBeSameAs(second);
    }

    [Fact]
    public void GetOrAdd_WithDisposableInstance_DisposesItWhenScopeDisposes()
    {
        ScopedServiceRegistry registry = new();
        PacketScope scope = new(registry);

        TestScopedService instance = scope.GetOrAdd(() => new TestScopedService());
        scope.Dispose();

        instance.IsDisposedSync.Should().BeTrue();
    }

    [Fact]
    public void GetRequiredService_WithStrictScopes_ThrowsInsteadOfFallingBack()
    {
        ScopedServiceRegistry registry = new() { StrictScopes = true };
        SingletonService singleton = new();
        InstanceManager.Instance.Register<SingletonService>(singleton);

        try
        {
            using PacketScope scope = new(registry);

            Action act = () => scope.GetRequiredService<SingletonService>();

            act.Should().Throw<InvalidOperationException>();
        }
        finally
        {
            _ = InstanceManager.Instance.RemoveInstance(typeof(SingletonService));
        }
    }

    [Fact]
    public void ResetForPool_AfterResolvingService_ClearsServicesAndAllowsReuse()
    {
        ScopedServiceRegistry registry = new();
        registry.RegisterScoped<ITestScopedService>(scope => new TestScopedService());

        PacketScope scope = new(registry);

        ITestScopedService first = scope.GetRequiredService<ITestScopedService>();
        Guid firstId = first.Id;

        // Reset for pool
        scope.ResetForPool();

        // Rent again
        ITestScopedService second = scope.GetRequiredService<ITestScopedService>();
        Guid secondId = second.Id;

        firstId.Should().NotBe(secondId);
        ((TestScopedService)first).IsDisposedSync.Should().BeTrue();
    }
}
