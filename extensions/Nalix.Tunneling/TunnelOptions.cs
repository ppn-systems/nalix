// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions;
using Nalix.Environment.Configuration.Binding;

namespace Nalix.Tunneling;

/// <summary>
/// Provides configuration options for TCP Tunneling.
/// </summary>
[IniComment("Configuration for TCP Tunneling sessions")]
public sealed partial class TunnelOptions : ConfigurationLoader
{
    /// <summary>
    /// Gets or sets the maximum number of concurrent tunnel sessions allowed.
    /// Default is 100.
    /// </summary>
    [IniComment("Maximum number of concurrent tunnel sessions allowed (default: 100)")]
    public int MaxConcurrentTunnels { get; set; } = 100;

    /// <summary>
    /// Gets or sets the maximum bandwidth in bytes per second for a single tunnel session.
    /// Default is 0 (unlimited).
    /// </summary>
    [IniComment("Maximum bandwidth in bytes per second for a single tunnel session (default: 0 for unlimited)")]
    public long MaxBytesPerSecond { get; set; } = 0;

    /// <summary>
    /// Gets or sets the buffer size for reading and writing data in the tunnel.
    /// Default is 8192.
    /// </summary>
    [IniComment("Buffer size for reading and writing data in the tunnel (default: 8192)")]
    public int BufferSize { get; set; } = 8192;
}
