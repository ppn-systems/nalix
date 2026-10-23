// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.SDK.Transport.Extensions;

namespace Nalix.SDK.Transport.Internal;

/// <summary>
/// Drives automatic reconnection for a <see cref="TransportSession"/> when
/// <see cref="Options.TransportOptions.AutoReconnectEnabled"/> is set. Reconnects
/// with exponential backoff + jitter, reuses <see cref="ResumeExtensions.ConnectWithResumeDetailedAsync"/>
/// for the resume/handshake flow, and invokes the session's re-auth hook after a
/// fresh handshake (not a resume) before signalling readiness.
/// </summary>
internal sealed class ReconnectSupervisor
{
    private readonly TransportSession _session;
    private readonly Random _jitter = new();

    private volatile TaskCompletionSource<bool>? _readyTcs;

    public ReconnectSupervisor(TransportSession session)
    {
        _session = session;
        _session.OnDisconnected += this.OnDisconnected;
    }

    /// <summary>
    /// Gets a value indicating whether an automatic reconnect loop is currently in flight.
    /// </summary>
    public bool IsReconnecting => _readyTcs is not null;

    /// <summary>
    /// Awaits until the session is connected and (if a fresh handshake occurred) re-authenticated.
    /// Completes immediately if the session is already connected and no reconnect is in flight.
    /// </summary>
    public Task ReadyAsync(CancellationToken ct)
    {
        TaskCompletionSource<bool>? tcs = _readyTcs;
        return tcs is null ? Task.CompletedTask : tcs.Task.WaitAsync(ct);
    }

    // OnDisconnected fires for both unexpected drops and explicit DisconnectAsync() calls.
    // Concrete sessions should call TransportSession.MarkIntentionalDisconnect() from DisconnectAsync()
    // so ConsumeIntentionalDisconnect() can prevent auto-reconnect for app-initiated disconnects.
    private void OnDisconnected(object? sender, Exception ex)
    {
        if (!_session.Options.AutoReconnectEnabled)
        {
            return;
        }

        if (_session.ConsumeIntentionalDisconnect())
        {
            // Application called DisconnectAsync() itself (e.g. logout) — do not reconnect.
            return;
        }

        TaskCompletionSource<bool> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref _readyTcs, tcs, null) is not null)
        {
            // A reconnect loop is already in flight.
            return;
        }

        _ = this.ReconnectLoopAsync(tcs);
    }

    private async Task ReconnectLoopAsync(TaskCompletionSource<bool> tcs)
    {
        int attempt = 0;
        int maxAttempts = _session.Options.ReconnectMaxAttempts;
        int baseDelay = _session.Options.ReconnectBaseDelayMillis;
        int maxDelay = _session.Options.ReconnectMaxDelayMillis;

        while (maxAttempts <= 0 || attempt < maxAttempts)
        {
            if (!_session.Options.AutoReconnectEnabled)
            {
                break;
            }

            attempt++;

            int delay = (int)Math.Min(maxDelay, baseDelay * Math.Pow(2, attempt - 1));
            delay = (int)(delay * (0.8 + (_jitter.NextDouble() * 0.4))); // +/-20% jitter

            try
            {
                await Task.Delay(delay).ConfigureAwait(false);

                if (!_session.Options.AutoReconnectEnabled)
                {
                    break;
                }

                ProtocolReason reason;
                _session.EnterDeferReady();
                try
                {
                    reason = await _session.ConnectWithResumeDetailedAsync().ConfigureAwait(false);

                    if (reason == ProtocolReason.NONE)
                    {
                        if (_session.OnResumedAsync is { } onResumed)
                        {
                            await onResumed(CancellationToken.None).ConfigureAwait(false);
                        }
                    }
                    else if (_session.OnReauthenticateAsync is { } reauth)
                    {
                        await reauth(CancellationToken.None).ConfigureAwait(false);
                    }
                }
                finally
                {
                    _session.ExitDeferReady();
                }

                _readyTcs = null;
                _ = tcs.TrySetResult(true);
                _session.NotifyReady();
                return;
            }
            catch (ObjectDisposedException ex)
            {
                // The session was disposed while reconnecting; retrying can never succeed.
                _readyTcs = null;
                _ = tcs.TrySetException(ex);
                return;
            }
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
            {
                // Keep retrying until attempts are exhausted; transient connect/handshake
                // failures are expected during an outage.
            }
        }

        _readyTcs = null;
        _ = tcs.TrySetException(_session.Options.AutoReconnectEnabled
            ? new TimeoutException($"Auto-reconnect gave up after {attempt} attempt(s).")
            : new OperationCanceledException("Auto-reconnect stopped: AutoReconnectEnabled was disabled."));
    }
}
