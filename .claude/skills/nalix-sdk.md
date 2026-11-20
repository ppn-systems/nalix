# Nalix.SDK

## Triggers
- Implementing a client application connecting to a Nalix server
- Setting up packet subscriptions or request-response patterns
- Handling session resume, time sync, or cipher updates
- Integrating Nalix with Unity or other main-thread-bound runtimes

---

## Rules

### Extension-Based API
Most SDK functionality lives in **extension methods** on `TransportSession` — not on `TcpSession`/`DatagramSession` directly. Import `Nalix.SDK.Transport.Extensions` and `Nalix.SDK.Extensions`.

### Session Lifecycle Order
```
ConnectAsync() → HandshakeAsync() → subscribe On<T>() → use RequestAsync / SendAsync
```
`HandshakeAsync()` is mandatory after connect — skipping it means no encryption, and the server will reject packets requiring authentication.

### Subscription Management
- `On<T>()` returns `IDisposable` — **must be disposed** to unsubscribe; leaking it = handler fires forever
- Use `SubscribeTemp<T>()` when the subscription should auto-clean up on disconnect
- Use `CompositeSubscription` (via `.Subscribe(sub1, sub2, ...)`) to manage multiple subscriptions as one unit

### Graceful Disconnect
Use `DisconnectGracefullyAsync()` instead of `DisconnectAsync()` — sends a `Control` frame with `ProtocolReason` before closing, allowing the server to clean up the session properly.

### Session Resume
`ConnectWithResumeAsync()` attempts connect + zero-RTT resume in one call. Returns `true` if resume succeeded. If `false`, perform a full `HandshakeAsync()`.

---

## Key Extension Methods

### Subscriptions (`TcpSessionSubscriptions.cs`)
| Method | Use for |
| :--- | :--- |
| `On<T>(Action<T>) → IDisposable` | Subscribe to all packets of type T |
| `OnExact<T>(Action<T>) → IDisposable` | Exact type match only — no subtype matching |
| `OnOnce<T>(Func<T,bool>, Action<T>) → IDisposable` | Fire once when predicate matches, then auto-unsubscribe |
| `SubscribeTemp<T>(Action<T>, Action<Exception>?) → IDisposable` | Auto-disposes when session disconnects |
| `Subscribe(params IDisposable[]) → CompositeSubscription` | Manage multiple subscriptions as one unit |

### Request-Response (`RequestExtensions.cs`)
```csharp
TResponse result = await session.RequestAsync<TResponse>(
    request: new LoginRequest { ... },
    options: new RequestOptions { TimeoutMs = 5000 },
    predicate: r => r.Status == Status.Ok  // optional filter
);
```

### Connection (`HandshakeExtensions.cs`, `ResumeExtensions.cs`, `DisconnectExtensions.cs`)
| Method | Use for |
| :--- | :--- |
| `HandshakeAsync(ct)` | X25519 key exchange + cipher setup after connect |
| `ConnectWithResumeAsync(host?, port?, ct) → bool` | Connect + attempt zero-RTT session resume |
| `ResumeSessionAsync(ct) → ProtocolReason` | Resume only (already connected) |
| `DisconnectGracefullyAsync(reason, closeLocal, ct)` | Graceful disconnect with server notification |

### Utilities
| Method | Use for |
| :--- | :--- |
| `PingAsync(timeoutMs, ct) → double` | RTT in milliseconds |
| `SyncTimeAsync(timeoutMs, ct) → (RttMs, AdjustedMs)` | NTP-style clock sync |
| `UpdateCipherAsync(CipherSuiteType, timeoutMs, ct)` | Rotate cipher mid-session |

---

## Checklists

### Connect and subscribe
```csharp
var session = new TcpSession(options);

// Subscribe before connecting — no messages missed
IDisposable sub1 = session.On<ChatMessage>(msg => HandleChat(msg));
IDisposable sub2 = session.On<ServerNotice>(n => ShowNotice(n));
var all = session.Subscribe(sub1, sub2); // manage as one unit

await session.ConnectAsync();
await session.HandshakeAsync(); // mandatory
```

### Connect with session resume
```csharp
bool resumed = await session.ConnectWithResumeAsync(host, port);
if (!resumed)
    await session.HandshakeAsync(); // full handshake if resume failed
```

### One-shot request
```csharp
var response = await session.RequestAsync<LoginResponse>(
    new LoginRequest { Username = "...", PasswordHash = hash },
    options: new RequestOptions { TimeoutMs = 5000 }
);
```

### Graceful shutdown
```csharp
await session.DisconnectGracefullyAsync(ProtocolReason.CLIENT_LEAVING);
session.Dispose();
```

---

## Gotchas

- **`On<T>()` leaks if not disposed**: The subscription is registered on `OnMessageReceived`. If you don't call `Dispose()` on the returned `IDisposable`, the handler fires indefinitely — including after you think you've "removed" it.

- **`HandshakeAsync()` is not automatic**: `ConnectAsync()` only establishes the TCP connection. Without `HandshakeAsync()`, there is no session key and all encrypted packets will be rejected by the server.

- **`RequestAsync` timeout does not cancel the server operation**: The client stops waiting, but the server processes the request and sends a response that is silently dropped. Design server operations to be idempotent for safe retries.

- **`OnExact<T>` vs `On<T>`**: `On<T>` matches T and any subtype. `OnExact<T>` matches only the exact type. Use `OnExact` when multiple packet types share a base class and you need precise routing.

- **`SubscribeTemp<T>` vs `On<T>`**: `SubscribeTemp` automatically disposes when the session disconnects — useful for fire-and-forget UI patterns. `On<T>` must be manually disposed; use it when you control the subscription lifetime explicitly.

- **`DisconnectAsync()` vs `DisconnectGracefullyAsync()`**: `DisconnectAsync()` closes the socket immediately. `DisconnectGracefullyAsync()` sends a control frame first, giving the server a chance to save state. Always prefer the graceful variant unless the connection is already broken.
