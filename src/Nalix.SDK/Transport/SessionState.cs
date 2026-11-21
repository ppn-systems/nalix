// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Primitives;

namespace Nalix.SDK.Transport;

/// <summary>
/// Encapsulates the runtime state of a transport session, separating ephemeral connection data 
/// (like encryption keys and session tokens) from persistent configuration options.
/// </summary>
public class SessionState
{
    /// <summary>
    /// Gets or sets the encryption key used for secure communication.
    /// This is negotiated during the Handshake phase.
    /// </summary>
    public Bytes32 Secret { get; set; }

    /// <summary>
    /// Gets or sets the unique session token assigned by the server.
    /// Primarily used for session resumption.
    /// </summary>
    public ulong SessionToken { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether AEAD encryption is actively applied to all outbound/inbound packets.
    /// </summary>
    public bool EncryptionEnabled { get; set; }

    /// <summary>
    /// Gets or sets the local port of the companion TCP session.
    /// Recorded when the stream connects.
    /// </summary>
    public int LocalPort { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SessionState"/> struct with default values.
    /// </summary>
    public SessionState()
    {
        this.Secret = Bytes32.Zero;
        this.SessionToken = 0;
        this.EncryptionEnabled = false;
        this.LocalPort = 0;
    }

    /// <summary>
    /// Resets the session state back to its unencrypted, initial state.
    /// </summary>
    public void Reset()
    {
        this.Secret = Bytes32.Zero;
        this.SessionToken = 0;
        this.EncryptionEnabled = false;
        this.LocalPort = 0;
    }
}
