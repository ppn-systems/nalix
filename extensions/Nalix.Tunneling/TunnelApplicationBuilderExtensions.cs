// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Environment.Configuration;
using Nalix.Framework.Injection;
using Nalix.Hosting;
using Nalix.Tunneling.Handlers;

namespace Nalix.Tunneling;

/// <summary>
/// Provides extension methods for <see cref="INetworkApplicationBuilder"/> to configure the Tunneling module.
/// </summary>
public static class TunnelApplicationBuilderExtensions
{
    /// <summary>
    /// Registers the Tunneling module into the Nalix networking pipeline.
    /// </summary>
    /// <param name="builder">The network application builder.</param>
    /// <returns>The builder for chaining.</returns>
    public static INetworkApplicationBuilder UseTunneling(this INetworkApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Initialize and register Registries
        TunnelOptions options = ConfigurationManager.Instance.Get<TunnelOptions>();
        InstanceManager.Instance.Register(options);

        ProviderRegistry providerRegistry = new();
        TunnelRegistry tunnelRegistry = new();
        TunnelSessionRegistry sessionRegistry = new();

        InstanceManager.Instance.Register(tunnelRegistry);
        InstanceManager.Instance.Register(sessionRegistry);
        InstanceManager.Instance.Register(providerRegistry);

        // Register the Tunneling handlers into the Dispatch Options
        _ = builder.ConfigureDispatchOptions(options =>
        {
            _ = options.WithHandler<ProviderHandler>();
            _ = options.WithHandler<ConsumerHandler>();
            _ = options.WithHandler<DataConnectionHandler>();
        });

        return builder;
    }
}
