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
}
