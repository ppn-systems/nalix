// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Nalix.Abstractions;
using Nalix.Abstractions.Injection;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Environment.Configuration.Binding;
using Nalix.Framework.Memory.Objects;
using Nalix.Runtime.Dispatching;
using Nalix.Runtime.Routing;

namespace Nalix.Hosting;

/// <summary>
/// Configures a <see cref="NetworkApplication"/> using a fluent builder API.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "<Pending>")]
public interface INetworkApplicationBuilder
{
    /// <summary>
    /// Builds a <see cref="NetworkApplication"/> from the current configuration.
    /// </summary>
    /// <returns>The configured <see cref="NetworkApplication"/> instance.</returns>
    NetworkApplication Build();

    /// <summary>
    /// Configures a Nalix options object before the host starts.
    /// </summary>
    /// <typeparam name="TOptions">The configuration type to mutate.</typeparam>
    /// <param name="configure">The callback used to configure the options instance.</param>
    /// <returns>The current builder instance.</returns>
    INetworkApplicationBuilder Configure<TOptions>(Action<TOptions> configure) where TOptions : ConfigurationLoader, new();

    /// <summary>
    /// Configures the options for the packet dispatcher.
    /// </summary>
    /// <param name="configure">The callback used to configure dispatcher options.</param>
    /// <returns>The current builder instance.</returns>
    INetworkApplicationBuilder ConfigureDispatchOptions(Action<PacketDispatchOptions<IPacket>> configure);

    /// <summary>
    /// Configures the host to use a custom packet dispatcher.
    /// </summary>
    /// <param name="factory">A factory delegate that receives the compiled dispatch options configuration and returns an <see cref="IPacketDispatch"/>.</param>
    /// <returns>The current builder instance.</returns>
    INetworkApplicationBuilder ConfigureDispatch(Func<Action<PacketDispatchOptions<IPacket>>, IPacketDispatch> factory);

    /// <summary>
    /// Sets the logger instance used by the hosted Nalix runtime.
    /// </summary>
    /// <param name="logger">The logger to register into the Nalix runtime.</param>
    /// <returns>The current builder instance.</returns>
    INetworkApplicationBuilder UseLogger(ILogger logger);

    /// <summary>
    /// Sets the <see cref="IConnectionHub"/> instance used by the hosted Nalix runtime.
    /// </summary>
    /// <param name="connectionHub">The connection hub to register into the Nalix runtime.</param>
    /// <returns>The current builder instance.</returns>
    INetworkApplicationBuilder UseConnectionHub(IConnectionHub connectionHub);

    /// <summary>
    /// Sets the <see cref="IConnectionGuard"/> instance used by the hosted Nalix runtime.
    /// </summary>
    /// <param name="connectionGuard">The connection guard to register into the Nalix runtime.</param>
    /// <returns>The current builder instance.</returns>
    INetworkApplicationBuilder UseConnectionGuard(IConnectionGuard connectionGuard);

    /// <summary>
    /// Explicitly registers a <see cref="IBufferPoolManager"/> instance to be used by the application.
    /// </summary>
    /// <param name="manager">The manager instance to use.</param>
    /// <returns>The current builder instance.</returns>
    INetworkApplicationBuilder UseBufferPoolManager(IBufferPoolManager manager);

    /// <summary>
    /// Explicitly registers a <see cref="ObjectPoolManager"/> instance to be used by the application.
    /// </summary>
    /// <param name="manager">The manager instance to use.</param>
    /// <returns>The current builder instance.</returns>
    INetworkApplicationBuilder UseObjectPoolManager(IObjectPoolManager manager);

    /// <summary>
    /// Registers a scoped service factory that creates per-packet instances from the active <see cref="Nalix.Abstractions.Injection.IPacketScope"/>.
    /// </summary>
    /// <typeparam name="TService">The service contract type.</typeparam>
    /// <param name="factory">The factory delegate used to construct the service.</param>
    /// <returns>The current builder instance.</returns>
    INetworkApplicationBuilder RegisterScoped<TService>(Func<IPacketScope, TService> factory) where TService : class;

