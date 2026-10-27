// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Hosting;

namespace Nalix.Observability;

/// <summary>
/// Provides extension methods to register observability features.
/// </summary>
public static class ObservabilityApplicationBuilderExtensions
{
    /// <summary>
    /// Registers the observability access and runtime observation handlers.
    /// </summary>
    /// <param name="builder">The application builder.</param>
    /// <param name="privateKeyPath">
    /// Optional path of the administrative key file. When omitted, <see cref="ObservabilityKey.DefaultPath"/> is used.
    /// </param>
    /// <returns>The builder instance for chaining.</returns>
    public static INetworkApplicationBuilder UseObservability(this INetworkApplicationBuilder builder, string? privateKeyPath = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        ObservabilityKey.Load(privateKeyPath ?? ObservabilityKey.DefaultPath);

        _ = builder.MapHandlers<ObservabilityAccessHandlers>();
        _ = builder.MapHandlers<RuntimeObservationHandlers>();

        return builder;
    }
}
