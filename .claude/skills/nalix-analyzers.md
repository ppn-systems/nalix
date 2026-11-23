# Nalix.Analyzers

## Triggers
- Adding a new diagnostic rule to enforce a Nalix coding pattern
- Investigating why the IDE shows a `NALIXxxx` warning or error
- Changing the Roslyn analyzer infrastructure

---

## Rules

### Diagnostic IDs
IDs are sequential: `NALIX001`–`NALIX078`, **not organized by hundreds**. Categories are mixed throughout the range. Full authoritative list is in `DiagnosticDescriptors.cs`. Broad groupings:
- `NALIX001–NALIX008`: Packet/controller/dispatch + middleware/handler patterns
- `NALIX009–NALIX022`: Packet operations (deserializer, base types, header, opcode)
- `NALIX023–NALIX028`: Configuration and SDK/request options
- `NALIX029–NALIX031`: Encrypted requests, middleware ordering
- `NALIX032–NALIX039`: Middleware behavior, buffer leaks, performance
- `NALIX040–NALIX058`: Network, hosting, opcode ranges, allocations, return types
- `NALIX071–NALIX078`: Crypto, formatting, logging, pooling, and AOT constraints
- `NALIX039`, `NALIX108–NALIX110`, `NALIX119`: `IBufferLease` ownership. `NalixUsageAnalyzer.LeaseFlow.cs` + `.LeaseFlow.Transfer.cs` run a data-flow analysis over the control-flow graph for owned leases (NALIX039/108/109 and aliases of borrowed parameters); `NalixUsageAnalyzer.LeaseOwnership.cs` holds the signature-based `[Borrowed]` checks (NALIX110/119)
- `NALIX111–NALIX114`: Handler policy attributes — missing `[PacketPermission]`, non-positive `[PacketRateLimit]`/`[PacketTimeout]`, policy attribute with no middleware (`NalixUsageAnalyzer.HandlerPolicy.cs`)
- `NALIX115–NALIX116`: SDK usage — retry on a packet without `[PacketIdempotent]`, discarded `On`/`OnExact`/`SubscribeTemp` subscription (`NalixUsageAnalyzer.SdkUsage.cs`)
- `NALIX117`: byte-wise `SequenceEqual` on secret-named data (`NalixUsageAnalyzer.SecretComparison.cs`)
- `NALIX118`: validation attributes that do not fit their `ConfigurationLoader` property (`NalixUsageAnalyzer.ConfigurationValidation.cs`)
- `NALIX120`: `ResetForPool()` completeness for every `IPoolable` (NALIX107 is the packet version; both share `AnalyzeResetForPoolCompleteness`, and a member touched in `OnRent()` counts as reset)
- `NALIX121`: pooled object used after `Return` — the lease flow analysis in pooled mode (`LeaseFlow` with a non-null poolable type: `Return` is the only release, nothing escapes, no leak reports)
- `NALIX025`, `NALIX026`, `NALIX043` were removed (the metadata-provider types no longer exist); do not reuse the IDs

When adding a new rule, append the next available ID — do not try to "slot" into a category range.

### Analyzer Performance Constraints
- **Zero allocations in `AnalyzeXxx` methods** — analyzers run on every keystroke in the IDE; allocation degrades typing responsiveness for all developers
- Cache symbol lookups in `SymbolSet` — never re-resolve `INamedTypeSymbol` on every analysis call
- Check `CancellationToken` before `SyntaxTree.GetRoot()` — IDE cancels analysis on every edit
- Match types by fully-qualified name string (from `KnownNames`) — never reference Nalix runtime assemblies

### Partial Class Structure
`NalixUsageAnalyzer` is split into:
- `NalixUsageAnalyzer.cs` — registration, main `Initialize()` entry
- `NalixUsageAnalyzer.InvocationAnalysis.cs` — invocation-specific analysis
- `NalixUsageAnalyzer.SymbolSet.cs` — cached Roslyn symbol lookups
- `NalixUsageAnalyzer.HandlerPolicy.cs` / `.SdkUsage.cs` / `.SecretComparison.cs` / `.ConfigurationValidation.cs` — NALIX111–118; they resolve their own optional symbols in `PolicySymbols` (null = rule skipped) and share one `Invocation` action, `AnalyzePolicyInvocation`