    /// <summary>
    /// Adds a packet controller type using the default Nalix activator.
    /// </summary>
    /// <typeparam name="THandler">The packet controller type to register.</typeparam>
    /// <returns>The current builder instance.</returns>
    INetworkApplicationBuilder MapHandlers<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicMethods)] THandler>()
        where THandler : class;

    /// <summary>
    /// Adds a packet controller type using an explicit factory.
    /// </summary>
    /// <typeparam name="THandler">The packet controller type to register.</typeparam>
    /// <param name="factory">The factory used to create controller instances.</param>
    /// <returns>The current builder instance.</returns>
    INetworkApplicationBuilder MapHandlers<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] THandler>(Func<THandler> factory) where THandler : class;

    /// <summary>
    /// Adds a packet controller type directly (primarily for static classes).
    /// </summary>
    /// <param name="controllerType">The type of the controller to register.</param>
    /// <returns>The current builder instance.</returns>
    INetworkApplicationBuilder MapHandlers(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicMethods)] Type controllerType);

    /// <summary>
    /// Binds a protocol to a datagram (NDP over UDP) listener. Clients obtain a ticket over an established TCP or
    /// WebSocket session and then use the datagram session; the ticket handlers are mapped automatically.
    /// </summary>
    /// <typeparam name="TProtocol">The protocol type whose frame processor receives datagram messages.</typeparam>
    /// <returns>A fluent builder to configure the port and the protocol factory.</returns>
    IProtocolBindingBuilder MapDatagram<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TProtocol>()
        where TProtocol : class, IProtocol;

    /// <summary>
    /// Adds a listener that the application activates and deactivates together with the protocol
    /// listeners, for extensions that run their own sockets (for example a raw UDP relay).
    /// </summary>
    /// <param name="factory">Creates the listener when the application is activated.</param>
    /// <returns>The current builder instance.</returns>
    INetworkApplicationBuilder AddListener(Func<IListener> factory);

    /// <summary>
    /// Binds a TCP protocol using a fluent builder for port and factory configuration.
    /// </summary>
    /// <typeparam name="TProtocol">The protocol type to host.</typeparam>
    /// <returns>A fluent builder to configure the binding.</returns>
    IProtocolBindingBuilder MapTcp<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TProtocol>()
        where TProtocol : class, IProtocol;

    /// <summary>
    /// Binds a WebSocket protocol using a fluent builder for port, path, and factory configuration.
    /// </summary>
    /// <typeparam name="TProtocol">The protocol type to host.</typeparam>
    /// <returns>A fluent builder to configure the WebSocket binding.</returns>
    IWebSocketBindingBuilder MapWebSocket<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TProtocol>()
        where TProtocol : class, IProtocol;

    #region Obsolete Methods

    /// <summary>
    /// Binds a TCP protocol using a fluent builder for port and factory configuration.
    /// </summary>
    /// <typeparam name="TProtocol">The protocol type to host.</typeparam>
    /// <returns>A fluent builder to configure the binding.</returns>
    [Obsolete("Use MapTcp<TProtocol>() instead. This method will be removed in a future version.")]
    IProtocolBindingBuilder ListenTcp<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TProtocol>()
        where TProtocol : class, IProtocol;

    /// <summary>
    /// Binds a WebSocket protocol using a fluent builder for port, path, and factory configuration.
    /// </summary>
    /// <typeparam name="TProtocol">The protocol type to host.</typeparam>
    /// <returns>A fluent builder to configure the WebSocket binding.</returns>
    [Obsolete("Use MapWebSocket<TProtocol>() instead. This method will be removed in a future version.")]
    IWebSocketBindingBuilder ListenWebSocket<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TProtocol>()
        where TProtocol : class, IProtocol;

    #endregion Obsolete Methods
}
