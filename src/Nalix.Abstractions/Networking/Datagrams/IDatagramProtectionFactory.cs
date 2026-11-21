// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using Nalix.Abstractions.Primitives;

namespace Nalix.Abstractions.Networking.Datagrams;

/// <summary>
/// Creates the <see cref="IDatagramProtection"/> of a datagram session from the stream session secret
/// and the ticket values. Lets the transport layer seal datagrams without referencing the crypto code.
/// </summary>
public interface IDatagramProtectionFactory
{
    /// <summary>
    /// Derives the keys of a datagram session.
    /// </summary>
    /// <param name="sessionSecret">The stream session secret at the time the ticket was issued.</param>
    /// <param name="connectionId">The datagram connection identifier from the ticket.</param>
    /// <param name="ticketNonce">The ticket nonce (16 bytes).</param>
    /// <param name="isServer"><see langword="true"/> for the server side, which sends with the server-to-client key.</param>
    /// <returns>The protection for the session; the caller disposes it.</returns>
    IDatagramProtection Create(Bytes32 sessionSecret, ulong connectionId, ReadOnlySpan<byte> ticketNonce, bool isServer);
}
