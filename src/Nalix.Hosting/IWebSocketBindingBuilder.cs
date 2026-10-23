// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using Nalix.Abstractions.Networking;
using Nalix.Network.Listeners.Web;
using Nalix.Runtime.Dispatching;

namespace Nalix.Hosting;

/// <summary>
/// Fluent builder for configuring a WebSocket protocol binding before attaching it to the host.
/// </summary>
public interface IWebSocketBindingBuilder
{
    /// <summary>
    /// Sets the port to listen on, overriding the default from <c>NetworkWebSocketOptions</c>.
    /// </summary>
    /// <param name="port">The port number.</param>
    /// <returns>The current builder instance.</returns>
    IWebSocketBindingBuilder OnPort(ushort port);

    /// <summary>
    /// Sets the HTTP path prefix to listen on, overriding the default from <c>NetworkWebSocketOptions</c>.
    /// </summary>
    /// <param name="path">The HTTP path prefix (e.g., "/nalix").</param>
    /// <returns>The current builder instance.</returns>
    IWebSocketBindingBuilder WithPath(string path);

    /// <summary>
    /// Uses a custom factory to create protocol instances instead of the default activator.
    /// </summary>
    /// <param name="factory">The factory delegate.</param>
    /// <returns>The current builder instance.</returns>
    IWebSocketBindingBuilder WithFactory(Func<IPacketDispatch, IProtocol> factory);

    /// <summary>
    /// Maps a minimal single-buffer HTTP <c>GET</c> route on this WebSocket listener port.
    /// Can also be used to override built-in DevOps endpoints such as <c>/metrics</c> or <c>/healthz</c>.
    /// </summary>
    /// <param name="path">The HTTP route path (e.g., <c>"/status"</c> or <c>"/metrics"</c>).</param>
    /// <param name="handler">The route handler delegate.</param>
    /// <returns>The current builder instance.</returns>
    IWebSocketBindingBuilder MapGet(string path, HttpRouteHandler handler);

    /// <summary>
    /// Maps a minimal single-buffer HTTP <c>POST</c> route on this WebSocket listener port.
    /// </summary>
    /// <param name="path">The HTTP route path (e.g., <c>"/webhook"</c>).</param>
    /// <param name="handler">The route handler delegate.</param>
    /// <returns>The current builder instance.</returns>
    IWebSocketBindingBuilder MapPost(string path, HttpRouteHandler handler);

    /// <summary>
    /// Finalizes this binding and returns the parent <see cref="INetworkApplicationBuilder"/>
    /// so that additional configuration or <c>Build()</c> can be chained.
    /// </summary>
    /// <returns>The parent application builder.</returns>
    INetworkApplicationBuilder Bind();
}
