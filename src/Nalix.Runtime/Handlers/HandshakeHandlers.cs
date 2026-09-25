// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Injection;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Networking.Sessions;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Security;
using Nalix.Codec.Extensions;
using Nalix.Codec.Pooling;
using Nalix.Codec.ProtocolFrames;
using Nalix.Codec.Security;
using Nalix.Codec.Security.Asymmetric;
using Nalix.Codec.Security.Primitives;
using Nalix.Environment.IO;
using Nalix.Environment.Random;
using Nalix.Framework.Injection;
using Nalix.Framework.Memory.Objects;
using Nalix.Runtime.Extensions;
using Nalix.Runtime.Internal;
using Nalix.Runtime.Security;

namespace Nalix.Runtime.Handlers;

/// <summary>
/// Provides handlers for the default server-side X25519 handshake protocol.
/// </summary>
[PacketHandler("Nalix.Handshake")]
public static partial class HandshakeHandlers
{
    #region APIs

    /// <inheritdoc/>
    [ReservedOpcodePermitted]
    [PacketEncryption(false)]
    [PacketPermission(PermissionLevel.NONE)]
    [PacketOpcode(ProtocolOpCode.SESSION_INIT)]
    public static ValueTask HandleSessionInitAsync(IPacketContext<SessionInit> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.IsReliable)
        {
            // This is a replayed packet, ignore silently.
            return default;
        }

        SessionInit packet = context.Packet;
        IConnection connection = context.Connection;

        if (connection.GetRuntimeState().HandshakeEstablished)
        {
            return RejectHandshakeAsync(connection, context.Sender, ProtocolReason.STATE_VIOLATION);
        }

        if (s_powPolicy != null && s_powPolicy.IsUnderAttack)
        {
            if (connection.Level < PermissionLevel.POW_VERIFIED)
            {
                // Adaptive Timeout: Expected solve time + 10 seconds network buffer
                // Time to solve is approximately 2^Difficulty hashes.
                // Assuming an average client can compute ~500 hashes per millisecond.
                int adaptiveTimeoutMs = ((1 << s_powPolicy.CurrentDifficulty) / 500) + 5000;

                PacketScope<Control> error = PacketFactory<Control>.Acquire();
                try
                {
                    error.Value.Initialize(ControlType.ERROR, reasonCode: ProtocolReason.POW_REQUIRED, flags: PacketFlags.SYSTEM);
                    connection.UpdateIdleTimeout(adaptiveTimeoutMs);
                    return context.Sender.SendAsync(error.Value).DisposeOnCompletionAsync(error);
                }
                catch
                {
                    error.Dispose();
                    throw;
                }
            }
        }

        if (!TryAcquireHandshakeSlot(connection))
        {
            return RejectHandshakeAsync(connection, context.Sender, ProtocolReason.STATE_VIOLATION);
        }

        X25519.X25519KeyPair serverKey = X25519.GenerateKeyPair();

        // Declared up front so the finally below scrubs them on every exit path — including the
        // early rejects. Each is a value type, so a copy of the key material would otherwise stay
        // readable in this frame after the handshake completes.
        Bytes32 sharedSecretEE = default;
        Bytes32 sharedSecretSE = default;
        Bytes32 masterSecret = default;

        try
        {
            try
            {
                sharedSecretEE = X25519.Agreement(serverKey.PrivateKey, packet.PublicKey);
            }
            catch (InvalidOperationException)
            {
                return RejectHandshakeAsync(connection, context.Sender, ProtocolReason.DECRYPTION_FAILED);
            }

            if (sharedSecretEE.IsZero)
            {
                return RejectHandshakeAsync(connection, context.Sender, ProtocolReason.DECRYPTION_FAILED);
            }

                    try
            {
                sharedSecretSE = X25519.Agreement(s_certificate, packet.PublicKey);
            }
            catch (InvalidOperationException)
            {
                return RejectHandshakeAsync(connection, context.Sender, ProtocolReason.DECRYPTION_FAILED);
            }

            if (sharedSecretSE.IsZero)
            {
                return RejectHandshakeAsync(connection, context.Sender, ProtocolReason.DECRYPTION_FAILED);
            }

            masterSecret = HandshakeX25519.ComputeMasterSecret(sharedSecretEE, sharedSecretSE);

            Span<byte> nonceBytes = stackalloc byte[Bytes32.Size];
            Csprng.Fill(nonceBytes);
            Bytes32 serverNonce = new(nonceBytes);
            MemorySecurity.ZeroMemory(nonceBytes);

            Bytes32 transcriptHash = HandshakeX25519.ComputeTranscriptHash(
                packet.PublicKey,
                packet.Nonce,
                serverKey.PublicKey,
                serverNonce);

            HandshakeContext state = s_pool.Get<HandshakeContext>();
            state.SharedSecret = masterSecret;
            state.TranscriptHash = transcriptHash;
            state.SessionKey = HandshakeX25519.DeriveSessionKey(masterSecret, packet.Nonce, serverNonce, transcriptHash);

            if (!TryPublishHandshakeState(connection, state))
            {
                return RejectHandshakeAsync(connection, context.Sender, ProtocolReason.STATE_VIOLATION);
            }

            PacketScope<SessionChallenge> lease = PacketFactory<SessionChallenge>.Acquire();
            try
            {
                SessionChallenge reply = lease.Value;

                reply.Initialize(serverKey.PublicKey, serverNonce, HandshakeX25519.ComputeServerProof(masterSecret, transcriptHash));
                reply.SequenceId = packet.SequenceId;

                return context.Sender.SendAsync(reply).DisposeOnCompletionAsync(lease);
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }
        finally
        {
            serverKey.Wipe();
            Bytes32.Wipe(ref sharedSecretEE);
            Bytes32.Wipe(ref sharedSecretSE);
            Bytes32.Wipe(ref masterSecret);
        }
    }

