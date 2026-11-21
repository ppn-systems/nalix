// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions;
using Nalix.Environment.Configuration.Binding;

namespace Nalix.Traversal.Options;

/// <summary>
/// Provides configuration options for NAT Traversal Reflector.
/// </summary>
[IniComment("Configuration for NAT Traversal and Reflector sessions")]
public sealed partial class ReflectorOptions : ConfigurationLoader
{
    /// <summary>
    /// Gets or sets the UDP port used by the Reflector service.
    /// Default is 28001.
    /// </summary>
    [IniComment("UDP Port for the Reflector service (default: 28001)")]
    public ushort Port { get; set; } = 28001;

    /// <summary>
    /// Gets or sets the maximum burst bandwidth for a single Reflector session (in bytes).
    /// Default is 512,000 bytes (~500 KB).
    /// </summary>
    [IniComment("Maximum burst bandwidth limit per Reflector session in bytes (e.g. 512000 for 500 KB)")]
    public long BandwidthBurstCapacity { get; set; } = 500 * 1024;

    /// <summary>
    /// Gets or sets the sustained bandwidth fill rate for a single Reflector session (in bytes per second).
    /// Default is 204,800 bytes (~200 KB/s).
    /// </summary>
    [IniComment("Sustained bandwidth limit per Reflector session in bytes per second (e.g. 204800 for 200 KB/s)")]
    public long BandwidthFillRate { get; set; } = 200 * 1024;
}
