// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Nalix.Abstractions.Networking.Protocols;

/// <summary>
/// Defines the reserved OpCodes for Nalix system and protocol-level internal packets.
/// Values in the range <c>0x0000</c> to <c>0x00FF</c> are reserved for framework and system use.
/// </summary>
public enum ProtocolOpCode : ushort
{
    #region System

    /// <summary>
    /// Client initiates the session handshake and sends its ephemeral public key.
    /// </summary>
    SESSION_INIT = 0x0000,

    /// <summary>
    /// System-level control packet, such as ping, pong, error, or disconnect.
    /// </summary>
    SYSTEM_CONTROL = 0x0001,

    /// <summary>
    /// Unified packet flow for session management, including resume, acknowledgement, and rejection.
    /// </summary>
    SESSION_SIGNAL = 0x0002,

    /// <summary>
    /// Client confirms the derived transcript and proves possession of the negotiated session material.
    /// </summary>
    SESSION_PROOF = 0x0003,

    /// <summary>
    /// System time synchronization packet used for latency and clock-offset estimation.
    /// </summary>
    SYSTEM_TIMESYNC = 0x0004,

    /// <summary>
    /// Trust On First Use packet used to bind or verify the remote session identity.
    /// </summary>
    SESSION_TOFU = 0x0005,

    /// <summary>
    /// Session challenge packet used for cryptographic validation.
    /// </summary>
    SESSION_CHALLENGE = 0x0006,

    /// <summary>
    /// Session acknowledgement packet indicating that the secure session has been established.
    /// </summary>
    SESSION_ESTABLISHED = 0x0007,

    /// <summary>
    /// System directive packet used to send framework-level commands or runtime instructions.
    /// </summary>
    SYSTEM_DIRECTIVE = 0x0008,

    /// <summary>
    /// Session rekey packet used to reestablish a session after a disconnect or network interruption.
    /// </summary>
    SESSION_REKEY = 0x0009,

    /// <summary>
    /// Server issues a cryptographic puzzle to the client.
    /// </summary>
    POW_CHALLENGE = 0x000A,

    /// <summary>
    /// Client submits the mathematical proof for the puzzle.
    /// </summary>
    POW_PROOF = 0x000B,

    /// <summary>
    /// Requests (client) or carries (server) a datagram session ticket over an established stream session.
    /// </summary>
    DATAGRAM_TICKET = 0x000C,

    #endregion System

    #region Extensions

    /// <summary>
    /// Tunnel request packet.
    /// </summary>
    TUNNEL_REQUEST = 0x00F3,

    /// <summary>
    /// Tunnel provisioning acknowledgement.
    /// </summary>
    TUNNEL_PROVIDE_ACK = 0x00F4,

    /// <summary>
    /// Tunnel connection request acknowledgement.
    /// </summary>
    TUNNEL_CONNECT_ACK = 0x00F5,

    /// <summary>
    /// Tunnel readiness notification sent when a tunnel endpoint is prepared for traffic forwarding.
    /// </summary>
    TUNNEL_READY = 0x00F6,

    /// <summary>
    /// Server challenge sent on a tunnel data connection; the provider answers with <c>TUNNEL_PROOF</c>.
    /// </summary>
    TUNNEL_CHALLENGE = 0x00F1,

    /// <summary>
    /// Provider proof of possession of the tunnel token, answering <c>TUNNEL_CHALLENGE</c>.
    /// </summary>
    TUNNEL_PROOF = 0x00F2,

    /// <summary>
    /// Tunnel provisioning packet used to provide tunnel endpoint or relay metadata.
    /// </summary>
    TUNNEL_PROVIDE = 0x00F7,

    /// <summary>
    /// Tunnel connection request used to establish a logical tunnel between peers or relay endpoints.
    /// </summary>
    TUNNEL_CONNECT = 0x00F8,

    /// <summary>
    /// Reflector initialization packet used to start NAT traversal reflector coordination.
    /// </summary>
    TRAVERSAL_REFLECTOR_INIT = 0x00F9,

    /// <summary>
    /// Reflector allocation response containing the assigned relay or reflector endpoint information.
    /// </summary>
    TRAVERSAL_REFLECTOR_ALLOCATED = 0x00FA,

    /// <summary>
    /// Peer signaling packet used to exchange traversal candidates or coordination metadata.
    /// </summary>
    TRAVERSAL_PEER_SIGNAL = 0x00FB,

    /// <summary>
    /// NAT probe acknowledgement packet used to confirm that a traversal probe was received.
    /// </summary>
    TRAVERSAL_NAT_PROBE_ACK = 0x00FC,

    /// <summary>
    /// NAT probe packet used to test direct peer reachability.
    /// </summary>
    TRAVERSAL_NAT_PROBE = 0x00FD,

    /// <summary>
    /// Runtime observation packet used for diagnostics, metrics, or internal runtime inspection.
    /// </summary>
    RUNTIME_OBSERVATION = 0x00FE,

    /// <summary>
    /// Observability access packet used to request or authorize access to framework-level telemetry.
    /// </summary>
    OBSERVABILITY_ACCESS = 0x00FF

    #endregion Extensions
}