    /// <inheritdoc/>
    [ReservedOpcodePermitted]
    [PacketEncryption(false)]
    [PacketPermission(PermissionLevel.NONE)]
    [PacketOpcode((ushort)ProtocolOpCode.SESSION_PROOF)]
    public static ValueTask HandleSessionProofAsync(IPacketContext<SessionProof> context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.IsReliable)
        {
            // This is a replayed packet, ignore silently.
            return default;
        }

        SessionProof packet = context.Packet;
        IConnection connection = context.Connection;

        if (connection.GetRuntimeState().HandshakeEstablished)
        {
            return RejectHandshakeAsync(connection, context.Sender, ProtocolReason.STATE_VIOLATION);
        }

        if (s_powPolicy != null && s_powPolicy.IsUnderAttack)
        {
            if (connection.Level < PermissionLevel.POW_VERIFIED)
            {
                PacketScope<Control> error = PacketFactory<Control>.Acquire();
                try
                {
                    error.Value.Initialize(ControlType.ERROR, reasonCode: ProtocolReason.POW_REQUIRED, flags: PacketFlags.SYSTEM);
                    return context.Sender.SendAsync(error.Value).DisposeOnCompletionAsync(error);
                }
                catch
                {
                    error.Dispose();
                    throw;
                }
            }
        }

        if (!TryGetState(connection, out HandshakeContext? state) || state is null)
        {
            return RejectHandshakeAsync(connection, context.Sender, ProtocolReason.STATE_VIOLATION);
        }

        Bytes32 expectedProof = HandshakeX25519.ComputeClientProof(state.SharedSecret, state.TranscriptHash);
        if (packet.Proof != expectedProof)
        {
            return RejectHandshakeAsync(connection, context.Sender, ProtocolReason.SIGNATURE_INVALID);
        }

        connection.Secret = state.SessionKey;
        connection.Algorithm = CipherSuiteType.Chacha20Poly1305;

        Bytes32 expectedFinish = HandshakeX25519.ComputeServerFinishProof(state.SharedSecret, state.TranscriptHash);

        if (context.Connection.Level < PermissionLevel.ESTABLISHED)
        {
            connection.Level = PermissionLevel.ESTABLISHED;
        }

        RuntimeConnectionState runtimeState = connection.GetRuntimeState();

        runtimeState.HandshakeEstablished = true;

        if (runtimeState.HandshakeState is HandshakeContext contextState)
        {
            s_pool.Return(contextState);
        }

        runtimeState.HandshakeState = null;

        if (s_sessionService != null)
        {
            return AwaitSaveSessionAndSendAsync(context, expectedFinish, connection, packet.SequenceId);
        }

        return SendSessionEstablishedAsync(context, expectedFinish, connection, packet.SequenceId);

        static async ValueTask AwaitSaveSessionAndSendAsync(IPacketContext<SessionProof> context, Bytes32 expectedFinish, IConnection connection, ushort sequenceId)
        {
            if (s_sessionService != null)
            {
                await s_sessionService.SaveSessionAsync(connection).ConfigureAwait(false);
            }

            await SendSessionEstablishedAsync(context, expectedFinish, connection, sequenceId).ConfigureAwait(false);
        }

        static ValueTask SendSessionEstablishedAsync(IPacketContext<SessionProof> context, Bytes32 expectedFinish, IConnection connection, ushort sequenceId)
        {
            PacketScope<SessionEstablished> lease = PacketFactory<SessionEstablished>.Acquire();
            try
            {
                SessionEstablished reply = lease.Value;

                reply.Initialize(expectedFinish, connection.ConnectionId);
                reply.SequenceId = sequenceId;

                return context.Sender.SendAsync(reply).DisposeOnCompletionAsync(lease);
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }
    }

    /// <summary>
    /// Initializes the handshake handlers with the default certificate.
    /// </summary>
    /// <remarks>
    /// This is called automatically by the host builder if no custom certificate path is specified.
    /// </remarks>
    public static void Initialize()
    {
        if (Volatile.Read(ref s_isInitialized) != 0)
        {
            return;
        }

        lock (s_initLock)
        {
            if (Volatile.Read(ref s_isInitialized) != 0)
            {
                return;
            }

            LoadCertificate(Path.Combine(Directories.ConfigurationDirectory, "certificate.private"));
            Volatile.Write(ref s_isInitialized, 1);
        }
    }

