// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using Nalix.Abstractions.Networking;
using Nalix.Network.Listeners.Web;
using Nalix.Runtime.Dispatching;

namespace Nalix.Hosting.Internal;

internal sealed record HttpRouteRegistration(HttpMethodType Method, string Path, HttpRouteHandler Handler);

internal sealed class WebSocketBindingBuilder : IWebSocketBindingBuilder
{
    private readonly INetworkApplicationBuilder _parent;

    internal ushort? Port { get; private set; }
    internal string? Path { get; private set; }
    internal Func<IPacketDispatch, IProtocol>? Factory { get; private set; }
    internal List<HttpRouteRegistration> HttpRoutes { get; } = [];

    internal WebSocketBindingBuilder(INetworkApplicationBuilder parent) => _parent = parent;

    public IWebSocketBindingBuilder OnPort(ushort port)
    {
        this.Port = port;
        return this;
    }

    public IWebSocketBindingBuilder WithPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        this.Path = path;
        return this;
    }

    public IWebSocketBindingBuilder WithFactory(Func<IPacketDispatch, IProtocol> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        this.Factory = factory;
        return this;
    }

    public IWebSocketBindingBuilder MapGet(string path, HttpRouteHandler handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(handler);
        this.HttpRoutes.Add(new HttpRouteRegistration(HttpMethodType.Get, path, handler));
        return this;
    }

    public IWebSocketBindingBuilder MapPost(string path, HttpRouteHandler handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(handler);
        this.HttpRoutes.Add(new HttpRouteRegistration(HttpMethodType.Post, path, handler));
        return this;
    }

    public INetworkApplicationBuilder Bind() => _parent;
}
