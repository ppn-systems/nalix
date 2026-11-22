# Nalix.Environment

## Triggers
- Working with binary I/O (`DataReader`, `DataWriter`, `BufferLease`)
- Touching the fragment assembler for large packet reassembly
- Using the configuration system (`ConfigurationManager`)
- Using `SequenceCounter` for anti-replay tracking

---

## Rules

### `BufferLease` Lifecycle
- `BufferLease` is ref-counted: `Retain()` increments, `Dispose()` decrements — reaches zero = returned to pool
- `lease.Borrow()` gives a `BorrowedLease`: a read-only `ref struct` view with no `Dispose` that cannot be stored, captured or kept across `await`; `view.Retain()` is the only way to keep the buffer. Prefer it for parameters that only read the payload
- `Generation` changes on every final release (never reset by pooling); `BorrowedLease.IsAlive` compares it, and debug builds assert it on every access
- **Call `Retain()` before any async handoff** — if the original owner `Dispose()`s while you still hold it, the buffer is reclaimed mid-use
- `ReleaseOwnership()` detaches the raw buffer permanently from the lease shell, preventing pool return on disposal; the caller is then responsible for returning/freeing the buffer
- Thread-local cache absorbs small bursts before falling back to the shared pool

### `DataReader` / `DataWriter`
- `DataReader` is a `ref struct` — stack-allocated, cannot be boxed or stored on the heap
- Reads are sequential; position advances automatically — there is no seek-back
- After deserialization, verify the reader is exhausted to catch packet size mismatches early
- `DataWriter` auto-grows its backing buffer — backing memory is pooled, not heap-allocated

### `SequenceCounter`
- One instance per connection — **never share across connections**
- Used for UDP anti-replay: validates inbound sequence IDs against a sliding window
- Sequence IDs older than the window are rejected silently — not an error, not logged

### Fragment Assembler
- `FragmentAssembler` is stateful — **one instance per connection**, not shared
- `FragmentHeader` is 12 bytes: stream ID + fragment index + total fragment count
- Partial streams are held in memory until complete or timed out — memory pressure risk if many streams open simultaneously

### Configuration
- `ConfigurationManager.Bind<T>()` requires a source-generated binder (from `ConfigurationGenerator`)
- The binder is generated only when `T` is used in a project that references `Nalix.Analyzers.Generators` as `OutputItemType="Analyzer"`
- `XxHash32` is for checksums only — **not cryptographically secure**, never use for authentication or integrity verification in security contexts

---

## Checklists

### Read a binary packet
```csharp
var reader = new DataReader(buffer.Span);
var field1 = reader.ReadInt32();
var field2 = reader.ReadString();
// Verify no leftover bytes after deserialization
reader.AssertExhausted();
```

### Pass a buffer across async boundary
```csharp
// In producer (before await or enqueue):
lease.Retain();

// In consumer (after async operation):
try { ... use lease.Span ... }
finally { lease.Dispose(); }
```

### Configure a new option class
1. Create a POCO class with properties matching INI keys
2. Ensure it is used in a call to `ConfigurationManager.Bind<T>()` in a project that references the generator
3. Build — generator emits an AOT-safe binder for this type

---

## Gotchas

- **Forgetting `Retain()` on async handoff**: The most common `BufferLease` bug. If you enqueue a lease into a channel without `Retain()`, the sender's `Dispose()` call returns it to the pool before the consumer reads it — silent data corruption, not a crash.

- **`DataReader` cannot be stored**: It is a `ref struct`. You cannot store it in a class field, pass it to an async method, or put it in a `List<>`. If you need to pass reader state across method boundaries, read all data first and pass the typed values.

- **`FragmentAssembler` memory leak on partial streams**: If a sender starts a fragmented stream but never completes it (e.g., disconnect mid-send), the assembler holds all received fragments in memory until the stream times out. High concurrent incomplete streams = memory pressure.

- **`XxHash32` in security contexts is a vulnerability**: It is a fast non-cryptographic hash. Using it for HMAC, authentication, or integrity validation in a security context is a vulnerability. Use `SHA-256` or `HKDF` from `Nalix.Codec.Security` instead.

- **Config binding fails silently without generator**: If `ConfigurationGenerator` did not generate a binder for your type (missing project reference setup), `Bind<T>()` may fall back to reflection or throw at runtime — not a compile-time error.
