// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Threading;
using Nalix.Abstractions.Networking.Packets;
using Nalix.SDK.Transport;
using Nalix.SDK.Transport.Extensions;

namespace Nalix.SDK.Options;

/// <summary>
/// Controls the behaviour of <see cref="RequestExtensions.RequestAsync{TResponse}(ITransportSession, IPacket, RequestOptions, Func{TResponse, bool}, CancellationToken)"/>.
/// </summary>
/// <remarks>
/// <para>
/// Use the static factory <see cref="Default"/> for a ready-made instance,
/// or build one with the fluent helpers:
/// <code>
/// var opts = RequestOptions.Default
///     .WithTimeout(3_000)
///     .WithRetry(2)
///     .WithEncrypt();
/// </code>
/// </para>
/// <para>
/// <b>Retry semantics:</b> a retry is triggered only on <see cref="TimeoutException"/>.
/// Fatal errors (dropped connection, send failure) propagate immediately — they are never retried.
/// Each attempt gets its own <see cref="TimeoutMs"/> window; total wall-clock time is at most
/// <c>TimeoutMs × (RetryCount + 1)</c>.
/// </para>
/// <para>
/// <b>WARNING:</b> Retries are safe only for idempotent requests. A timeout does not prove the
/// server did not receive or process the request. Retrying non-idempotent operations may result
/// in duplicate side effects on the server.
/// </para>
/// </remarks>
public sealed record RequestOptions
{
    // ── Defaults ─────────────────────────────────────────────────────────────

    /// <summary>Default timeout in milliseconds (5 000 ms).</summary>
    public const int DefaultTimeoutMs = 5_000;

    /// <summary>Ready-made instance with sensible defaults (no retry, encryption per <see cref="TransportOptions.EncryptRequestsByDefault"/>).</summary>
    public static RequestOptions Default { get; } = new();

    // ── Properties ───────────────────────────────────────────────────────────

    /// <summary>
    /// Milliseconds to wait for a response on each attempt.
    /// Use <c>0</c> to wait indefinitely (not recommended in production).
    /// </summary>
    public int TimeoutMs { get; init; } = DefaultTimeoutMs;

    /// <summary>
    /// Number of additional attempts after the first one times out.
    /// <c>0</c> means try once and stop. Must be ≥ 0.
    /// <para>
    /// <b>WARNING:</b> Configuring retries (value > 0) is safe only for idempotent requests.
    /// Because a timeout does not guarantee that the server did not receive or process the initial request,
    /// retrying non-idempotent operations may duplicate side effects. This is not enforced at runtime —
    /// mark request/response packet types with <see cref="Nalix.Abstractions.Networking.Packets.PacketIdempotentAttribute"/>
    /// as documentation of which packets are actually safe to retry.
    /// </para>
    /// </summary>
    public int RetryCount { get; init; }

    /// <summary>
    /// Whether the outbound request frame is encrypted.
    /// <list type="bullet">
    /// <item><description><see langword="true"/>: always encrypt (requires a completed handshake).</description></item>
    /// <item><description><see langword="false"/>: never encrypt, e.g. when the transport is already wrapped in TLS.</description></item>
    /// <item><description><see langword="null"/> (default): use <see cref="TransportOptions.EncryptRequestsByDefault"/>;
    /// when that is enabled the request follows the session's encryption state.</description></item>
    /// </list>
    /// </summary>
    public bool? Encrypt { get; init; }

    // ── Validation ───────────────────────────────────────────────────────────

    /// <summary>
    /// Throws <see cref="ArgumentOutOfRangeException"/> if <see cref="RetryCount"/> is negative.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    public void Validate()
    {
        if (this.TimeoutMs < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(this.TimeoutMs), this.TimeoutMs, $"{nameof(this.TimeoutMs)} must be >= 0.");
        }
        if (this.RetryCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(this.RetryCount), this.RetryCount, $"{nameof(this.RetryCount)} must be >= 0.");
        }
    }

    // ── Fluent builders ───────────────────────────────────────────────────────

    /// <summary>
    /// Returns a new <see cref="RequestOptions"/> with <see cref="TimeoutMs"/> changed.
    /// </summary>
    /// <param name="ms">The timeout in milliseconds for each attempt.</param>
    public RequestOptions WithTimeout(int ms) => this with { TimeoutMs = ms };

    /// <summary>
    /// Returns a new <see cref="RequestOptions"/> with <see cref="RetryCount"/> changed.
    /// </summary>
    /// <param name="count">The number of retries after the first attempt times out.</param>
    public RequestOptions WithRetry(int count) => this with { RetryCount = count };

    /// <summary>
    /// Returns a new <see cref="RequestOptions"/> with <see cref="Encrypt"/> changed.
    /// </summary>
    /// <param name="encrypt">Whether to encrypt outbound frames; <see langword="null"/> restores the session default.</param>
    public RequestOptions WithEncrypt(bool? encrypt = true) => this with { Encrypt = encrypt };

    /// <summary>
    /// Resolves <see cref="Encrypt"/> for <paramref name="client"/>: an explicit value wins; otherwise
    /// <see langword="null"/> (follow the session state) when <see cref="TransportOptions.EncryptRequestsByDefault"/>
    /// is enabled, and <see langword="false"/> when it is not.
    /// </summary>
    internal bool? ResolveEncrypt(ITransportSession client)
        => this.Encrypt ?? (client is TransportSession { Options.EncryptRequestsByDefault: true } ? null : false);
}
