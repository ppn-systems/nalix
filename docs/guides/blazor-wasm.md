# Blazor WebAssembly Client

This guide connects a **Blazor WebAssembly** app running in the browser to a Nalix server over WebSocket, with the full X25519 handshake. It covers request/response, server push, reconnecting, and how to register the session in dependency injection (DI).

Full source: `samples/BlazorWasm`

## What you'll build

- A Nalix server with one WebSocket endpoint (`ws://localhost:57230/ws/`) that handles an echo request and rebroadcasts chat lines.
- A standalone Blazor WebAssembly app that:
    - keeps a single `WebSocketSession` in a DI singleton,
    - sends an echo request and awaits the typed response,
    - shows chat lines the server pushes to every tab,
    - reconnects with backoff when the connection drops (it tries to resume first, then does a full handshake).

```text
samples/BlazorWasm/
├── BlazorWasm.Contracts/   # packets, shared by server and browser
├── BlazorWasm.Server/      # Nalix server (console app)
└── BlazorWasm.Client/      # Blazor WebAssembly app
    ├── Services/NalixClient.cs        # the one session + reconnect loop
    ├── Services/NalixClientOptions.cs
    ├── Pages/Home.razor
    └── wwwroot/appsettings.json       # Host / Port / Path / ServerPublicKey
```

## Does the secure handshake work in the browser?

Yes. The Nalix handshake uses its own managed X25519, AEAD, and hashing code. It does not call `System.Security.Cryptography`, which is mostly unsupported in browser WebAssembly. `ConnectAsync` → `HandshakeAsync` gives you the same encrypted session in Chromium, Firefox, and Safari that you get on desktop.

The sample was checked in headless Chromium, in both the Debug build and the trimmed Release publish: handshake, echo, broadcast to a second tab, and reconnect after a drop.

## 1. Shared packet project

Put packets in a plain class library that **both** the server and the WASM app reference. The browser can only deserialize packet types it knows about.

```csharp
// samples/BlazorWasm/BlazorWasm.Contracts/EchoRequestPacket.cs
[Packet]
[GenerateFormatter]
[SerializePackable(SerializeLayout.Explicit)]
public sealed partial class EchoRequestPacket : PacketBase<EchoRequestPacket>, IPacketStaticOpcode
{
    public static ushort StaticOpCode => 0x7301;

    [SerializeOrder(0)]
    public string Text { get; set; } = string.Empty;
}
```

`EchoResponsePacket` (`0x7302`) and `ChatMessagePacket` (`0x7303`) follow the same pattern. The contracts project references only `Nalix.Abstractions` and `Nalix.Codec`, never `Nalix.Hosting` or `Nalix.Network`, so it stays small and browser-safe. It also sets `<IsTrimmable>true</IsTrimmable>`.

## 2. Server

```csharp
// samples/BlazorWasm/BlazorWasm.Server/Program.cs
await using NetworkApplication app = NetworkApplication.CreateBuilder()
    .UseLogger(logger)
    .UseSecureConnections()   // X25519 handshake + AEAD
    .UseSystemControl()       // serves PUBLIC_KEY_REQUEST (TOFU), ping, disconnect
    .UseSessions()            // allows resume after a reconnect
    .MapHandlers(typeof(DemoHandlers))
    .MapWebSocket<DefaultProtocol>().OnPort(57230).WithPath("/ws/").Bind()
    .Build();
```

The handlers are ordinary Nalix handlers. The transport makes no difference to them:

```csharp
[PacketOpcode(0x7301)]
public static async ValueTask HandleEchoAsync(IPacketContext<EchoRequestPacket> context)
{
    using PacketScope<EchoResponsePacket> lease = PacketFactory<EchoResponsePacket>.Acquire();
    lease.Value.Text = context.Packet.Text.ToUpperInvariant();
    lease.Value.ServerUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    await context.Sender.SendAsync(lease.Value).ConfigureAwait(false);   // → RequestAsync<T> on the client
}

[PacketOpcode(0x7303)]
public static async ValueTask HandleChatAsync(IPacketContext<ChatMessagePacket> context)
{
    IConnectionBroadcaster? hub = InstanceManager.Instance.GetExistingInstance<IConnectionBroadcaster>();
    if (hub is not null)
    {
        await hub.BroadcastAsync(context.Packet).ConfigureAwait(false);  // → On<T> in every tab
    }
}
```

