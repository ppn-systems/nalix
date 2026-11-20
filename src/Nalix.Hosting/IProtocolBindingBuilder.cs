// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using Nalix.Abstractions.Networking;
using Nalix.Runtime.Dispatching;

namespace Nalix.Hosting;

/// <summary>
/// Fluent builder for configuring a protocol binding before attaching it to the host.
/// </summary>
public interface IProtocolBindingBuilder
{
    /// <summary>
    /// Sets the port to listen on, overriding the default from <c>NetworkSocketOptions</c>.
    /// </summary>
    /// <param name="port">The port number.</param>
    /// <returns>The current builder instance.</returns>
    IProtocolBindingBuilder OnPort(ushort port);

    /// <summary>
    /// Uses a custom factory to create protocol instances instead of the default activator.
    /// </summary>
    /// <param name="factory">The factory delegate.</param>
    /// <returns>The current builder instance.</returns>
    IProtocolBindingBuilder WithFactory(Func<IPacketDispatch, IProtocol> factory);

    /// <summary>
    /// Finalizes this binding and returns the parent <see cref="INetworkApplicationBuilder"/>
    /// so that additional configuration or <c>Build()</c> can be chained.
    /// </summary>
    /// <returns>The parent application builder.</returns>
    INetworkApplicationBuilder Bind();
}
