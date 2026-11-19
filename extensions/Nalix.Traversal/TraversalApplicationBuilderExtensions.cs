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

        // Bind UdpPassthroughListener for Reflector using the native builder API
        _ = builder.MapUdp<ReflectorProtocol>()
                   .WithMode(Abstractions.Networking.OperatingMode.Passthrough)
                   .WithFactory(_ => new ReflectorProtocol(manager))
                   .OnPort(ConfigurationManager.Instance.Get<ReflectorOptions>().Port)
                   .Bind();

        return builder;
    }
}
