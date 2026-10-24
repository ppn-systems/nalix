// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Nalix.Abstractions;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Hosting;
using Nalix.Hosting.Internal;
using Nalix.Network.Protocols;
using Nalix.Runtime.Dispatching;
using Xunit;

namespace Nalix.Hosting.Tests.Application;

public sealed class NetworkApplicationBuilderRegistrationTests
{
    #region MapHandlers registrations

    [Fact]
    public void MapHandlers_Generic_RegistersHandlerDescriptor()
    {
        NetworkApplicationBuilder builder = NetworkApplication.CreateBuilder();

        builder.MapHandlers<TestAotController>();

        HostingBuilderContext state = GetState(builder);
        state.Handlers.Should().ContainSingle(h => h.HandlerType == typeof(TestAotController));
    }

    [Fact]
    public void MapHandlers_Type_RegistersHandlerDescriptor()
    {
        NetworkApplicationBuilder builder = NetworkApplication.CreateBuilder();

        builder.MapHandlers(typeof(TestAotController));

        HostingBuilderContext state = GetState(builder);
        state.Handlers.Should().ContainSingle(h => h.HandlerType == typeof(TestAotController));
    }

    [Fact]
    public void MapHandlers_Factory_RegistersHandlerDescriptor()
    {
        NetworkApplicationBuilder builder = NetworkApplication.CreateBuilder();
        var controller = new TestAotController();

        builder.MapHandlers(() => controller);

        HostingBuilderContext state = GetState(builder);
        state.Handlers.Should().ContainSingle(h => h.HandlerType == typeof(TestAotController));
    }

    [Fact]
    public void MapHandlers_DuplicateType_RegistersBothDescriptors()
    {
        NetworkApplicationBuilder builder = NetworkApplication.CreateBuilder();

        builder.MapHandlers<TestAotController>();
        builder.MapHandlers(typeof(TestAotController));

        HostingBuilderContext state = GetState(builder);
        state.Handlers.Should().HaveCount(2);
        state.Handlers.Should().AllSatisfy(h => h.HandlerType.Should().Be(typeof(TestAotController)));
    }

    #endregion

    #region CreateProtocol generic instantiation

    private static IProtocol InvokeCreateProtocol<TProtocol>(IPacketDispatch dispatch) where TProtocol : class, IProtocol
    {
        MethodInfo method = typeof(NetworkApplicationBuilder)
            .GetMethod("CreateProtocol", BindingFlags.Instance | BindingFlags.NonPublic)!
            .MakeGenericMethod(typeof(TProtocol));
        try
        {
            NetworkApplicationBuilder builder = NetworkApplication.CreateBuilder();
            return (IProtocol)method.Invoke(builder, [dispatch])!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw; // Unreachable
        }
    }

    [Fact]
    public void CreateProtocol_WithDispatchConstructor_PassesDispatch()
    {
        var mockDispatch = new StubPacketDispatch();

        IProtocol protocol = InvokeCreateProtocol<TestProtocolWithDispatch>(mockDispatch);

        protocol.Should().NotBeNull();
        protocol.Should().BeOfType<TestProtocolWithDispatch>();
        ((TestProtocolWithDispatch)protocol).ReceivedDispatch.Should().BeSameAs(mockDispatch);
    }

    [Fact]
    public void CreateProtocol_WithoutDispatchConstructor_UsesParameterlessCtor()
    {
        var mockDispatch = new StubPacketDispatch();

        IProtocol protocol = InvokeCreateProtocol<TestProtocolNoDispatch>(mockDispatch);

        protocol.Should().NotBeNull();
        protocol.Should().BeOfType<TestProtocolNoDispatch>();
        ((TestProtocolNoDispatch)protocol).WasParameterlessCtorCalled.Should().BeTrue();
    }

    [Fact]
    public void CreateProtocol_NullDispatch_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            InvokeCreateProtocol<TestProtocolWithDispatch>(null!));
    }

    #endregion

    #region ResolveHandlerRegistrations returns only explicit handlers

    [Fact]
    public void ResolveHandlerRegistrations_WithMappedHandler_ReturnsOnlyExplicitHandlers()
    {
        NetworkApplicationBuilder builder = NetworkApplication.CreateBuilder();
        builder.MapHandlers<TestAotController>();

        HostingBuilderContext state = GetState(builder);
        IEnumerable<HandlerDescriptor> handlers = InvokeResolveHandlerRegistrations(state);

        handlers.Should().HaveCount(1);
        handlers.First().HandlerType.Should().Be(typeof(TestAotController));
    }

    [Fact]
    public void ResolveHandlerRegistrations_WithoutHandlers_ReturnsEmpty()
    {
        NetworkApplicationBuilder builder = NetworkApplication.CreateBuilder();

        HostingBuilderContext state = GetState(builder);
        IEnumerable<HandlerDescriptor> handlers = InvokeResolveHandlerRegistrations(state);

        handlers.Should().BeEmpty();
    }

    #endregion

    #region Helpers and Test Types

    private static HostingBuilderContext GetState(NetworkApplicationBuilder builder)
    {
        FieldInfo? field = typeof(NetworkApplicationBuilder)
            .GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic);
        return (HostingBuilderContext)field!.GetValue(builder)!;
    }

    private static IEnumerable<HandlerDescriptor> InvokeResolveHandlerRegistrations(HostingBuilderContext state)
    {
        MethodInfo? method = typeof(NetworkApplicationBuilder)
            .GetMethod("ResolveHandlerRegistrations", BindingFlags.Static | BindingFlags.NonPublic);
        return (IEnumerable<HandlerDescriptor>)method!.Invoke(null, [state])!;
    }

    [Nalix.Abstractions.Injection.Injectable]
    internal sealed class TestProtocolWithDispatch : Protocol
    {
        public IPacketDispatch? ReceivedDispatch { get; }
        public override IFrameProcessor FrameProcessor => null!;
        public override Nalix.Abstractions.Networking.Protocols.IOpCodeExtractor OpCodeExtractor => null!;

        public TestProtocolWithDispatch(IPacketDispatch dispatch)
        {
            ReceivedDispatch = dispatch;
        }

        public override void ProcessMessage(object? sender, IConnectionEventArgs args) { }
    }

    [Nalix.Abstractions.Injection.Injectable]
    internal sealed class TestProtocolNoDispatch : Protocol
    {
        public bool WasParameterlessCtorCalled { get; }
        public override IFrameProcessor FrameProcessor => null!;
        public override Nalix.Abstractions.Networking.Protocols.IOpCodeExtractor OpCodeExtractor => null!;

        public TestProtocolNoDispatch()
        {
            WasParameterlessCtorCalled = true;
        }

        public override void ProcessMessage(object? sender, IConnectionEventArgs args) { }
    }

    private sealed class StubPacketDispatch : IPacketDispatch
    {
        public void HandlePacket(IBufferLease lease, IConnection connection) { }
        public void Activate(CancellationToken cancellationToken = default) { }
        public void Deactivate(CancellationToken cancellationToken = default) { }
        public void Dispose() { }
        public string GenerateReport() => string.Empty;
        public void WriteReportData(Utf8JsonWriter writer) { }
    }

    [PacketHandler("AotTest")]
    internal sealed class TestAotController { }

    #endregion
}
