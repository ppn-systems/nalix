# BlazorWasm — Nalix in the Browser

A Blazor WebAssembly app talking to a Nalix server over **WebSocket** with the full
**X25519 handshake** (Nalix's crypto is fully managed, so it runs in browser WASM).

Step-by-step walkthrough: [`docs/guides/blazor-wasm.md`](../../docs/guides/blazor-wasm.md).

## What This Sample Demonstrates

- A shared packet project referenced by both the server and the browser app
- `WebSocketSession` + `HandshakeAsync` (via `ConnectWithResumeAsync`) from Blazor WASM
- Request/response with `RequestAsync<T>` (echo)
- Server push with `IConnectionBroadcaster` on the server and `session.On<T>()` in the browser (chat)
- A reconnect loop with exponential backoff that tries resume first, then a fresh handshake
- Registering the session as a DI singleton and consuming it from a component (`InvokeAsync(StateHasChanged)`)
- Trimmed Release publish (`TrimmerRootAssembly` for the contracts)

## Folder Structure

```text
BlazorWasm/
├── BlazorWasm.Contracts/     # EchoRequest/EchoResponse/ChatMessage packets
├── BlazorWasm.Server/        # Program.cs, DemoHandlers.cs  (ws://localhost:57230/ws/)
├── BlazorWasm.Client/        # Blazor WASM app
│   ├── Services/NalixClient.cs
│   ├── Services/NalixClientOptions.cs
│   ├── Pages/Home.razor
│   └── wwwroot/appsettings.json
└── BlazorWasm.sln
```

## How to Build

```bash
dotnet build samples/BlazorWasm/BlazorWasm.sln
```

## How to Run

```bash
# Terminal 1 — server (prints its public key)
dotnet run --project samples/BlazorWasm/BlazorWasm.Server

# Terminal 2 — Blazor dev server
dotnet run --project samples/BlazorWasm/BlazorWasm.Client --urls http://localhost:5230
```

Open <http://localhost:5230> in two browser tabs:

- **Echo** — returns your text upper-cased with the server time.
- **Chat** — a line sent from one tab appears in every tab.
- **Simulate drop** — triggers the reconnect loop; the status goes `Reconnecting` → `Connected`.
  Stopping and restarting the server does the same for real.

## Notes

- For production, copy the `Server public key` printed by the server into
  `wwwroot/appsettings.json` → `Nalix:ServerPublicKey` (pinning instead of trust-on-first-use),
  and serve over `https://` + `wss://` (`UseTls: true`) behind a TLS-terminating proxy.
- Every tab and reload comes from `127.0.0.1`. The stock `ConnectionQuotaOptions.ExemptLoopback`
  (on by default) keeps loopback out of the per-IP quotas and auto-bans, so no relaxation is needed.
- See the guide's *Pitfalls* section for origin checks, trimming, and browser limits.
