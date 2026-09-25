// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace BlazorWasm.Client.Services;

/// <summary>
/// Connection settings bound from the <c>Nalix</c> section of <c>wwwroot/appsettings.json</c>.
/// </summary>
public sealed class NalixClientOptions
{
    /// <summary>Server host name (the browser resolves it; <c>localhost</c> for development).</summary>
    public string Host { get; set; } = "localhost";

    /// <summary>WebSocket port of the Nalix server.</summary>
    public ushort Port { get; set; } = 57230;

    /// <summary>WebSocket path; must match the server's <c>WithPath(...)</c>.</summary>
    public string Path { get; set; } = "/ws/";

    /// <summary>Use <c>wss://</c>. Required when the page itself is served over HTTPS.</summary>
    public bool UseTls { get; set; }

    /// <summary>
    /// Hex X25519 public key printed by the server at startup. Pin it in production; when empty
    /// the client trusts the key the server presents on first use (TOFU).
    /// </summary>
    public string? ServerPublicKey { get; set; }
}
