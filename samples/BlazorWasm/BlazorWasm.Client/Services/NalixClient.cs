// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

// Sample favours readable logging calls over LoggerMessage source generation.
#pragma warning disable CA1873

using BlazorWasm.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nalix.SDK.Options;
using Nalix.SDK.Transport;
using Nalix.SDK.Transport.Extensions;

namespace BlazorWasm.Client.Services;

/// <summary>Connection state surfaced to the UI.</summary>
public enum ConnectionStatus
{
    /// <summary>Not connected and not trying.</summary>
    Disconnected,

    /// <summary>First connect + handshake in progress.</summary>
    Connecting,

    /// <summary>Connected and the handshake (or resume) completed.</summary>
    Connected,

    /// <summary>The connection dropped; a backoff loop is reconnecting.</summary>
    Reconnecting,
}

/// <summary>
/// The one Nalix session of the app, registered as a DI singleton. Components never touch
/// <see cref="WebSocketSession"/> directly: they call the typed methods and subscribe to the
/// C# events, which is what keeps component code free of transport concerns.
/// </summary>
public sealed class NalixClient : IAsyncDisposable
{
    private readonly NalixClientOptions _options;
    private readonly ILogger<NalixClient> _logger;
    private readonly WebSocketSession _session;
    private readonly IDisposable _chatSubscription;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private int _reconnecting;
    private bool _userDisconnected;

    /// <summary>Raised on every connection state change.</summary>
    public event Action<ConnectionStatus>? StatusChanged;

    /// <summary>Raised for every chat line pushed by the server.</summary>
    public event Action<ChatLine>? ChatReceived;

    /// <summary>Gets the current connection state.</summary>
    public ConnectionStatus Status { get; private set; } = ConnectionStatus.Disconnected;

    /// <summary>Gets a value indicating whether the last (re)connect resumed an existing server session.</summary>
    public bool LastConnectResumed { get; private set; }

    /// <summary>Initializes the client; does not connect.</summary>
    public NalixClient(IOptions<NalixClientOptions> options, ILogger<NalixClient> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _logger = logger;

        TransportOptions transport = new()
        {
            Address = _options.Host,
            Port = _options.Port,
            ServerPublicKey = string.IsNullOrWhiteSpace(_options.ServerPublicKey) ? null : _options.ServerPublicKey,
            ConnectTimeoutMillis = 5_000,
            // WebSocketSession has no built-in auto-reconnect supervisor; this class runs its own loop.
            ResumeEnabled = true,
            ResumeFallbackToHandshake = true,
        };

        _session = new WebSocketSession(transport, new WebSocketTransportOptions
        {
            Path = _options.Path,
            UseTls = _options.UseTls,
        });

        // Subscribe BEFORE connecting so no push is missed. The packet is returned to the pool
        // after the handler returns, so copy what you need out of it.
        _chatSubscription = _session.On<ChatMessagePacket>(p => ChatReceived?.Invoke(new ChatLine(p.Username, p.Message)));

        _session.OnDisconnected += this.OnSessionDisconnected;
    }

    /// <summary>
    /// Connects and performs the X25519 handshake. Safe to call from several components:
    /// concurrent callers share one attempt, and an already-connected client returns immediately.
    /// </summary>
    public async Task EnsureConnectedAsync(CancellationToken ct = default)
    {
        if (Status == ConnectionStatus.Connected && _session.IsConnected)
        {
            return;
        }

        await _connectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Status == ConnectionStatus.Connected && _session.IsConnected)
            {
                return;
            }

