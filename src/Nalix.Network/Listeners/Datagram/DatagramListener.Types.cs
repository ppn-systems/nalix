// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Threading;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Datagrams;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Primitives;
using Nalix.Environment.Options;
using Nalix.Network.Connections;

namespace Nalix.Network.Listeners.Datagram;

public sealed partial class DatagramListener
{
#pragma warning disable CA1001 // The connection is disposed by Discard() or handed over to the session table.
    /// <summary>
    /// A ticket waiting for its first authenticated datagram. Its connection is created on the first datagram that
    /// carries the identifier and stays pending until one authenticates, so a forged datagram cannot consume it.
    /// </summary>
    private sealed class PendingTicket(DatagramIdentity identity, byte[] nonce, long expiresAtMs, Action<IConnection>? onAccepted, ulong streamConnectionId)
    {
        private readonly Lock _lock = new();
        private DatagramIdentity _identity = identity;
        private DatagramConnection? _connection;

        public long ExpiresAtMs { get; } = expiresAtMs;

        public Action<IConnection>? OnAccepted { get; } = onAccepted;

        /// <summary>Gets the <see cref="IConnection.ConnectionId"/> of the stream connection the ticket was issued to.</summary>
        public ulong StreamConnectionId { get; } = streamConnectionId;

        public bool IsExpired(long nowMs) => nowMs > this.ExpiresAtMs;

        public DatagramConnection GetOrCreateConnection(
            DatagramListener listener, ulong connectionId, IDatagramProtectionFactory factory,
            DatagramEngineOptions options, IOpCodeExtractor classifier)
        {
            lock (_lock)
            {
                if (_connection is null)
                {
                    IDatagramProtection protection = factory.Create(_identity.Secret, connectionId, nonce, isServer: true);
                    _connection = new DatagramConnection(listener, connectionId, protection, options, classifier, _identity);
                }

                return _connection;
            }
        }

        /// <summary>Wipes the key material once the session owns its keys.</summary>
        public void ClearSecret()
        {
            lock (_lock)
            {
                _identity = _identity with { Secret = Bytes32.Zero };
                Array.Clear(nonce);
            }
        }

        /// <summary>Disposes the pending connection and wipes the key material.</summary>
        public void Discard()
        {
            lock (_lock)
            {
                _connection?.Dispose();
                _connection = null;
                _identity = _identity with { Secret = Bytes32.Zero };
                Array.Clear(nonce);
            }
        }
    }
#pragma warning restore CA1001
}
