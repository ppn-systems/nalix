// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Runtime.Dispatching;
using Nalix.Runtime.Routing;

namespace Nalix.Hosting.Internal;

/// <summary>
/// Represents the mutable state accumulated while building a network hosting pipeline.
/// </summary>
/// <remarks>
/// This context is used internally during application startup to collect
/// configuration, protocol bindings, packet metadata providers, and handler
/// registrations before the hosting runtime is finalized.
/// </remarks>
internal sealed class HostingBuilderContext
{
    /// <summary>
    /// Gets the registered options configurations.
    /// </summary>
    public List<OptionsConfiguration> Options { get; } = [];

    /// <summary>
    /// Gets the registered packet handler descriptors.
    /// </summary>
    public List<HandlerDescriptor> Handlers { get; } = [];

    /// <summary>
    /// Gets the TCP protocol bindings configured for the host.
    /// </summary>
    public List<TcpProtocolBinding> TcpBindings { get; } = [];

    /// <summary>
    /// Gets the UDP protocol bindings configured for the host.
    /// </summary>
    public List<UdpProtocolBinding> UdpBindings { get; } = [];

    /// <summary>
    /// Gets the WebSocket protocol bindings configured for the host.
    /// </summary>
    public List<WebSocketProtocolBinding> WebSocketBindings { get; } = [];

    /// <summary>
    /// Gets the factories of listeners added with <see cref="INetworkApplicationBuilder.AddListener"/>.
    /// </summary>
    public List<Func<IListener>> CustomListeners { get; } = [];

    /// <summary>
    /// Gets the configuration delegates applied to
    /// <see cref="PacketDispatchOptions{TPacket}"/>.
    /// </summary>
    public List<Action<PacketDispatchOptions<IPacket>>> PacketDispatchOptionsConfigurators { get; } = [];

    /// <summary>
    /// Gets or sets the logger used during host construction.
    /// </summary>
    /// <value>
    /// Defaults to <see cref="NullLogger.Instance"/> when no logger is provided.
    /// </value>
    public ILogger Logger { get; set; } = NullLogger.Instance;

    /// <summary>
    /// Indicates whether the user has explicitly configured a custom
    /// <see cref="IConnectionHub"/> via <c>UseConnectionHub</c>.
    /// When <c>true</c>, the host will not create a default hub.
    /// </summary>
    public bool HasCustomConnectionHub { get; set; }

    /// <summary>
    /// Indicates whether the user has explicitly configured a custom
    /// <see cref="IConnectionGuard"/> via <c>UseConnectionGuard</c>.
    /// When <c>true</c>, the host will not create a default guard.
    /// </summary>
    public bool HasCustomConnectionGuard { get; set; }

    /// <summary>
    /// Indicates whether the user has explicitly configured a custom
    /// <see cref="Nalix.Framework.Memory.Buffers.BufferPoolManager"/> via <c>UseBufferPoolManager</c>.
    /// When <c>true</c>, the host will not create a default manager.
    /// </summary>
    public bool HasCustomBufferPoolManager { get; set; }

    /// <summary>
    /// Indicates whether the user has explicitly configured a custom
    /// <see cref="Nalix.Framework.Memory.Objects.ObjectPoolManager"/> via <c>UseObjectPoolManager</c>.
    /// When <c>true</c>, the host will not create a default manager.
    /// </summary>
    public bool HasCustomObjectPoolManager { get; set; }

    /// <summary>
    /// A custom factory for creating the packet dispatcher.
    /// </summary>
    public Func<Action<PacketDispatchOptions<IPacket>>, IPacketDispatch>? CustomDispatchFactory { get; set; }
}

/// <summary>
/// Describes an options configuration applied during host building.
/// </summary>
internal sealed class OptionsConfiguration
{
    /// <summary>
    /// Gets the options type being configured. Retained for diagnostics only.
    /// </summary>
    public Type OptionsType { get; }

    /// <summary>
    /// Gets the AOT-safe delegate that loads, configures, and validates the options instance.
    /// The delegate was captured at generic registration time, so the closed generic type
    /// is known to the compiler and does not require runtime <c>MakeGenericMethod</c>.
    /// </summary>
    public Action Apply { get; }

    /// <summary>
    /// Initialises a new <see cref="OptionsConfiguration"/> with a pre-captured typed delegate.
    /// </summary>
    /// <param name="optionsType">The options type (for diagnostics).</param>
    /// <param name="apply">
    /// A delegate that loads, configures, and validates the options instance.
    /// </param>
    public OptionsConfiguration(Type optionsType, Action apply)
    {
        this.OptionsType = optionsType;
        this.Apply = apply;
    }
}

