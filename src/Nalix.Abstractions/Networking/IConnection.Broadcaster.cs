// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nalix.Abstractions.Networking;

/// <summary>
/// Provides mechanisms to broadcast messages and packets to multiple connections efficiently.
/// </summary>
public interface IConnectionBroadcaster
{
    /// <summary>
    /// Broadcasts a message to all active connections using a generic sender, allowing zero-allocation high-performance loops.
    /// </summary>
    /// <typeparam name="TState">The type of the state passed to the sender.</typeparam>
    /// <typeparam name="TSender">The type of the sender struct implementing <see cref="IConnectionSender{TState}"/>.</typeparam>
    /// <param name="state">The state to pass to the sender.</param>
    /// <param name="sender">The sender struct instance.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous broadcast operation.</returns>
    Task BroadcastAsync<TState, TSender>(TState state, TSender sender, CancellationToken cancellationToken = default)
        where TSender : struct, IConnectionSender<TState>;

    /// <summary>
    /// Multicasts a message to a specific connection group using a generic sender, allowing zero-allocation high-performance loops.
    /// </summary>
    /// <typeparam name="TState">The type of the state passed to the sender.</typeparam>
    /// <typeparam name="TSender">The type of the sender struct implementing <see cref="IConnectionSender{TState}"/>.</typeparam>
    /// <param name="groupProvider">The connection group provider to use for resolving group members.</param>
    /// <param name="groupName">The name of the group to receive the message.</param>
    /// <param name="state">The state to pass to the sender.</param>
    /// <param name="sender">The sender struct instance.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous multicast operation.</returns>
    Task MulticastAsync<TState, TSender>(IConnectionGroupRegistry groupProvider, string groupName, TState state, TSender sender, CancellationToken cancellationToken = default)
        where TSender : struct, IConnectionSender<TState>;

    /// <summary>
    /// Multicasts a message to a specific connection group using a generic sender, excluding a specific connection.
    /// </summary>
    /// <typeparam name="TState">The type of the state passed to the sender.</typeparam>
    /// <typeparam name="TSender">The type of the sender struct implementing <see cref="IConnectionSender{TState}"/>.</typeparam>
    /// <param name="groupProvider">The connection group provider to use for resolving group members.</param>
    /// <param name="groupName">The name of the group to receive the message.</param>
    /// <param name="excludedConnection">The connection to exclude from the broadcast.</param>
    /// <param name="state">The state to pass to the sender.</param>
    /// <param name="sender">The sender struct instance.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous multicast operation.</returns>
    Task MulticastExceptAsync<TState, TSender>(IConnectionGroupRegistry groupProvider, string groupName, IConnection excludedConnection, TState state, TSender sender, CancellationToken cancellationToken = default)
        where TSender : struct, IConnectionSender<TState>;

    /// <summary>
    /// Broadcasts a message to all active connections.
    /// </summary>
    /// <param name="message">The pre-serialized message buffer to broadcast.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous broadcast operation.</returns>
    [Obsolete(
        "This overload sends a pre-serialized buffer and bypasses the normal compression and encryption pipeline. Use the packet-based multicast overload instead.",
        error: false,
        DiagnosticId = "NALIX_NET001")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = "Existing overload; only the UDP transport parameter was removed.")]
    Task BroadcastAsync(
        ReadOnlyMemory<byte> message,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Multicasts a pre-serialized message buffer to a specific connection group.
    /// </summary>
    /// <param name="groupProvider">The connection group provider to use for resolving group members.</param>
    /// <param name="groupName">The name of the group to receive the message.</param>
    /// <param name="message">The pre-serialized message buffer to multicast.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous multicast operation.</returns>
    [Obsolete(
        "This overload sends a pre-serialized buffer and bypasses the normal compression and encryption pipeline. Use the packet-based multicast overload instead.",
        error: false,
        DiagnosticId = "NALIX_NET001")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = "Existing overload; only the UDP transport parameter was removed.")]
    Task MulticastAsync(
        IConnectionGroupRegistry groupProvider,
        string groupName,
        ReadOnlyMemory<byte> message,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Multicasts a pre-serialized message buffer to a specific connection group, excluding a specific connection.
    /// </summary>
    /// <param name="groupProvider">The connection group provider to use for resolving group members.</param>
    /// <param name="groupName">The name of the group to receive the message.</param>
    /// <param name="excludedConnection">The connection to exclude from the broadcast.</param>
    /// <param name="message">The pre-serialized message buffer to multicast.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous multicast operation.</returns>
    [Obsolete(
        "This overload sends a pre-serialized buffer and bypasses the normal compression and encryption pipeline. Use the packet-based multicast overload instead.",
        error: false,
        DiagnosticId = "NALIX_NET001")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = "Existing overload; only the UDP transport parameter was removed.")]
    Task MulticastExceptAsync(
        IConnectionGroupRegistry groupProvider,
        string groupName,
        IConnection excludedConnection,
        ReadOnlyMemory<byte> message,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Broadcasts a message to connections matching the given predicate.
    /// </summary>
    /// <param name="message">The pre-serialized message buffer to broadcast.</param>
    /// <param name="predicate">The condition to match connections.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous broadcast operation.</returns>
    [Obsolete(
        "This overload sends a pre-serialized buffer and bypasses the normal compression and encryption pipeline. Use the packet-based multicast overload instead.",
        error: false,
        DiagnosticId = "NALIX_NET001")]
    Task BroadcastWhereAsync(
        ReadOnlyMemory<byte> message,
        Func<IConnection, bool> predicate,
        CancellationToken cancellationToken = default);
}
