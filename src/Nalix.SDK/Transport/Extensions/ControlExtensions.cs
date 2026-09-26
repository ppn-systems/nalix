// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Codec.ProtocolFrames;
using Nalix.Environment.Time;
using Nalix.SDK.Transport.Internal;

namespace Nalix.SDK.Transport.Extensions;

/// <summary>
/// Provides client-side helpers for CONTROL frames, including a fluent <see cref="ControlBuilder"/>,
/// a matcher via <see cref="AwaitControlAsync"/>, and a fluent sender via <see cref="SendControlAsync"/>.
/// </summary>
/// <remarks>
/// These helpers use <see cref="Clock"/> for stamping and monotonic timing to achieve robust RTT measurements.
/// </remarks>
/// <seealso cref="Control"/>
/// <seealso cref="Clock"/>
/// <seealso cref="TcpSession"/>
[SkipLocalsInit]
public static class ControlExtensions
{
    /// <summary>
    /// A fluent builder for <see cref="Control"/> frames.
    /// Use <see cref="NewControl"/> to create an instance,
    /// then chain configuration methods before calling <see cref="Build"/>.
    /// </summary>
    /// <param name="c">The control instance being configured.</param>
    /// <remarks>
    /// This is a <see langword="ref struct"/> — it cannot be captured in lambdas or stored on the heap.
    /// Use <see cref="Build"/> to materialize the <see cref="Control"/> before passing it to async code.
    /// </remarks>
    public readonly ref struct ControlBuilder(Control c)
    {
        /// <summary>Sets the sequence identifier.</summary>
        /// <param name="seq">The sequence identifier to assign.</param>
        /// <returns>The current builder.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ControlBuilder WithSeq(ushort seq) { c.SequenceId = seq; return this; }

        /// <summary>Sets the reason code.</summary>
        /// <param name="reason">The protocol reason code.</param>
        /// <returns>The current builder.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ControlBuilder WithReason(ProtocolReason reason) { c.Reason = reason; return this; }

        /// <summary>Builds and returns the configured <see cref="Control"/> instance.</summary>
        /// <returns>The configured <see cref="Control"/>.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Control Build() => c;
    }

    /// <summary>
    /// Creates a new CONTROL frame with the specified type.
    /// The frame is pre-stamped with the current time via <see cref="Control.Initialize(ControlType, ushort, PacketFlags, ProtocolReason)"/>.
    /// </summary>
    /// <param name="_">The client connection (unused; provided for fluent extension syntax).</param>
    /// <param name="type">The control type.</param>
    /// <returns>A <see cref="ControlBuilder"/> initialized with the requested type.</returns>
    /// <example>
    /// <code>
    /// Control ping = client.NewControl(opCode, ControlType.PING).WithSeq(123).Build();
    /// </code>
    /// </example>
    public static ControlBuilder NewControl(this ITransportSession _, ControlType type)
    {
#pragma warning disable CA2000 // Ownership is transferred to ControlBuilder; callers own/dispose the materialized Control returned by Build().
        Control c = Control.Create();
#pragma warning restore CA2000
        // Initialize already stamps MonoTicks + Timestamp internally.
        c.Initialize(type, sequenceId: 0, flags: PacketFlags.SYSTEM, reasonCode: ProtocolReason.NONE);
        return new ControlBuilder(c);
    }

    /// <summary>
    /// Awaits until a packet of type <typeparamref name="TPkt"/> satisfying the predicate arrives,
    /// a timeout occurs, or the connection is dropped.
    /// </summary>
    /// <typeparam name="TPkt">The expected packet type.</typeparam>
    /// <param name="client">The connected client.</param>
    /// <param name="predicate">A predicate that returns <c>true</c> for the desired packet.</param>
    /// <param name="timeoutMs">Maximum wait time in milliseconds.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>The first matching packet.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="client"/> or <paramref name="predicate"/> is <c>null</c>.
    /// </exception>
    /// <exception cref="NetworkException">Thrown when the client is not connected.</exception>
    /// <exception cref="TimeoutException">Thrown when no matching packet is received within <paramref name="timeoutMs"/>.</exception>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="ct"/> is canceled.</exception>
    public static ValueTask<TPkt> AwaitPacketAsync<TPkt>(
        this ITransportSession client,
        Func<TPkt, bool> predicate,
        int timeoutMs,
        CancellationToken ct = default)
        where TPkt : class, IPacket, IPacketStaticOpcode
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(predicate);

        if (!client.IsConnected)
        {
            throw new NetworkException("Client not connected.");
        }

        // Delegate all TCS + subscribe + timeout logic to PacketAwaiter.
        // sendAsync = no-op because the caller has already sent (or will send externally).
        return PacketAwaiter.AwaitAsync(client, predicate, timeoutMs, sendAsync: _ => Task.CompletedTask, ct);
    }

    /// <summary>
    /// Awaits until a CONTROL frame matching the specified predicate is received, or a timeout occurs.
    /// Non-matching packets are ignored.
    /// </summary>
    /// <param name="client">The connected client.</param>
    /// <param name="predicate">A predicate that returns <c>true</c> for the desired CONTROL.</param>
    /// <param name="timeoutMs">The maximum time to wait, in milliseconds.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>The first CONTROL packet matching <paramref name="predicate"/>.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="client"/> or <paramref name="predicate"/> is <c>null</c>.
    /// </exception>
    /// <exception cref="TimeoutException">Thrown when no matching CONTROL is received within <paramref name="timeoutMs"/>.</exception>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="ct"/> is canceled.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ValueTask<Control> AwaitControlAsync(
        this ITransportSession client,
        Func<Control, bool> predicate,
        int timeoutMs,
        CancellationToken ct = default)
        => AwaitPacketAsync(client, predicate, timeoutMs, ct);

    /// <summary>
    /// Sends a CONTROL frame using a fluent configuration callback.
    /// </summary>
    /// <param name="client">The connected reliable client.</param>
    /// <param name="type">The control type to send.</param>
    /// <param name="configure">
    /// An optional callback to customize the built <see cref="Control"/> before sending.
    /// Receives the materialized <see cref="Control"/> instance (not the <see langword="ref struct"/> builder)
    /// to avoid stack-capture restrictions.
    /// </param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous send operation.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="client"/> is <c>null</c>.</exception>
    /// <exception cref="NetworkException">Thrown when the client is not connected.</exception>
    /// <example>
    /// <code>
    /// await client.SendControlAsync(
    ///     opCode: 0,
    ///     type: ControlType.NOTICE,
    ///     configure: ctrl => { ctrl.SequenceId = 42; ctrl.Reason = ProtocolReason.NONE; },
    ///     ct: ct);
    /// </code>
    /// </example>
    public static async ValueTask SendControlAsync(this ITransportSession client, ControlType type, Action<Control>? configure = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        if (!client.IsConnected)
        {
            throw new NetworkException("Client not connected.");
        }

        // Materialize the Control from the builder first; ref structs cannot be lambda-captured.
        using Control ctrl = client.NewControl(type).Build();
        configure?.Invoke(ctrl);
        await client.SendAsync(ctrl, encrypt: false, ct).ConfigureAwait(false);
    }
}
