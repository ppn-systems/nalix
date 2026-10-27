// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Hosting;
using Nalix.Observability.Internal.History;

namespace Nalix.Observability;

/// <summary>
/// Provides extension methods to register observability features.
/// </summary>
public static class ObservabilityApplicationBuilderExtensions
{
    /// <summary>
    /// Registers the observability handlers with default options (history recording on).
    /// </summary>
    /// <param name="builder">The application builder.</param>
    /// <param name="privateKeyPath">
    /// Optional path of the administrative key file. When omitted, <see cref="ObservabilityKey.DefaultPath"/> is used.
    /// </param>
    /// <returns>The builder instance for chaining.</returns>
    public static INetworkApplicationBuilder UseObservability(this INetworkApplicationBuilder builder, string? privateKeyPath = null)
        => builder.UseObservability(options => options.PrivateKeyPath = privateKeyPath);

    /// <summary>
    /// Registers the observability handlers and, unless disabled, starts recording metric history to disk.
    /// </summary>
    /// <param name="builder">The application builder.</param>
    /// <param name="configure">Configures key location and history recording.</param>
    /// <returns>The builder instance for chaining.</returns>
    public static INetworkApplicationBuilder UseObservability(this INetworkApplicationBuilder builder, Action<ObservabilityOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        ObservabilityOptions options = new();
        configure(options);

        ObservabilityKey.Load(options.PrivateKeyPath ?? ObservabilityKey.DefaultPath);

        if (options.HistoryEnabled)
        {
            HistoryRecorder.Start(options);
        }

        _ = builder.MapHandlers<ObservabilityAccessHandlers>();
        _ = builder.MapHandlers<RuntimeObservationHandlers>();

        return builder;
    }
}