    /// <summary>
    /// Sets a custom path for the server identity certificate and initializes it.
    /// </summary>
    /// <param name="path">The absolute path to the certificate file.</param>
    public static void SetCertificatePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        lock (s_initLock)
        {
            LoadCertificate(path);
            Volatile.Write(ref s_isInitialized, 1);
        }
    }

    /// <summary>
    /// Configures the internal object pool limits for handshake contexts.
    /// </summary>
    /// <param name="maxCapacity">The maximum number of contexts to retain in the pool.</param>
    /// <param name="preallocCount">The number of contexts to preallocate immediately.</param>
    public static void ConfigureContextPool(int maxCapacity, int preallocCount = 0)
    {
        _ = s_pool.SetMaxCapacity<HandshakeContext>(maxCapacity);

        if (preallocCount > 0)
        {
            _ = s_pool.Prealloc<HandshakeContext>(preallocCount);
        }
    }

    #endregion APIs

    #region Fields

    private static int s_isInitialized;
    private static readonly Lock s_initLock = new();
    private static Bytes32 s_certificate = Bytes32.Zero;
    private static Bytes32 s_serverPublicKey = Bytes32.Zero;
    private static readonly object s_handshakeClaimToken = new();

    /// <summary>
    /// Gets the server's public key (derived from the certificate).
    /// </summary>
    public static Bytes32 ServerPublicKey => s_serverPublicKey;

    [Inject]
    private static ObjectPoolManager s_pool = null!;

    [Inject]
    private static ISessionService? s_sessionService;

    [Inject]
    private static IProofOfWorkPolicy? s_powPolicy;

    #endregion Fields

    #region Private Methods

    #region Nested Types

    private sealed class HandshakeContext : IPoolable
    {
        public Bytes32 SessionKey;
        public Bytes32 SharedSecret;
        public Bytes32 TranscriptHash;

        public void ResetForPool()
        {
            MemorySecurity.ZeroMemory(MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref SessionKey, 1)));
            MemorySecurity.ZeroMemory(MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref SharedSecret, 1)));
            MemorySecurity.ZeroMemory(MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref TranscriptHash, 1)));
        }
    }

    #endregion Nested Types

    private static ICertificateStore GetCertificateStore() =>
        InstanceManager.Instance.GetExistingInstance<ICertificateStore>() ??
        InstanceManager.Instance.GetOrCreateInstance<FileCertificateStore>();

    private static void LoadCertificate(string certPath)
    {
        ICertificateStore store = GetCertificateStore();
        try
        {
            s_certificate = store.Load(certPath);
            s_serverPublicKey = X25519.GenerateKeyFromPrivateKey(s_certificate).PublicKey;
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex) && ex is not InternalErrorException)
        {
            throw new InternalErrorException($"Handshake failed: Unable to load server identity from '{certPath}'. Exception detail: " + ex.Message, ex);
        }
    }

    private static ValueTask RejectHandshakeAsync(IConnection connection, IPacketSender sender, ProtocolReason reason)
    {
        RuntimeConnectionState runtimeState = connection.GetRuntimeState();

        if (runtimeState.HandshakeState is HandshakeContext contextState)
        {
            s_pool.Return(contextState);
        }

        runtimeState.HandshakeState = null;

        Control error = new();
        error.Initialize(ControlType.ERROR, reasonCode: reason, flags: PacketFlags.SYSTEM);

        ValueTask pending = sender.SendAsync(error);
        if (pending.IsCompletedSuccessfully)
        {
#pragma warning disable CA1849
            pending.GetAwaiter().GetResult();
#pragma warning restore CA1849
            error.Dispose();
            connection.Disconnect(reason.ToString());
            return default;
        }

        return AwaitRejectAsync(pending, connection, error, reason.ToString());

        static async ValueTask AwaitRejectAsync(ValueTask task, IConnection conn, Control err, string reasonStr)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            finally
            {
                err.Dispose();
                conn.Disconnect(reasonStr);
            }
        }
    }

    private static bool TryGetState(IConnection connection, [NotNullWhen(true)] out HandshakeContext? state)
    {
        RuntimeConnectionState runtimeState = connection.GetRuntimeState();

        if (runtimeState.HandshakeState is HandshakeContext typed)
        {
            state = typed;
            return true;
        }

        state = null;
        return false;
    }
    private static bool TryAcquireHandshakeSlot(IConnection connection)
    {
        RuntimeConnectionState runtimeState = ConnectionExtensions.GetRuntimeState(connection);
        return Interlocked.CompareExchange(ref runtimeState.HandshakeState, s_handshakeClaimToken, null) == null;
    }

    private static bool TryPublishHandshakeState(IConnection connection, HandshakeContext state)
    {
        RuntimeConnectionState runtimeState = ConnectionExtensions.GetRuntimeState(connection);
        return Interlocked.CompareExchange(ref runtimeState.HandshakeState, state, s_handshakeClaimToken) == s_handshakeClaimToken;
    }

    #endregion Private Methods
}
