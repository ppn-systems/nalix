// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics;

namespace Nalix.SDK;

/// <summary>
/// Central registry for all diagnostic event names used within the SDK module.
///
/// Design goals:
/// - Provide strongly-typed event names (avoid magic strings)
/// - Keep naming consistent across modules
/// - Reduce duplication of prefixes (namespace already scopes context)
/// </summary>
/// <remarks>
/// Every event carries a <see cref="Nalix.Abstractions.Diagnostics.DiagnosticLog"/> payload. Subscribe to the
/// <see cref="ListenerName"/> listener through <see cref="DiagnosticListener.AllListeners"/> to observe them.
/// </remarks>
public static class DiagnosticsEvents
{
    /// <summary>
    /// The name of the <see cref="DiagnosticListener"/> used by the SDK module.
    /// Consumers can subscribe to this listener to observe all emitted events.
    /// </summary>
    public const string ListenerName = "SDK";

    /// <summary>
    /// Global diagnostic source for emitting events.
    /// </summary>
    public static readonly DiagnosticListener Source = Environment.Diagnostics.DiagnosticListenerFactory.Create(ListenerName);

    /// <summary>
    /// Writes a diagnostic event payload through <see cref="Source"/> in an AOT-safe manner.
    /// </summary>
    /// <typeparam name="T">The diagnostic payload type.</typeparam>
    /// <param name="name">The event name.</param>
    /// <param name="payload">The event payload.</param>
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "DiagnosticSource.Write<T>() requires unreferenced-code analysis for payload property discovery. " +
            "Nalix diagnostic payloads are observational only and do not affect runtime behavior.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "Trimming",
        "IL2091",
        Justification = "Diagnostic payloads are observational only; public-property preservation is not required for runtime behavior.")]
    public static void Write<T>(string name, T payload) => Source.Write(name, payload);

    /// <summary>
    /// Connection lifecycle events of TCP, WebSocket and datagram sessions.
    /// </summary>
    public static class Session
    {
        /// <summary>
        /// Fired before a session starts connecting.
        /// </summary>
        public const string Connecting = "Session.Connecting";

        /// <summary>
        /// Fired when a session has connected.
        /// </summary>
        public const string Connected = "Session.Connected";

        /// <summary>
        /// Fired when a connection attempt fails.
        /// </summary>
        public const string ConnectFailed = "Session.ConnectFailed";

        /// <summary>
        /// Fired when a session has disconnected.
        /// </summary>
        public const string Disconnected = "Session.Disconnected";

        /// <summary>
        /// Fired when a transport error closes the session.
        /// </summary>
        public const string TransportError = "Session.TransportError";
    }

    /// <summary>
    /// Handshake and server identity events.
    /// </summary>
    public static class Handshake
    {
        /// <summary>
        /// Fired when a handshake starts.
        /// </summary>
        public const string Started = "Handshake.Started";

        /// <summary>
        /// Fired when the server requests a proof-of-work.
        /// </summary>
        public const string ProofOfWorkRequested = "Handshake.ProofOfWorkRequested";

        /// <summary>
        /// Fired when a server key is trusted on first use (it arrived over an unauthenticated channel).
        /// </summary>
        public const string TofuTrusted = "Handshake.TofuTrusted";

        /// <summary>
        /// Fired when a trusted-on-first-use server key is saved to the configuration.
        /// </summary>
        public const string TofuPersisted = "Handshake.TofuPersisted";

        /// <summary>
        /// Fired when a trusted-on-first-use server key is not saved (disabled, browser, or failure).
        /// </summary>
        public const string TofuNotPersisted = "Handshake.TofuNotPersisted";

        /// <summary>
        /// Fired when the server's proof does not verify (possible man-in-the-middle).
        /// </summary>
        public const string ProofInvalid = "Handshake.ProofInvalid";

        /// <summary>
        /// Fired when a handshake completes and the session is encrypted.
        /// </summary>
        public const string Completed = "Handshake.Completed";

        /// <summary>
        /// Fired when a handshake fails.
        /// </summary>
        public const string Failed = "Handshake.Failed";
    }

    /// <summary>
    /// Session resume events.
    /// </summary>
    public static class Resumption
    {
        /// <summary>
        /// Fired when a session is resumed.
        /// </summary>
        public const string Resumed = "Resumption.Resumed";

        /// <summary>
        /// Fired when the server declines to resume a session.
        /// </summary>
        public const string Declined = "Resumption.Declined";

        /// <summary>
        /// Fired when a resume attempt fails (canceled, timed out, network error, invalid state).
        /// </summary>
        public const string Failed = "Resumption.Failed";

        /// <summary>
        /// Fired when the resume response proof does not verify.
        /// </summary>
        public const string ProofInvalid = "Resumption.ProofInvalid";

        /// <summary>
        /// Fired when a failed resume falls back to a full handshake.
        /// </summary>
        public const string Fallback = "Resumption.Fallback";
    }

    /// <summary>
    /// Automatic reconnect events.
    /// </summary>
    public static class Reconnect
    {
        /// <summary>
        /// Fired when a reconnect attempt is scheduled.
        /// </summary>
        public const string Scheduled = "Reconnect.Scheduled";

        /// <summary>
        /// Fired when a reconnect attempt fails.
        /// </summary>
        public const string AttemptFailed = "Reconnect.AttemptFailed";

        /// <summary>
        /// Fired when the session has reconnected.
        /// </summary>
        public const string Succeeded = "Reconnect.Succeeded";

        /// <summary>
        /// Fired when automatic reconnect gives up.
        /// </summary>
        public const string GaveUp = "Reconnect.GaveUp";
    }

    /// <summary>
    /// Keepalive events.
    /// </summary>
    public static class KeepAlive
    {
        /// <summary>
        /// Fired when a keepalive PONG is missed.
        /// </summary>
        public const string Missed = "KeepAlive.Missed";

        /// <summary>
        /// Fired when keepalive gives up and closes the session.
        /// </summary>
        public const string Failed = "KeepAlive.Failed";
    }

    /// <summary>
    /// Session key rotation events.
    /// </summary>
    public static class Rekey
    {
        /// <summary>
        /// Fired when a key rotation starts.
        /// </summary>
        public const string Started = "Rekey.Started";

        /// <summary>
        /// Fired when a key rotation is acknowledged.
        /// </summary>
        public const string Completed = "Rekey.Completed";

        /// <summary>
        /// Fired when a key rotation fails or is not acknowledged.
        /// </summary>
        public const string Failed = "Rekey.Failed";
    }

    /// <summary>
    /// Request/response events.
    /// </summary>
    public static class Requests
    {
        /// <summary>
        /// Fired when a request is retried after a timeout.
        /// </summary>
        public const string Retrying = "Requests.Retrying";

        /// <summary>
        /// Fired when a request fails.
        /// </summary>
        public const string Failed = "Requests.Failed";

        /// <summary>
        /// Fired when a pending request cannot handle an inbound frame.
        /// </summary>
        public const string InboundFrameFailed = "Requests.InboundFrameFailed";
    }

    /// <summary>
    /// Per-frame transport events (high volume).
    /// </summary>
    public static class Frames
    {
        /// <summary>
        /// Fired for every frame sent.
        /// </summary>
        public const string Sent = "Frames.Sent";

        /// <summary>
        /// Fired for every frame received and delivered.
        /// </summary>
        public const string Received = "Frames.Received";

        /// <summary>
        /// Fired when an inbound frame is dropped by the replay window.
        /// </summary>
        public const string ReplayRejected = "Frames.ReplayRejected";
    }

    /// <summary>
    /// Packet subscription events.
    /// </summary>
    public static class Subscriptions
    {
        /// <summary>
        /// Fired when a subscription handler throws or its task faults.
        /// </summary>
        public const string HandlerFailed = "Subscriptions.HandlerFailed";

        /// <summary>
        /// Fired when an asynchronous subscription handler is canceled.
        /// </summary>
        public const string HandlerCanceled = "Subscriptions.HandlerCanceled";

        /// <summary>
        /// Fired when an exact-type subscription receives another packet type.
        /// </summary>
        public const string UnexpectedPacket = "Subscriptions.UnexpectedPacket";

        /// <summary>
        /// Fired when disposing a subscription fails.
        /// </summary>
        public const string DisposeFailed = "Subscriptions.DisposeFailed";
    }
}