At startup the server prints its public key (`HandshakeHandlers.ServerPublicKey`). Pin that key in the client for production (see [Pitfalls](#pitfalls)).

## 3. Client project

```xml
<Project Sdk="Microsoft.NET.Sdk.BlazorWebAssembly">
  <ItemGroup>
    <PackageReference Include="Microsoft.AspNetCore.Components.WebAssembly" Version="10.0.9" />
    <PackageReference Include="Microsoft.AspNetCore.Components.WebAssembly.DevServer" Version="10.0.9" PrivateAssets="all" />
    <PackageReference Include="Nalix.SDK" Version="14.3.1" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\BlazorWasm.Contracts\BlazorWasm.Contracts.csproj" />
  </ItemGroup>
  <ItemGroup>
    <!-- Keep every packet + generated formatter when the Release publish trims. -->
    <TrimmerRootAssembly Include="BlazorWasm.Contracts" />
  </ItemGroup>
</Project>
```

Connection settings go in `wwwroot/appsettings.json`. The browser downloads this file, so it must not contain secrets:

```json
{ "Nalix": { "Host": "localhost", "Port": 57230, "Path": "/ws/", "UseTls": false, "ServerPublicKey": "" } }
```

## 4. Register the session in DI

A browser tab should hold **one** Nalix connection. Wrap it in a service, register that service as a singleton, and have components talk to the service rather than to `WebSocketSession`.

```csharp
// Program.cs
builder.Services.Configure<NalixClientOptions>(builder.Configuration.GetSection("Nalix"));
builder.Services.AddSingleton<NalixClient>();
```

In Blazor WebAssembly, `Singleton` and `Scoped` both live as long as the tab. `Singleton` states that intent directly.

The service constructs the session and subscribes to pushed packets **before** it connects:

```csharp
_session = new WebSocketSession(
    new TransportOptions
    {
        Address = o.Host,
        Port = o.Port,
        ServerPublicKey = string.IsNullOrWhiteSpace(o.ServerPublicKey) ? null : o.ServerPublicKey,
        ConnectTimeoutMillis = 5_000,
        ResumeEnabled = true,
        ResumeFallbackToHandshake = true,
    },
    new WebSocketTransportOptions { Path = o.Path, UseTls = o.UseTls });

// Subscribe before connecting so no push is missed. The packet goes back to the pool
// when the handler returns, so copy its fields out.
_chatSubscription = _session.On<ChatMessagePacket>(p => ChatReceived?.Invoke(new ChatLine(p.Username, p.Message)));
_session.OnDisconnected += OnSessionDisconnected;
```

Connecting is single-flight, so several components can call it at once:

```csharp
public async Task EnsureConnectedAsync(CancellationToken ct = default)
{
    if (Status == ConnectionStatus.Connected && _session.IsConnected) return;
    await _connectGate.WaitAsync(ct);
    try
    {
        // ConnectAsync + resume if possible, otherwise HandshakeAsync.
        LastConnectResumed = await _session.ConnectWithResumeAsync(ct: ct);
        SetStatus(ConnectionStatus.Connected);
    }
    finally { _connectGate.Release(); }
}
```

## 5. Call it from a component

```razor
@page "/"
@implements IDisposable
@inject NalixClient Nalix

<button @onclick="EchoAsync">Echo</button> <span>@_echo</span>
<ul>@foreach (var l in _chat) { <li><b>@l.Username</b>: @l.Message</li> }</ul>

@code {
    protected override async Task OnInitializedAsync()
    {
        Nalix.ChatReceived += OnChat;
        Nalix.StatusChanged += OnStatus;
        await Nalix.EnsureConnectedAsync();
    }

    async Task EchoAsync() => _echo = (await Nalix.EchoAsync("hello")).Text;

    // SDK callbacks do not come from the renderer: go back through InvokeAsync.
    void OnChat(ChatLine l) => _ = InvokeAsync(() => { _chat.Add(l); StateHasChanged(); });
    void OnStatus(ConnectionStatus _) => _ = InvokeAsync(StateHasChanged);

    public void Dispose() { Nalix.ChatReceived -= OnChat; Nalix.StatusChanged -= OnStatus; }
}
```

`EchoAsync` inside the service is a plain `RequestAsync`:

```csharp
using EchoRequestPacket request = new() { Text = text };
using EchoResponsePacket response = await _session.RequestAsync<EchoResponsePacket>(
    request, RequestOptions.Default.WithTimeout(5_000), ct: ct);
```

## 6. Handle server push

Push packets reach `session.On<T>()`. The service turns each packet into a plain record (`ChatLine`) and raises a C# event, and components append the record and re-render through `InvokeAsync`. Two rules apply:

- **Dispose the `On<T>` subscription** when the service is disposed. Otherwise the handler keeps firing.
- **Unsubscribe component handlers in `Dispose`.** Otherwise the singleton holds on to pages the user has already left.

## 7. Reconnect

`TransportOptions.AutoReconnectEnabled` is used by `TcpSession`/`UdpSession`. **`WebSocketSession` does not run a reconnect supervisor**, so the browser client runs its own loop:

```csharp
private void OnSessionDisconnected(object? sender, Exception ex)
{
    if (_userDisconnected) return;                                  // DisconnectAsync() was ours
    if (Interlocked.Exchange(ref _reconnecting, 1) == 1) return;    // one loop at a time
    _ = ReconnectLoopAsync();
}

private async Task ReconnectLoopAsync()
{
    SetStatus(ConnectionStatus.Reconnecting);
    for (int attempt = 0; !_lifetime.IsCancellationRequested; attempt++)
    {
        int delay = (int)Math.Min(15_000, 500 * Math.Pow(2, attempt));       // exp. backoff
        await Task.Delay((int)(delay * (0.8 + Random.Shared.NextDouble() * 0.4)), _lifetime.Token); // + jitter
        try { await ConnectCoreAsync(_lifetime.Token); SetStatus(ConnectionStatus.Connected); return; }
        catch (Exception) { /* keep trying */ }
    }
}
```

`ConnectWithResumeAsync` sends a resume request when the session holds a session token and secret. If the server still has that session, the handshake is skipped. If not, it falls back to `HandshakeAsync()`.

By default the server persists a session only when it has at least `SessionStoreOptions.MinAttributesForPersistence` (10) attributes. A demo connection that stores nothing therefore reconnects with a fresh handshake, and the sample page shows this. Your own state lives on the server, so re-send any subscriptions or logins after a fresh handshake.

The **Simulate drop** button in the sample runs this path. You can also stop and restart the server.

## Run it

```bash
# terminal 1
dotnet run --project samples/BlazorWasm/BlazorWasm.Server
# terminal 2
dotnet run --project samples/BlazorWasm/BlazorWasm.Client --urls http://localhost:5230
```

Open `http://localhost:5230` in two tabs. Echo replies in upper case, and chat lines appear in both tabs.

## Pitfalls

**WebSocket URL.** The SDK builds `{ws|wss}://{Host}:{Port}{Path}`. `Path` must match the server's `WithPath(...)`, including the trailing slash (the default is `/ws/`). `Host` is resolved **by the browser**, so `localhost` means the user's machine. Put the public host name in `appsettings.json`, or in `appsettings.{Environment}.json`, for deployed builds.

**Mixed content.** A page served over `https://` can only open `wss://`. Set `UseTls = true` and terminate TLS in front of Nalix, for example with nginx or Caddy (see [Reverse Proxy TLS](deployment/reverse-proxy-tls.md)). Browsers do not let you accept a self-signed certificate for a WebSocket the way you can for a page. Trust the dev certificate, or run plain `ws://` locally.

**Origin checks.** Browsers send an `Origin` header on the WebSocket upgrade, and any site the user visits can try to open a socket to your server (cross-site WebSocket hijacking). Set `NetworkWebSocketOptions.AllowedOrigins` to the exact origin(s) that serve your Blazor app, for example `https://app.example.com`. The listener then answers `403 Forbidden` before the upgrade for any other origin. Set `AllowMissingOrigin = false` if only browsers connect. Leave it `true` if native SDK clients share the endpoint, since they send no `Origin`. The default (empty allowlist) accepts every origin and logs a startup warning. See [WebSocket Options](../api/options/network/websocket-options.md#origin-enforcement-cswsh-protection). The origin check is not authentication. Still require application-level auth after the handshake. The handshake encrypts the connection. It does not tell you who the user is.

**Pin the server key.** When `ServerPublicKey` is empty, the client trusts the key the server sends on first use (TOFU). This needs `UseSystemControl()` on the server; otherwise the handshake times out waiting for `SessionTofu`. It also cannot detect a man-in-the-middle on that first connection. For production, copy the key the server prints at startup into `appsettings.json`. It is a public key, so it can ship to the browser.

**Per-IP limits during development.** Every tab, reload, and reconnect comes from `127.0.0.1`. `ConnectionQuotaOptions.ExemptLoopback` (on by default) exempts loopback clients from per-IP/subnet quotas, rate windows, and automatic bans, so quick reloads during development are not throttled and no workaround is needed. Remote clients still get the default limits (10 attempts per 5 s, halved in burst mode); once one is banned the browser only reports `ERR_CONNECTION_RESET`. In production, behind a proxy, configure `TrustedProxyOptions` so that limits apply to real client IPs rather than to the proxy. If a same-host proxy does not forward the client address, set `ExemptLoopback = false`, or every client is exempt. See [Connection Quota Options](../api/options/network/connection-quota-options.md).

**Trimming and AOT.** A Release publish of Blazor WASM trims assemblies. Packets are found at runtime through `PacketRegistry`, so root the contracts assembly with `<TrimmerRootAssembly Include="YourApp.Contracts" />`. The sample publishes trimmed without warnings and runs. WebAssembly AOT (`RunAOTCompilation`) is optional and only affects speed. The SDK's receive loop is async and never blocks a thread, which matters because WASM has only one thread.

**Browser-only API limits.** Use the async APIs only. `WebSocketSession.Send(ReadOnlySpan<byte>, ...)`, `TcpSession`, and `UdpSession` are marked `[UnsupportedOSPlatform("browser")]`. There are no raw sockets, UDP, or TCP in the browser. Background tabs can be throttled, and mobile browsers may freeze them entirely, so expect `OnDisconnected` after a tab wakes up. Handling it is the job of the reconnect loop.

**Threading and rendering.** `On<T>` handlers and `OnDisconnected` are not raised on the renderer's synchronization context. Always call `InvokeAsync(StateHasChanged)` from them. Copy data out of pooled packets before the handler returns.

## See also

- [Client Session Guide](networking/connecting-clients.md)
- [Build a Chat Room](build-a-chat-room.md)
- [Securing Your Server](securing-your-server.md)
