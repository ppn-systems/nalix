// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Nalix.Abstractions.Networking;

/// <summary>
/// Processes incoming frames for a connection.
/// </summary>
public interface IFrameProcessor
{
    /// <summary>
    /// Processes a received frame.
    /// </summary>
    /// <param name="sender">
    /// The source that raised the frame event.
    /// </param>
    /// <param name="args">
    /// The connection event arguments.
    /// </param>
    void ProcessFrame(object? sender, IConnectionEventArgs args);

    /// <summary>
    /// Gets a value indicating whether <see cref="ProcessFrame"/> is short and non-blocking
    /// (for example, it only transforms the frame and enqueues it for dispatch), so the transport
    /// may call it directly on the receive completion instead of queuing it to the thread pool.
    /// </summary>
    /// <remarks>
    /// Defaults to <see langword="false"/>. Return <see langword="true"/> only when the method never
    /// blocks and never runs application handlers inline: while it runs, the connection does not
    /// receive its next frame.
    /// </remarks>
    bool SupportsInlineProcessing => false;

    /// <summary>
    /// Called when the transport drops a received frame instead of passing it to <see cref="ProcessFrame"/>, because the
    /// connection's or the remote address's pending-frame budget is full (see <c>NetworkCallbackOptions</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Lets the protocol answer the dropped request (for example with a retryable rate-limit error) instead of leaving
    /// the client to time out. The default implementation does nothing, which keeps the previous silent drop.
    /// </para>
    /// <para>
    /// Runs on the receive path while the connection is overloaded: it must not block, and it should not answer every
    /// rejected frame of a flood. <paramref name="frame"/> is the raw frame as received (header in plain text, body still
    /// encrypted or compressed) and is only valid for the duration of the call.
    /// </para>
    /// </remarks>
    /// <param name="connection">The connection the frame arrived on.</param>
    /// <param name="frame">The rejected frame.</param>
    void OnFrameRejected(IConnection connection, System.ReadOnlySpan<byte> frame) { }
}
