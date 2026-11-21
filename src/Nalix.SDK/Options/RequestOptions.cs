// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
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
/// Each attempt gets its own <see cref="TimeoutMs"/> window; without a deadline the total wall-clock time is
/// at most <c>TimeoutMs × (RetryCount + 1)</c> plus any <see cref="RetryDelayMs"/>. Set <see cref="DeadlineMs"/>
/// to bound the whole call (waiting for a reconnect, every attempt and every retry delay); each attempt then waits
/// at most the time left. All attempts carry the same <c>SequenceId</c>, so a late reply to an earlier attempt still
/// completes the call.
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
    /// Overall budget in milliseconds for the whole call: waiting for a reconnect, every attempt and every retry
    /// delay. Each attempt waits at most <c>min(TimeoutMs, time left)</c>; no attempt starts once the budget is spent.
    /// <c>0</c> (default) means no overall deadline. Must be ≥ 0.
    /// </summary>
    public int DeadlineMs { get; init; }

    /// <summary>
    /// Base delay in milliseconds before a retry. Doubles on each further retry, with ±20% jitter, and never runs
    /// past <see cref="DeadlineMs"/>. <c>0</c> (default) retries immediately. Must be ≥ 0.
    /// </summary>
    public int RetryDelayMs { get; init; }

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
        if (this.DeadlineMs < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(this.DeadlineMs), this.DeadlineMs, $"{nameof(this.DeadlineMs)} must be >= 0.");
        }
        if (this.RetryDelayMs < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(this.RetryDelayMs), this.RetryDelayMs, $"{nameof(this.RetryDelayMs)} must be >= 0.");
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
    /// Returns a new <see cref="RequestOptions"/> with <see cref="DeadlineMs"/> changed.
    /// </summary>
    /// <param name="ms">The overall budget in milliseconds for the whole call; <c>0</c> for none.</param>
    public RequestOptions WithDeadline(int ms) => this with { DeadlineMs = ms };

    /// <summary>
    /// Returns a new <see cref="RequestOptions"/> with <see cref="RetryDelayMs"/> changed.
    /// </summary>
    /// <param name="ms">The base delay in milliseconds before a retry; doubles on each further retry.</param>
    public RequestOptions WithRetryDelay(int ms) => this with { RetryDelayMs = ms };

    /// <summary>
    /// Gets the wait for the next attempt, or <c>-1</c> once <see cref="DeadlineMs"/> is spent.
    /// Without a deadline this is <see cref="TimeoutMs"/>.
    /// </summary>
    internal int AttemptTimeoutMs(long startTimestamp)
    {
        if (this.DeadlineMs <= 0)
        {
            return this.TimeoutMs;
        }

        long left = this.DeadlineMs - (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        if (left <= 0)
        {
            return -1;
        }

        return this.TimeoutMs == 0 ? (int)left : (int)Math.Min(this.TimeoutMs, left);
    }

    /// <summary>
    /// Waits before retry number <paramref name="retry"/> (1 for the first retry). Returns <see langword="false"/>
    /// when the deadline leaves no time for another attempt.
    /// </summary>
    internal async ValueTask<bool> DelayBeforeRetryAsync(int retry, long startTimestamp, CancellationToken ct)
    {
        if (this.RetryDelayMs > 0)
        {
            double delay = this.RetryDelayMs * Math.Pow(2, retry - 1) * (0.8 + (Random.Shared.NextDouble() * 0.4));
            int delayMs = (int)Math.Min(delay, int.MaxValue);

            if (this.DeadlineMs > 0)
            {
                int left = this.AttemptTimeoutMs(startTimestamp);
                if (left < 0 || delayMs >= left)
                {
                    return false;
                }
            }

            await Task.Delay(delayMs, ct).ConfigureAwait(false);
        }

        return this.AttemptTimeoutMs(startTimestamp) >= 0;
    }
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
