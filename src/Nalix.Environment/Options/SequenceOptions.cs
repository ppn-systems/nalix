// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions;
using Nalix.Abstractions.Validation;
using Nalix.Environment.Configuration.Binding;

namespace Nalix.Environment.Options;

/// <summary>
/// Configuration for sequence number validation windows (anti-replay + reordering support).
/// </summary>
[IniComment("Sequence counter configuration - controls replay protection and packet reordering tolerance")]
public sealed partial class SequenceOptions : ConfigurationLoader, IValidatableConfiguration
{
    /// <summary>
    /// Gets or sets the reordering window size for TCP connections.
    /// TCP preserves byte order, but concurrent application responses can be emitted
    /// with earlier transport sequence numbers after later responses.
    /// </summary>
    [IniComment("Reordering window for TCP (default: 32). Small value is sufficient and more secure.")]
    [ValueRange(0, 256)]
    public uint TcpWindow { get; set; } = 32;

    /// <summary>
    /// Validates the configuration options.
    /// </summary>
    public void Validate() => this.ValidateDataAnnotations();
}
