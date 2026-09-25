# WebSocket Options

The `NetworkWebSocketOptions` class represents configuration settings for the hosted server-side WebSocket listeners.

## Source Mapping

- `src/Nalix.Network/Options/NetworkWebSocketOptions.cs`

## Overview

These settings manage binding properties (port, host, path), subprotocol handshakes, channel queues, timeouts, and maximum payload constraints.

## Configuration Table

The options map to INI configuration sections:

| Property | Type | Default Value | Description |
|----------|------|---------------|-------------|
| `Port` | `ushort` | `57207` | The local port number the WebSocket listener binds to. Range: 1 to 65535. |
| `Path` | `string` | `"/ws/"` | The URL path prefix mapped to the WebSocket upgrade handler. |
| `Host` | `string` | `"*"` | The host prefix address to listen on (e.g. `*` for all interfaces, `+`, or `localhost`). |
| `SubProtocol` | `string` | `"nalix.v1"` | The server-side subprotocol verified during WebSocket negotiation. |
| `EnableTimeout` | `bool` | `true` | If `true`, enables connection idle timeout tracking inside the timing wheel. |
| `ProcessChannelDrainTimeout` | `int` | `5000` (ms) | Time in milliseconds to wait for the internal processing channel queue to drain during listener deactivation. |
| `ProcessChannelCapacity` | `int` | `256` | Bounded capacity of the pending connection channel. Reaching this capacity will drop/throttle new handshakes. |
| `MaxMessageSize` | `int` | `1,048_576` (1 MB) | The maximum permitted size of a single inbound WebSocket frame payload in bytes. |
| `AllowedOrigins` | `string` | `""` (check disabled) | Comma-separated allowlist of browser origins (`scheme://host[:port]`) permitted to upgrade. Empty disables the check. |
| `AllowMissingOrigin` | `bool` | `true` | When an allowlist is set, whether an upgrade with **no** `Origin` header (native/non-browser clients) is accepted. Ignored when `AllowedOrigins` is empty. |

## Origin Enforcement (CSWSH protection)

Browsers attach an `Origin` header to every WebSocket upgrade, and any page the user visits can try to open a socket to your server (cross-site WebSocket hijacking). When `AllowedOrigins` is non-empty the listener checks `Origin` **before** the `101 Switching Protocols` response:

- The match is exact on scheme, host and port. Scheme and host compare case-insensitively, and a trailing `/` is ignored. `https://app.example.com` does **not** match `http://app.example.com`, `https://app.example.com:8443` or `https://evil.app.example.com`.
- There is no wildcard. `*` is not special; list every origin explicitly.
- A request without `Origin` is accepted only if `AllowMissingOrigin = true`. Native clients (the Nalix SDK, console/MAUI/Unity) send none. Set it to `false` for browser-only deployments.
- A rejected request gets `HTTP/1.1 403 Forbidden` and the socket is closed. No connection or session is created. The listener writes a `NW.ws:origin` warning diagnostic (`ws-origin-rejected`) with the origin and remote endpoint.

The default (`AllowedOrigins` empty) keeps the legacy accept-everything behaviour for backward compatibility. The listener writes a `ws-origin-check-disabled` warning at startup in that case. Set `AllowedOrigins` in any deployment that browsers can reach.

## Usage Example

### Mutating Options Programmatically

```csharp
using Nalix.Hosting;
using Nalix.Network.Options;

INetworkApplicationBuilder builder = NetworkApplication.CreateBuilder();

builder.Configure<NetworkWebSocketOptions>(options =>
{
    options.Port = 8080;
    options.Path = "/socket";
    options.Host = "127.0.0.1";
    options.EnableTimeout = true;
    options.ProcessChannelCapacity = 512;
    options.MaxMessageSize = 2_097_152; // 2 MB
    options.AllowedOrigins = "https://app.example.com,https://admin.example.com";
    options.AllowMissingOrigin = false; // browser-only deployment
});
```

### INI Configuration Format

```ini
[NetworkWebSocket]
; Network WebSocket configuration — controls endpoint, subprotocol, and behavior
Port = 57207
Path = /ws/
Host = *
SubProtocol = nalix.v1
EnableTimeout = true
ProcessChannelDrainTimeout = 5000
ProcessChannelCapacity = 256
MaxMessageSize = 1048576
AllowedOrigins =
AllowMissingOrigin = true
```

## See Also

* [WebSocket Listener](../../network/websocket-listener.md)
* [WebSocket Connection](../../network/websocket-connection.md)
