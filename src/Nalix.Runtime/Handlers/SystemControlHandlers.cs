// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Nalix.Abstractions;
using Nalix.Abstractions.Diagnostics;
using Nalix.Abstractions.Injection;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Security;
using Nalix.Codec.Extensions;
using Nalix.Codec.Pooling;
using Nalix.Codec.ProtocolFrames;
using Nalix.Codec.Security;
using Nalix.Runtime.Extensions;
using Nalix.Runtime.Internal;
using Nalix.Runtime.Internal.RateLimiting;

namespace Nalix.Runtime.Handlers;

/// <summary>
/// Provides handlers for system-level control packets like PING and PONG.
/// </summary>
[PacketHandler("Nalix.Control")]
public static partial class SystemControlHandlers
{
    /// <summary>
    /// Handles incoming system control packets.
    /// </summary>
    /// <param name="context">The packet context.</param>
    /// <returns>A responding control packet or null.</returns>
    [ReservedOpcodePermitted]
    [PacketEncryption(false)]
    [PacketPermission(PermissionLevel.NONE)]
    [PacketOpcode(ProtocolOpCode.SYSTEM_CONTROL)]
    public static ValueTask HandleAsync(IPacketContext<Control> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        Control packet = context.Packet;
        switch (packet.Type)
        {
            case ControlType.DISCONNECT:
                HandleDisconnect(context, packet);
                break;
            case ControlType.PING:
                // Handled by SystemTimeSyncHandlers
                break;
            case ControlType.SESSION_REKEY:
                return HandleSessionRekey(context, packet);
            case ControlType.CIPHER_UPDATE:
                return HandleCipherUpdate(context, packet);
            case ControlType.TIMESYNCREQUEST:
                // Handled by SystemTimeSyncHandlers
                break;
            case ControlType.ERROR:
                HandleError(context, packet);
                break;
            case ControlType.FAIL:
                HandleFail(context.Connection, packet);
                break;
            case ControlType.NOTICE:
                HandleNotice(context.Connection, packet);
                break;
            case ControlType.POW_REQUEST:
                return HandlePowRequest(context);
            case ControlType.PUBLIC_KEY_REQUEST:
                return HandlePublicKeyRequest(context, packet);
            case ControlType.CIPHER_UPDATE_ACK:
                break;
            // Server generally does not need to send back automatic replies for these
            case ControlType.PONG:              // PONG received if Server pings Client
            case ControlType.SESSION_REKEY_ACK: // Client ACK (if Server inititated)
            case ControlType.SHUTDOWN:          // Ignored by default unless admin system handles it

            // These types are not implemented on the server side:
            // 1.Incorrect protocol direction(e.g., TIMESYNCRESPONSE is sent by the server).
            // 2.Processed at a lower layer(Transport/ Session Layer).
            // 3.These are types reserved for the future.
            case ControlType.NONE:
            case ControlType.RESUME:
            case ControlType.REDIRECT:
            case ControlType.TIMEOUT:
            case ControlType.TIMESYNCRESPONSE:
            case ControlType.RESERVED1:
            case ControlType.RESERVED2:
            default:
                break;
        }

        return default;
    }

    #region Fields

    [Inject]
    private static IProofOfWorkPolicy? s_powPolicy;

    private static readonly ConditionalWeakTable<RuntimeConnectionState, PreAuthControlCooldownState> s_cooldowns = new();

    private sealed class PreAuthControlCooldownState
    {
        public long PowRequestLastSentAtMs;
        public long PublicKeyRequestLastSentAtMs;
    }

    #endregion Fields

    #region Private Methods