/// <summary>
/// Describes an assembly used for packet type discovery.
/// </summary>
/// <param name="Assembly">
/// The assembly containing packet definitions.
/// </param>
/// <param name="RequirePacketAttribute">
/// Indicates whether discovered types must be annotated with a packet attribute
/// to be considered valid packets.
/// </param>
internal sealed record PacketAssemblyDescriptor(
    Assembly Assembly,
    bool RequirePacketAttribute);

/// <summary>
/// Describes an assembly path used for packet type discovery.
/// </summary>
/// <param name="AssemblyPath">
/// The path to an assembly containing packet definitions.
/// </param>
/// <param name="RequirePacketAttribute">
/// Indicates whether discovered types must be annotated with a packet attribute
/// to be considered valid packets.
/// </param>
internal sealed record PacketAssemblyPathDescriptor(
    string AssemblyPath,
    bool RequirePacketAttribute);

/// <summary>
/// Describes a packet namespace filter used during packet discovery.
/// </summary>
/// <param name="PacketNamespace">The namespace to match.</param>
/// <param name="Recursive">
/// Indicates whether sub-namespaces should be included.
/// </param>
/// <param name="AssemblyPath">
/// Optional assembly path scope. When null, currently loaded assemblies are used.
/// </param>
internal sealed record PacketNamespaceDescriptor(
    string PacketNamespace,
    bool Recursive,
    string? AssemblyPath = null);

/// <summary>
/// Describes a packet handler and its creation strategy.
/// </summary>
/// <param name="handlerType">
/// The concrete handler type.
/// </param>
/// <param name="factory">
/// A factory delegate used to create handler instances.
/// </param>
internal sealed class HandlerDescriptor(
    [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicMethods)] Type handlerType,
    Func<object> factory)
{
    [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicMethods)]
    public Type HandlerType { get; } = handlerType;

    public Func<object> Factory { get; } = factory;
}



/// <summary>
/// Represents a binding between a TCP transport and a protocol implementation.
/// </summary>
/// <param name="ProtocolType">
/// The protocol runtime type.
/// </param>
/// <param name="Factory">
/// A factory delegate that creates the protocol using an
/// <see cref="IPacketDispatch"/> instance.
/// </param>
/// <param name="Port">
/// Optional explicit port to listen on. If null, the default configured port is used.
/// </param>
/// <param name="BindingBuilder">
/// Optional mutable builder populated by <c>ListenTcp</c> fluent API.
/// When present, <see cref="Port"/> is resolved from this builder at build time.
/// </param>
internal sealed record TcpProtocolBinding(
    Type ProtocolType,
    Func<IPacketDispatch, IProtocol> Factory,
    ushort? Port = null,
    object? BindingBuilder = null);

/// <summary>
/// Represents a binding between a protocol type and its creation factory for UDP.
/// </summary>
/// <param name="ProtocolType">The type of the protocol.</param>
/// <param name="Factory">The factory used to create the protocol instance.</param>
/// <param name="Port">The optional port to listen on.</param>
/// <param name="Authentication">The optional authentication predicate used to validate incoming datagrams.</param>
/// <param name="BindingBuilder">
/// Optional mutable builder populated by <c>ListenUdp</c> fluent API.
/// When present, <see cref="Port"/> and <see cref="Authentication"/> are resolved from this builder at build time.
/// </param>
internal sealed record UdpProtocolBinding(
    Type ProtocolType,
    Func<IPacketDispatch, IProtocol> Factory,
    ushort? Port = null,
    Func<IConnection, System.Net.EndPoint, ReadOnlySpan<byte>, bool>? Authentication = null,
    object? BindingBuilder = null);

/// <summary>
/// Represents a binding between a protocol type and its creation factory for WebSocket.
/// </summary>
/// <param name="ProtocolType">The type of the protocol.</param>
/// <param name="Factory">The factory used to create the protocol instance.</param>
/// <param name="Port">The optional port to listen on.</param>
/// <param name="Path">The optional HTTP path prefix to listen on.</param>
/// <param name="BindingBuilder">
/// Optional mutable builder populated by <c>ListenWebSocket</c> fluent API.
/// When present, <see cref="Port"/> and <see cref="Path"/> are resolved from this builder at build time.
/// </param>
internal sealed record WebSocketProtocolBinding(
    Type ProtocolType,
    Func<IPacketDispatch, IProtocol> Factory,
    ushort? Port = null,
    string? Path = null,
    object? BindingBuilder = null);
