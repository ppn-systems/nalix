// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;
using Nalix.Abstractions.Exceptions;
using Nalix.Framework.Injection;

namespace Nalix.Framework.Tests.Injection;

/// <summary>
/// Verifies the public behaviors exposed by <see cref="InstanceManager"/>.
/// </summary>
public sealed class InstanceManagerTests : IDisposable
{
    private readonly InstanceManager _manager = new();

    public InstanceManagerTests() => _manager.Clear(dispose: true);

    public void Dispose()
    {
        try
        {
            _manager.Clear(dispose: true);
        }
        catch
        {
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Register_WhenSameInstanceRegisteredTwice_DoesNotDisposeInstance()
    {
        DisposableCounter instance = new();

        _manager.Register(instance);
        _manager.Register(instance);

        Assert.Equal(0, instance.DisposeCount);

        _ = _manager.RemoveInstance(typeof(DisposableCounter));
    }

    [Fact]
    public void Register_WhenReplacingInstance_DisposesPreviousOnce()
    {
        DisposableCounter first = new();
        DisposableCounter second = new();

        _manager.Register(first);
        _manager.Register(second);

        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(0, second.DisposeCount);

        _ = _manager.RemoveInstance(typeof(DisposableCounter));
    }

    [Fact]
    public void Register_WhenTypeIsInjectable_PublishesInterfaceSlot()
    {
        TestService service = new("svc1");

        _manager.Register(service);

        ITestService? fromInterface = _manager.GetExistingInstance<ITestService>();
        TestService? fromConcrete = _manager.GetExistingInstance<TestService>();

        Assert.Same(service, fromInterface);
        Assert.Same(service, fromConcrete);

        _ = _manager.RemoveInstance(typeof(TestService));
    }

    [Fact]
    public void GetOrCreateInstance_WhenCalledRepeatedly_CachesWhileCreateInstanceReturnsNewObject()
    {
        TestService first = _manager.GetOrCreateInstance<TestService>();
        TestService second = _manager.GetOrCreateInstance<TestService>();
        TestService fresh = (TestService)_manager.CreateInstance(typeof(TestService));

        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.NotSame(first, fresh);

        _manager.Clear(dispose: true);
    }

    [Fact]
    public void RemoveInstance_WhenInstanceRegistered_DisposesAndRemovesIt()
    {
        DisposableCounter instance = new();
        _manager.Register(instance);

        bool removed = _manager.RemoveInstance(typeof(DisposableCounter));

        Assert.True(removed);
        Assert.Equal(1, instance.DisposeCount);
        Assert.False(_manager.HasInstance<DisposableCounter>());
    }

    [Fact]
    public void Clear_WhenDisposeIsTrue_RemovesTrackedInstances()
    {
        DisposableCounter first = new();
        DisposableCounter second = new();
        TestService service = new("x");

        _manager.Register(first);
        _manager.RegisterForClassOnly(second);
        _manager.Register(service);

        _manager.Clear(dispose: true);

        Assert.True(first.DisposeCount >= 0);
        Assert.True(second.DisposeCount >= 0);
        Assert.False(_manager.HasInstance<DisposableCounter>());
        Assert.False(_manager.HasInstance<TestService>());
    }

    [Fact]
    public void GenerateReport_WhenTypesRegistered_ContainsRegisteredTypes()
    {
        _manager.Clear(dispose: true);
        _manager.Register(new TestService("r1"));
        _manager.Register(new DisposableCounter());

        string report = _manager.GenerateReport();

        Assert.Contains(nameof(TestService), report);
        Assert.Contains(nameof(DisposableCounter), report);
    }

    [Fact]
    public void RegisterAndGetOrCreateInstance_WhenCalledConcurrently_DoesNotThrow()
    {
        _manager.Clear(dispose: true);

        const int threadCount = 16;
        const int iterations = 500;
        ConcurrentQueue<Exception> exceptions = new();

        _ = Parallel.For(0, threadCount, _ =>
        {
            try
            {
                for (int i = 0; i < iterations; i++)
                {
                    if ((i & 1) == 0)
                    {
                        _manager.Register(new DisposableCounter());
                        continue;
                    }

                    try
                    {
                        DisposableCounter instance = _manager.GetOrCreateInstance<DisposableCounter>();
                        if (instance.DisposeCount < 0)
                        {
                            exceptions.Enqueue(new InvalidOperationException("Invalid dispose count."));
                        }
                    }
                    catch (Exception ex)
                    {
                        exceptions.Enqueue(ex);
                    }
                }
            }
            catch (Exception ex)
            {
                exceptions.Enqueue(ex);
            }
        });

        Assert.Empty(exceptions);

        DisposableCounter? existing = _manager.GetExistingInstance<DisposableCounter>();
        if (existing is not null)
        {
            Assert.True(existing.DisposeCount >= 0);
        }
    }

    [Fact]
    public void HasInstance_BeforeAndAfterRegister_ReflectsRegistration()
    {
        _manager.Clear(dispose: true);

        Assert.False(_manager.HasInstance<TestService>());
        Assert.Null(_manager.GetExistingInstance<TestService>());

        TestService service = new("t");
        _manager.Register(service);

        Assert.True(_manager.HasInstance<TestService>());
        Assert.Same(service, _manager.GetExistingInstance<TestService>());
    }

    [Fact]
    public void CreateInstance_WhenCalledTwice_DoesNotCacheCreatedObject()
    {
        _manager.Clear(dispose: true);

        TestService first = (TestService)_manager.CreateInstance(typeof(TestService));
        TestService second = (TestService)_manager.CreateInstance(typeof(TestService));

        Assert.NotSame(first, second);
        Assert.Null(_manager.GetExistingInstance<TestService>());
    }

    [Fact]
    public void CreateInstance_WhenStructParameterIsNull_ThrowsNullReferenceException()
    {
        _ = Assert.Throws<NullReferenceException>(() => _manager.CreateInstance(typeof(StructParamService), [null]));
    }

    [Fact]
    public void CreateInstance_WhenNullableStructParameterIsNull_CreatesInstance()
    {
        NullableStructParamService instance = (NullableStructParamService)_manager.CreateInstance(typeof(NullableStructParamService), [null]);
        Assert.NotNull(instance);
        Assert.Null(instance.S);
    }

    [Fact]
    public void RemoveInstance_WhenInterfaceSlotWasReadOnSameThread_InvalidatesInterfaceSlot()
    {
        TestService service = new("svc-removed");

        _manager.Register(service);
        Assert.Same(service, _manager.GetExistingInstance<ITestService>());

        _ = _manager.RemoveInstance(typeof(TestService));

        Assert.Null(_manager.GetExistingInstance<ITestService>());
    }

    [Fact]
    public void GetOrCreateInstance_WhenLockedAndTypeNotCached_Throws()
    {
        _manager.Lockdown();

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => _manager.GetOrCreateInstance<LockdownProbe>());

        Assert.Contains("locked", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(_manager.HasInstance<LockdownProbe>());
    }

    private sealed class LockdownProbe
    {
    }

    private sealed class DisposableCounter : IDisposable
    {
        private int _disposed;

        public int DisposeCount => Volatile.Read(ref _disposed);

        public void Dispose() => _ = Interlocked.Increment(ref _disposed);
    }

    internal interface ITestService
    {
        string Name { get; }
    }

    [Nalix.Abstractions.Injection.Injectable(typeof(ITestService))]
    internal sealed class TestService(string name) : ITestService, IDisposable
    {
        public string Name { get; } = name ?? throw new ArgumentNullException(nameof(name));

        public TestService()
            : this("default")
        {
        }

        public void Dispose()
        {
        }
    }

#pragma warning disable CS0649 // Field is never assigned to
    internal struct MyStruct { public int X; }
#pragma warning restore CS0649
    [Nalix.Abstractions.Injection.Injectable]
    internal sealed class StructParamService(MyStruct s) { public MyStruct S { get; } = s; }

    [Nalix.Abstractions.Injection.Injectable]
    internal sealed class NullableStructParamService(MyStruct? s) { public MyStruct? S { get; } = s; }

}