    /// <summary>
    /// Handles the incoming POW_REQUEST control packet.
    /// Note: This method is called directly by SystemControlHandlers, so it does not need a [PacketOpcode] attribute.
    /// </summary>
    private static ValueTask HandlePowRequest(IPacketContext<Control> context)
    {
        if (!context.IsReliable)
        {
            // This is a replayed packet, ignore silently.
            return default;
        }

        if (!DirectiveGuard.TryAcquire(
                context.Connection,
                static state => s_cooldowns.GetOrCreateValue(state).PowRequestLastSentAtMs,
                static (state, val) => s_cooldowns.GetOrCreateValue(state).PowRequestLastSentAtMs = val))
        {
            return default;
        }

        byte diff = 12; // Fallback

        if (s_powPolicy is not null)
        {
            diff = s_powPolicy.CurrentDifficulty;
        }

        long ts = System.Environment.TickCount64; // Using ticks as simple timestamp
        (Bytes32 nonce, Bytes32 mac) = ProofOfWork.CreateChallenge(diff, context.Connection.ConnectionId, ts);

        PacketScope<ProofOfWorkChallenge> lease = PacketFactory<ProofOfWorkChallenge>.Acquire();
        try
        {
            ProofOfWorkChallenge challenge = lease.Value;
            challenge.Initialize(nonce, diff, ts, mac);
            challenge.SequenceId = context.Packet.SequenceId;

            return context.Sender.SendAsync(challenge).DisposeOnCompletionAsync(lease);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private static ValueTask HandleCipherUpdate(IPacketContext<Control> context, Control packet)
    {
        if (!context.IsReliable)
        {
            return default;
        }

        IConnection connection = context.Connection;
        if (!connection.GetRuntimeState().HandshakeEstablished || !context.EncryptedOnWire)
        {
            connection.Disconnect("CIPHER_UPDATE requires an established, encrypted session.");
            return default;
        }

        CipherSuiteType requestedCipher = (CipherSuiteType)packet.Reason;
        if ((ushort)packet.Reason > byte.MaxValue ||
            requestedCipher == CipherSuiteType.None ||
            !Enum.IsDefined(requestedCipher))
        {
            connection.Disconnect("Invalid or disallowed cipher suite requested.");
            return default;
        }

        connection.Algorithm = requestedCipher;

        PacketScope<Control> lease = PacketFactory<Control>.Acquire();
        try
        {
            Control ack = lease.Value;
            ack.Initialize(ControlType.CIPHER_UPDATE_ACK, packet.SequenceId, packet.Flags, ProtocolReason.NONE);

            return context.Sender.SendAsync(ack).DisposeOnCompletionAsync(lease);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private static ValueTask HandleSessionRekey(IPacketContext<Control> context, Control packet)
    {
        if (!context.IsReliable)
        {
            // This is a replayed packet, ignore silently.
            return default;
        }

        IConnection connection = context.Connection;

        if (!connection.GetRuntimeState().HandshakeEstablished || !context.EncryptedOnWire)
        {
            connection.Disconnect("Cannot perform Rekey before Handshake is established.");
            return default;
        }

        // Apply the new secret using HKDF Ratcheting
        connection.Secret = HandshakeX25519.DeriveRekeySecret(connection.Secret);

        // Reset the sequence counters to prevent overflow
        connection.TCP.SendSequence.Reset();
        connection.TCP.ReceiveSequence.Reset();

        if (connection.IsUdpCreated)
        {
            connection.UDP!.SendSequence.Reset();
            connection.UDP!.ReceiveSequence.Reset();
        }

        PacketScope<Control> lease = PacketFactory<Control>.Acquire();
        try
        {
            Control ack = lease.Value;
            ack.Initialize(ControlType.SESSION_REKEY_ACK, packet.SequenceId, packet.Flags, ProtocolReason.NONE);

            return context.Sender.SendAsync(ack).DisposeOnCompletionAsync(lease);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    [SuppressMessage("Style", "IDE0060:Remove unused parameter", Justification = "<Pending>")]
    [SuppressMessage("Style", "IDE0022:Use expression body for method", Justification = "<Pending>")]
    private static void HandleDisconnect(IPacketContext<Control> context, Control packet)
    {
        context.Connection.Disconnect("Client requested disconnect via Control frame.");
    }

    private static void HandleError(IPacketContext<Control> context, Control packet)
    {
        IConnection connection = context.Connection;

        connection.Disconnect($"Client reported ERROR: {packet.Reason}");

        if (connection.Level < PermissionLevel.USER)
        {
            return;
        }

        if (!DirectiveGuard.TryAcquire(connection,
            state => state.InboundControlLogLastSentAtMs,
            (state, val) => state.InboundControlLogLastSentAtMs = val))
        {
            return;
        }

        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Information))
        {
            DiagnosticsEvents.Write(
                DiagnosticsEvents.Internal.Information,
                new DiagnosticLog("RT.SystemControlHandlers:HandleAsync", $"error ep={connection.NetworkEndpoint} reason={packet.Reason}"));
        }
    }

    private static void HandleFail(IConnection connection, Control packet)
    {
        connection.Disconnect($"Client reported FAIL: {packet.Reason}");

        if (connection.Level < PermissionLevel.USER)
        {
            return;
        }

        if (!DirectiveGuard.TryAcquire(connection,
            state => state.InboundControlLogLastSentAtMs,
            (state, val) => state.InboundControlLogLastSentAtMs = val))
        {
            return;
        }

        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Warning))
        {
            DiagnosticsEvents.Write(
                DiagnosticsEvents.Internal.Warning,
                new DiagnosticLog("RT.SystemControlHandlers:HandleAsync", $"fail ep={connection.NetworkEndpoint} reason={packet.Reason}"));
        }
    }

    private static void HandleNotice(IConnection connection, Control packet)
    {
        if (connection.Level < PermissionLevel.USER)
        {
            return;
        }

        if (!DirectiveGuard.TryAcquire(connection,
            state => state.InboundControlLogLastSentAtMs,
            (state, val) => state.InboundControlLogLastSentAtMs = val))
        {
            return;
        }

        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Debug))
        {
            DiagnosticsEvents.Write(
                DiagnosticsEvents.Internal.Debug,
                new DiagnosticLog("RT.SystemControlHandlers:HandleAsync", $"notice ep={connection.NetworkEndpoint} reason={packet.Reason}"));
        }
    }

    private static ValueTask HandlePublicKeyRequest(IPacketContext<Control> context, Control packet)
    {
        if (!context.IsReliable)
        {
            // This is a replayed packet, ignore silently.
            return default;
        }

        // Key exchange must happen BEFORE handshake.
        if (context.Connection.GetRuntimeState().HandshakeEstablished)
        {
            context.Connection.Disconnect("Key exchange requested after handshake was established (State Violation).");
            return default;
        }

        if (!DirectiveGuard.TryAcquire(
                context.Connection,
                static state => s_cooldowns.GetOrCreateValue(state).PublicKeyRequestLastSentAtMs,
                static (state, val) => s_cooldowns.GetOrCreateValue(state).PublicKeyRequestLastSentAtMs = val))
        {
            return default;
        }

        PacketScope<SessionTofu> lease = PacketFactory<SessionTofu>.Acquire();
        try
        {
            SessionTofu reply = lease.Value;
            reply.Initialize(HandshakeHandlers.ServerPublicKey);

            // Preserve reliability flag from the request
            reply.SequenceId = packet.SequenceId;

            return context.Sender.SendAsync(reply).DisposeOnCompletionAsync(lease);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    #endregion Private Methods
}