---

## Checklists

### Add a new diagnostic rule
1. Add descriptor in `DiagnosticDescriptors.cs` — append next available `NALIXxxx` ID
2. Register the analysis action in `NalixUsageAnalyzer.Initialize()` (e.g., `context.RegisterSyntaxNodeAction(...)`)
3. Implement analysis logic in `InvocationAnalysis.cs` or a new partial file
4. Add corresponding code fix in `Nalix.Analyzers.CodeFixes` — see nalix-analyzers-codefixes skill
5. Build `Nalix.Analyzers` — all consuming projects pick it up automatically via `Directory.Build.props`

### Suppress a diagnostic (when justified)
```csharp
#pragma warning disable NALIX014 // reason why suppression is justified
... code ...
#pragma warning restore NALIX014
```
Do not suppress at the project level — suppressions should be local and documented.

---

## Gotchas

- **`[Borrowed]` is an ownership contract, not a NALIX039 suppressor**: NALIX108–110 treat every lease-typed parameter *without* `[Borrowed]` as taking ownership. Marking a parameter `[Borrowed]` while the method disposes or stores the lease raises NALIX110. Use `[Borrowed]` only when the caller keeps ownership; call `Retain()` when the callee needs its own reference. Delegate invocations, generic `T` parameters and calls to methods that return `bool` are treated as non-owning (a `bool` method is a conditional transfer: it keeps the lease only on success, whatever its name). A parameter inherits `[Borrowed]` from the interface member or base method it implements, so put the attribute on the base declaration (NALIX119 flags an implementation that adds it alone). One `Retain()` grants one release, and only for releases after it. A borrowed lease may not be returned, have its `Memory` stored, or be captured by a lambda queued to `Task`/`ThreadPool` without `Retain()`.

- **Lease flow analysis is "possibly released", like a Rust move**: after `if (x) lease.Dispose();` the lease is possibly released, so any later use or release is NALIX109/NALIX108. A `catch` block starts from every state reached before a statement of its `try` block that can throw, so `lease.Dispose(); Log(); ` inside `try` followed by `lease.Dispose()` in `catch` is reported: release last, right before `return`. NALIX039 is only raised when a lease is certainly still owned at a `return`. NALIX108/109/110/119 are errors in this repository (`.editorconfig`). The analysis gives up on a lease (no reports) once it is stored, retained, passed by `ref`, captured by a lambda, or put behind names that do not always refer to the same lease.

- **The analyzer is not attached to every Nalix project**: `src/Directory.Build.props` skips `Nalix.Abstractions`, `Nalix.Environment`, `Nalix.Framework` and `Nalix.Network` (every rule needs `Nalix.Codec` symbols), so a clean `src` build says nothing about code in those four projects. Info-level diagnostics are not printed by `dotnet build`; use `-p:ErrorLog=out.sarif` to see them.

- **Analyzer tests see the real Nalix assemblies**: the test harness references the whole trusted-platform assembly list, which includes the real `Nalix.Abstractions`. A type defined as a stub in the test source wins over it, but a type the stubs do not define still resolves to the real one. A new rule that keys on an attribute therefore also fires in older tests whose fixtures lack it (NALIX111 did for handlers without `[PacketPermission]`); put new-rule stubs in a separate source (`PolicyTestSources`) or fix the fixtures.

- **Allocating in analyzer = IDE lag**: A `new List<>()` inside `AnalyzeInvocation` runs thousands of times per second. Use `ImmutableArray`, stackalloc, or pre-allocated pools instead.

- **`SymbolSet` must be initialized once per compilation**: Re-resolving symbols on every analysis call causes repeated semantic model queries. `SymbolSet` caches them at `CompilationStartAction` time.

- **Analyzer applies to all projects automatically**: `Directory.Build.props` attaches the analyzer to every Nalix project. A buggy analyzer that throws `NullReferenceException` will fail builds across the entire solution — test on a single project first.

- **Diagnostic severity affects build**: `DiagnosticSeverity.Error` causes `dotnet build` to fail. `Warning` does not. Be intentional about severity — use `Error` only for patterns that always produce broken code.