            _userDisconnected = false;
            this.SetStatus(ConnectionStatus.Connecting);
            await this.ConnectCoreAsync(ct).ConfigureAwait(false);
            this.SetStatus(ConnectionStatus.Connected);
        }
        catch
        {
            this.SetStatus(ConnectionStatus.Disconnected);
            throw;
        }
        finally
        {
            _ = _connectGate.Release();
        }
    }

    /// <summary>Request/response round-trip.</summary>
    public async Task<EchoResult> EchoAsync(string text, CancellationToken ct = default)
    {
        await this.EnsureConnectedAsync(ct).ConfigureAwait(false);

        using EchoRequestPacket request = new() { Text = text };
        using EchoResponsePacket response = await _session.RequestAsync<EchoResponsePacket>(
            request,
            RequestOptions.Default.WithTimeout(5_000),
            ct: ct).ConfigureAwait(false);

        return new EchoResult(response.Text, DateTimeOffset.FromUnixTimeMilliseconds(response.ServerUnixMs));
    }

    /// <summary>Fire-and-forget send; the server broadcasts it back to everyone.</summary>
    public async Task SendChatAsync(string username, string message, CancellationToken ct = default)
    {
        await this.EnsureConnectedAsync(ct).ConfigureAwait(false);
        using ChatMessagePacket packet = new() { Username = username, Message = message };
        await _session.SendAsync(packet, ct).ConfigureAwait(false);
    }

    /// <summary>Closes the connection on purpose; no automatic reconnect follows.</summary>
    public async Task DisconnectAsync()
    {
        _userDisconnected = true;
        await _session.DisconnectAsync().ConfigureAwait(false);
        this.SetStatus(ConnectionStatus.Disconnected);
    }

    /// <summary>Simulates a network drop (closes the socket without marking it intentional).</summary>
    public void SimulateDrop() => this.OnSessionDisconnected(this, new IOException("Simulated drop"));

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _userDisconnected = true;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        _session.OnDisconnected -= this.OnSessionDisconnected;
        _chatSubscription.Dispose();
        await _session.DisconnectAsync().ConfigureAwait(false);
        _session.Dispose();
        _lifetime.Dispose();
        _connectGate.Dispose();
    }

    private async Task ConnectCoreAsync(CancellationToken ct)
    {
        // Connect, then try to resume the previous server session (keeps server-side state and
        // skips the key exchange); falls back to a full handshake when there is nothing to resume.
        LastConnectResumed = await _session.ConnectWithResumeAsync(ct: ct).ConfigureAwait(false);
        _logger.LogInformation("Connected to Nalix ({Mode}).", LastConnectResumed ? "resumed" : "new handshake");
    }

    private void OnSessionDisconnected(object? sender, Exception ex)
    {
        if (_userDisconnected || _lifetime.IsCancellationRequested)
        {
            return;
        }

        // Single-flight: the SDK may raise OnDisconnected more than once for one drop.
        if (Interlocked.Exchange(ref _reconnecting, 1) == 1)
        {
            return;
        }

        _logger.LogWarning("Connection lost: {Reason}. Reconnecting...", ex.Message);
        _ = this.ReconnectLoopAsync();
    }

    private async Task ReconnectLoopAsync()
    {
        this.SetStatus(ConnectionStatus.Reconnecting);
        int attempt = 0;
        try
        {
            while (!_lifetime.IsCancellationRequested && !_userDisconnected)
            {
                // Exponential backoff with jitter, capped at 15 s.
                int delay = (int)Math.Min(15_000, 500 * Math.Pow(2, attempt++));
                delay = (int)(delay * (0.8 + (Random.Shared.NextDouble() * 0.4)));
                await Task.Delay(delay, _lifetime.Token).ConfigureAwait(false);

                await _connectGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                try
                {
                    await this.ConnectCoreAsync(_lifetime.Token).ConfigureAwait(false);
                    this.SetStatus(ConnectionStatus.Connected);
                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogInformation("Reconnect attempt {Attempt} failed: {Message}", attempt, ex.Message);
                }
                finally
                {
                    _ = _connectGate.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // App is shutting down.
        }
        finally
        {
            _ = Interlocked.Exchange(ref _reconnecting, 0);
        }
    }

    private void SetStatus(ConnectionStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(status);
    }
}

/// <summary>A chat line copied out of a pooled packet.</summary>
public sealed record ChatLine(string Username, string Message);

/// <summary>Result of <see cref="NalixClient.EchoAsync"/>.</summary>
public sealed record EchoResult(string Text, DateTimeOffset ServerTime);
