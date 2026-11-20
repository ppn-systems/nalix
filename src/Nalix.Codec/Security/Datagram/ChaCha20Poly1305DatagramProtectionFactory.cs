// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using Nalix.Abstractions.Networking.Datagrams;
using Nalix.Abstractions.Primitives;

namespace Nalix.Codec.Security.Datagram;

/// <summary>
/// Creates <see cref="ChaCha20Poly1305DatagramProtection"/> instances.
/// </summary>
public sealed class ChaCha20Poly1305DatagramProtectionFactory : IDatagramProtectionFactory
{
    /// <summary>
    /// Gets the shared instance. The factory is stateless.
    /// </summary>
    public static ChaCha20Poly1305DatagramProtectionFactory Instance { get; } = new();

    /// <inheritdoc/>
    public IDatagramProtection Create(Bytes32 sessionSecret, ulong connectionId, ReadOnlySpan<byte> ticketNonce, bool isServer)
        => ChaCha20Poly1305DatagramProtection.Create(sessionSecret, connectionId, ticketNonce, isServer);
}
