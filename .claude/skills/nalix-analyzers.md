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
- `NALIX108–NALIX110`: `IBufferLease` ownership (`NalixUsageAnalyzer.LeaseOwnership.cs`)

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

- **`[Borrowed]` is an ownership contract, not a NALIX039 suppressor**: NALIX108–110 treat every lease-typed parameter *without* `[Borrowed]` as taking ownership. Marking a parameter `[Borrowed]` while the method disposes or stores the lease raises NALIX110. Use `[Borrowed]` only when the caller keeps ownership; call `Retain()` when the callee needs its own reference. Delegate invocations, generic `T` parameters and `bool TryXxx(lease)` calls are treated as non-owning.

- **Allocating in analyzer = IDE lag**: A `new List<>()` inside `AnalyzeInvocation` runs thousands of times per second. Use `ImmutableArray`, stackalloc, or pre-allocated pools instead.

- **`SymbolSet` must be initialized once per compilation**: Re-resolving symbols on every analysis call causes repeated semantic model queries. `SymbolSet` caches them at `CompilationStartAction` time.

- **Analyzer applies to all projects automatically**: `Directory.Build.props` attaches the analyzer to every Nalix project. A buggy analyzer that throws `NullReferenceException` will fail builds across the entire solution — test on a single project first.

- **Diagnostic severity affects build**: `DiagnosticSeverity.Error` causes `dotnet build` to fail. `Warning` does not. Be intentional about severity — use `Error` only for patterns that always produce broken code.
