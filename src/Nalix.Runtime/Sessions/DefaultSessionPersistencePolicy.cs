// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Sessions;
using Nalix.Environment.Configuration;
using Nalix.Runtime.Extensions;
using Nalix.Runtime.Options;

namespace Nalix.Runtime.Sessions;

/// <summary>
/// Default implementation of <see cref="ISessionPersistencePolicy"/> that checks the handshake status 
/// and minimum attribute count requirements.
/// </summary>
public sealed class DefaultSessionPersistencePolicy : ISessionPersistencePolicy
{
    private readonly SessionStoreOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="DefaultSessionPersistencePolicy"/> class.
    /// </summary>
    public DefaultSessionPersistencePolicy() => _options = ConfigurationManager.Instance.Get<SessionStoreOptions>();

    /// <inheritdoc/>
    public bool ShouldPersist(IConnection connection)
    {
        System.ArgumentNullException.ThrowIfNull(connection);

        // Policy 1: Only persist if the handshake was established
        if (!connection.GetRuntimeState().HandshakeEstablished)
        {
            return false;
        }

        // Policy 2: Any connection with an active negotiated session secret is resumable.
        if (!connection.Secret.IsZero)
        {
            return true;
        }

        // Policy 3: Fallback metadata threshold for non-cryptographic/custom sessions.
        if (connection.Attributes.Count <= _options.MinAttributesForPersistence)
        {
            return false;
        }

        return true;
    }
}
