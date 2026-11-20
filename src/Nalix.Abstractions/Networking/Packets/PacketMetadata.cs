// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nalix.Abstractions.Networking.Packets;

/// <summary>
/// Captures the packet attributes that drive dispatch, validation, and transport behavior.
/// </summary>
/// <param name="opCode">The opcode metadata for the packet.</param>
/// <param name="timeout">The optional timeout metadata.</param>
/// <param name="permission">The optional permission requirement.</param>
/// <param name="encryption">The optional encryption requirement.</param>
/// <param name="rateLimit">The optional rate limit metadata.</param>
/// <param name="customAttributes">Additional custom attributes keyed by attribute type.</param>
/// <remarks>
/// The struct is immutable so metadata can be cached and shared safely across dispatch paths.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
[method: MethodImpl(MethodImplOptions.AggressiveInlining)]
[EditorBrowsable(EditorBrowsableState.Never)]
public readonly struct PacketMetadata(
    PacketOpcodeAttribute opCode,
    PacketTimeoutAttribute? timeout,
    PacketPermissionAttribute? permission,
    PacketEncryptionAttribute? encryption,
    PacketRateLimitAttribute? rateLimit,
    IReadOnlyDictionary<Type, Attribute>? customAttributes = null)
{
    /// <summary>
    /// Gets the opcode attribute that uniquely identifies the packet type.
    /// </summary>
    public readonly PacketOpcodeAttribute PacketOpcode = opCode;

    /// <summary>
    /// Gets the optional timeout attribute.
    /// </summary>
    public readonly PacketTimeoutAttribute? Timeout = timeout;

    /// <summary>
    /// Gets the optional permission requirement.
    /// </summary>
    public readonly PacketPermissionAttribute? Permission = permission;

    /// <summary>
    /// Gets the optional encryption requirement.
    /// </summary>
    public readonly PacketEncryptionAttribute? Encryption = encryption;

    /// <summary>
    /// Gets the optional rate limit requirement.
    /// </summary>
    public readonly PacketRateLimitAttribute? RateLimit = rateLimit;


    /// <summary>Gets additional custom metadata attributes keyed by attribute type.</summary>
    public readonly IReadOnlyDictionary<Type, Attribute> CustomAttributes = customAttributes
        ?? new Dictionary<Type, Attribute>();

    /// <summary>Gets a custom attribute of the specified type, if present.</summary>
    /// <typeparam name="TAttribute">The attribute type to retrieve.</typeparam>
    /// <returns>
    /// The attribute instance if it exists; otherwise, <see langword="null"/>.
    /// </returns>
    public TAttribute? GetCustomAttribute<TAttribute>() where TAttribute : Attribute
        => CustomAttributes.TryGetValue(typeof(TAttribute), out Attribute? value) ? value as TAttribute : null;
}
