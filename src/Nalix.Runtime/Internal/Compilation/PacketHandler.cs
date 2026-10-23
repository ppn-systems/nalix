// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Security;
using Nalix.Runtime.Dispatching;

namespace Nalix.Runtime.Internal.Compilation;

/// <summary>
/// Immutable dispatch record that pairs packet metadata with the compiled invoker
/// used to execute a packet handler without reflection on the hot path.
/// </summary>
/// <typeparam name="TPacket">The packet type handled by this delegate.</typeparam>
/// <param name="opCode">The opcode mapped to this handler.</param>
/// <param name="metadata">Dispatch metadata used for runtime policies.</param>
/// <param name="controllerInstance">The controller instance that owns the handler method.</param>
/// <param name="methodName">The name of the handler method.</param>
/// <param name="returnType">The handler return type.</param>
/// <param name="compiledInvoker">Compiled delegate used to invoke the handler.</param>
/// <param name="expectedPacketType">
/// Cached concrete packet runtime type expected by the handler, or <see langword="null"/>
/// when runtime packet type checks are not required.
/// </param>
[StructLayout(LayoutKind.Sequential)]
[EditorBrowsable(EditorBrowsableState.Never)]
[method: MethodImpl(MethodImplOptions.AggressiveInlining)]
internal readonly struct PacketHandler<TPacket>(
    ushort opCode, PacketMetadata metadata,
    object? controllerInstance, string methodName, Type returnType,
    Func<object?, PacketContext<TPacket>, ValueTask<object?>> compiledInvoker,
    Type? expectedPacketType) where TPacket : IPacket
{
    #region Fields

    /// <summary>
    /// The OpCode associated with this packet handler.
    /// </summary>
    public readonly ushort OpCode = opCode;

    /// <summary>
    /// The return type of the handler method.
    /// </summary>
    public readonly Type ReturnType = returnType;

    /// <summary>
    /// Metadata for this handler, including timeout, rate limiting, and permissions.
    /// </summary>
    public readonly PacketMetadata Metadata = metadata;

    /// <summary>
    /// The controller instance to invoke the handler on (cached for reuse).
    /// </summary>
    public readonly object? Instance = controllerInstance;

    /// <summary>
    /// The name of the handler method, useful for debugging or logging.
    /// </summary>
    public readonly string MethodName = methodName;

    /// <summary>
    /// A compiled delegate for invoking the handler directly.
    /// This is the performance-critical entry point used every time a packet is dispatched.
    /// It avoids reflection, parameter boxing, and per-call delegate allocation.
    /// </summary>
    public readonly Func<object?, PacketContext<TPacket>,
                    ValueTask<object?>> Invoker = compiledInvoker;

    /// <summary>
    /// Concrete packet type expected by this handler, or <see langword="null"/>
    /// when no strict runtime type check is required.
    /// </summary>
    public readonly Type? ExpectedPacketType = expectedPacketType;

    #endregion Fields

    #region Methods

    /// <summary>
    /// Executes the handler using the compiled delegate for maximum performance.
    /// This is the zero-allocation path that the dispatcher calls for every packet.
    /// </summary>
    /// <param name="context">The packet context containing the request and metadata.</param>
    /// <returns>
    /// A <see cref="ValueTask{TResult}"/> that completes with the handler’s result.
    /// </returns>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public ValueTask<object?> ExecuteAsync(PacketContext<TPacket> context) => Invoker(Instance, context);

    /// <summary>
    /// Determines whether this handler can be executed for the specified packet context.
    /// </summary>
    /// <param name="context">The packet context to validate for execution.</param>
    /// <param name="denyReason">
    /// When this method returns <see langword="false"/>, the protocol reason that should be
    /// reported back to the caller for the denial.
    /// </param>
    /// <returns><see langword="true"/> if the handler can be executed; otherwise, <see langword="false"/>.</returns>
    /// <remarks>
    /// This method can be extended to implement validation logic such as:
    /// <list type="bullet">
    /// <item><description>Permission checks</description></item>
    /// <item><description>Rate limiting</description></item>
    /// <item><description>Custom filters</description></item>
    /// </list>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public bool CanExecute(PacketContext<TPacket> context, out ProtocolReason denyReason)
    {
        // SEC-77: Enforce permission policy by default on the hot path.
        // Middleware is still recommended for logging and more complex policies,
        // but this provides a fail-closed baseline in the dispatcher itself.
        if (Metadata.Permission is { } permission &&
            !IsAuthorized(context.Connection.Level, permission))
        {
            denyReason = ProtocolReason.UNAUTHORIZED;
            return false;
        }

        // Enforce declared encryption requirement: a handler marked
        // [PacketEncryption(true)] must not execute for a frame that
        // did not arrive encrypted on the wire. The header's ENCRYPTED flag
        // is transient transform-internal bookkeeping and is cleared once the
        // inbound cipher transforms finish decrypting, so it cannot be used
        // here. PacketContext.EncryptedOnWire is a dedicated signal captured
        // once at decrypt time (see FrameCipher.DecryptFrame / TryDecryptFrame)
        // and propagated across subsequent transforms (e.g. decompression).
        if (Metadata.Encryption is { IsEncrypted: true } && !context.EncryptedOnWire)
        {
            denyReason = ProtocolReason.FORBIDDEN;
            return false;
        }

        denyReason = ProtocolReason.NONE;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsAuthorized(PermissionLevel actorLevel, PacketPermissionAttribute policy) =>
        policy.Evaluation switch
        {
            PermissionEvaluation.StrictMatch => actorLevel == policy.Level,
            PermissionEvaluation.MinimumLevel => actorLevel >= policy.Level,
            _ => false
        };

    #endregion Methods
}
