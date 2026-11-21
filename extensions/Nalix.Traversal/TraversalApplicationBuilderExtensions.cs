// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using Nalix.Environment.Configuration;
using Nalix.Framework.Injection;
using Nalix.Hosting;
using Nalix.Traversal.Handlers;
using Nalix.Traversal.Options;
using Nalix.Traversal.Reflector;

namespace Nalix.Traversal;

/// <summary>
/// Provides extension methods for <see cref="INetworkApplicationBuilder"/> to configure the Traversal module.
/// </summary>
public static class TraversalApplicationBuilderExtensions
{
    /// <summary>
    /// Registers the NAT Traversal module into the Nalix networking pipeline.
    /// This enables peer-to-peer hole punching signaling capabilities.
    /// </summary>
    /// <param name="builder">The network application builder.</param>
    /// <returns>The builder for chaining.</returns>
    public static INetworkApplicationBuilder UseTraversal(this INetworkApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Initialize Reflector manager
        ReflectorManager manager = new();
        InstanceManager.Instance.Register(manager);

        // Register the Traversal handlers into the Dispatch Options
        _ = builder.ConfigureDispatchOptions(options =>
        {
            _ = options.WithHandler<PeerSignalHandler>();
            _ = options.WithHandler<ReflectorInitHandler>();
        });

        // The Reflector relays raw datagrams on its own UDP socket.
        ushort port = ConfigurationManager.Instance.Get<ReflectorOptions>().Port;
        _ = builder.AddListener(() => new ReflectorRelay(manager, port));

        return builder;
    }
}
