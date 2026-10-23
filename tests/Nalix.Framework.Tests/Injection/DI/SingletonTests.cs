// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using Nalix.Framework.Injection.DI;
using Xunit;

namespace Nalix.Framework.Tests.Injection.DI;

public sealed class SingletonTests : IDisposable
{
    public SingletonTests() => Singleton.Clear();

    public void Dispose() => Singleton.Clear();

    [Fact]
    public void Resolve_WhenInstanceRegistered_ReturnsSameInstance()
    {
        Service service = new("alpha");

        Singleton.Register<Service>(service);
        Service? resolved = Singleton.Resolve<Service>();

        Assert.Same(service, resolved);
        Assert.True(Singleton.IsRegistered<Service>());
    }

    [Fact]
    public void Register_WhenDuplicateWithoutOverwrite_ThrowsInvalidOperationException()
    {
        Singleton.Register<Service>(new Service("a"));

        Assert.Throws<InvalidOperationException>(() => Singleton.Register<Service>(new Service("b")));
    }

    [Fact]
    public void Register_WhenOverwriteAllowed_ReplacesResolvedService()
    {
        Singleton.Register<Service>(new Service("old"));
        Singleton.Register<Service>(new Service("new"), allowOverwrite: true);

        Service? resolved = Singleton.Resolve<Service>();

        Assert.NotNull(resolved);
        Assert.Equal("new", resolved!.Name);
    }

    [Fact]
    public void Resolve_WhenInterfaceRegisteredWithFactory_ReturnsFactoryInstance()
    {
        Singleton.Register<IService, Service>(() => new Service("factory"));

        IService? resolved = Singleton.Resolve<IService>();

        Assert.NotNull(resolved);
        Assert.Equal("factory", resolved!.Name);
    }

    [Fact]
    public void Resolve_WhenFactoryCreatesDefaultImplementation_ReturnsImplementation()
    {
        Singleton.Register<IService, DefaultService>(() => new DefaultService());

        IService? resolved = Singleton.Resolve<IService>();

        Assert.NotNull(resolved);
        Assert.Equal("default", resolved!.Name);
    }

    [Fact]
    public void Resolve_WhenInterfaceRegisteredWithoutFactoryOrActivator_ThrowsInvalidOperationException()
    {
        Singleton.Register<IService, DefaultService>();

        // Must fail fast - no generated activator exists for DefaultService
        // (it is a private nested test class, not [Injectable] or SingletonBase<T>).
        Assert.Throws<InvalidOperationException>(() => Singleton.Resolve<IService>());
    }

    [Fact]
    public void Resolve_WhenNotRegisteredAndCreateDisabled_ReturnsNull()
    {
        Service? resolved = Singleton.Resolve<Service>(createIfNotExists: false);

        Assert.Null(resolved);
    }

    [Fact]
    public void Resolve_WhenNotRegisteredAndCreateEnabled_ThrowsInvalidOperationException()
    {
        Assert.Throws<InvalidOperationException>(() => Singleton.Resolve<Service>(createIfNotExists: true));
    }

    [Fact]
    public void Remove_WhenTypeRegistered_UnregistersType()
    {
        Singleton.Register<Service>(new Service("x"));
        Assert.True(Singleton.IsRegistered<Service>());

        Singleton.Remove<Service>();

        Assert.False(Singleton.IsRegistered<Service>());
        Assert.Null(Singleton.Resolve<Service>(createIfNotExists: false));
    }

    [Fact]
    public void Dispose_WhenServicesWereResolved_DisposesCreatedServices()
    {
        DisposableService disposable = new();
        Singleton.Register<DisposableService>(disposable);
        _ = Singleton.Resolve<DisposableService>();

        Singleton.Dispose();

        Assert.True(disposable.IsDisposed);
        Assert.False(Singleton.IsDisposing);
    }

    private interface IService
    {
        string Name { get; }
    }

    private sealed class Service(string name) : IService
    {
        public string Name { get; } = name;
    }

    private sealed class DefaultService : IService
    {
        public string Name => "default";
    }

    private sealed class DisposableService : IDisposable
    {
        public bool IsDisposed { get; private set; }
        public void Dispose() => IsDisposed = true;
    }
}
