# Nalix.Analyzers

> Roslyn-based static code analyzer enforcing high-performance coding standards, serialization correctness, and resource safety in Nalix projects.

**Nalix.Analyzers** is the developer guardrail of the Nalix networking framework. It provides real-time feedback in your IDE and during CI/CD to identify API pitfalls, enforce allocation-free code paths, and guarantee binary serialization correctness.

## Key Features

| Feature | Description | Key Target |
| :--- | :--- | :--- |
| ⚡ **Performance Auditing** | Identifies heap allocations (`new` keywords) and boxing in performance-critical message routing pathways. | Hot Path Methods |
| 📦 **Serialization Safety** | Enforces `SerializeOrder` uniqueness, explicit layout boundaries, and header region integrity. | [SerializePackable] Types |
| 🛡️ **Opcode Integrity** | Prevents duplicate routing opcodes globally or locally and flags system-reserved opcode range usage. | [PacketHandler] Handlers |
| 💧 **Resource Leak Prevention** | Tracks the lifecycle of pooled `IBufferLease` leases to prevent memory leakage or double disposals. | IBufferLease variables |

---

## Core Diagnostic Rules

The analyzer defines a rich catalog of diagnostic checks across four main categories. Below are the most critical diagnostic rules enforced:

| ID | Title | Severity | Target Area | Diagnostic Description / Trigger |
| :--- | :--- | :---: | :---: | :--- |
| **`NALIX001`** | Duplicate Controller Opcode | **Warning** | Routing | Multiple handler methods inside the same controller share a duplicate `PacketOpcode`. |
| **`NALIX002`** | Missing Handler Opcode | **Warning** | Routing | A method matches handler signature patterns but is missing a `[PacketOpcode]` annotation. |
| **`NALIX003`** | Invalid Handler Signature | **Warning** | Routing | A controller method has a signature that is not compatible with the source-generated handler invoker (`PacketHandlerGenerator`). |
| **`NALIX008`** | Missing Controller Attribute | **Warning** | Routing | A type registered as a dispatch handler is missing the `[PacketHandler]` attribute. |
| **`NALIX009`** | Missing Static Deserialize | **Warning** | Serialization | A packet type is registered in the registry but is missing a `public static T Deserialize(ReadOnlySpan<byte>)` method. |
| **`NALIX010`** | Generic Self-Type Mismatch | **Warning** | Type Safety | A packet class inherits from `PacketBase<TSelf>` but fails to use itself as the `TSelf` argument. |
| **`NALIX013`** | Missing SerializeOrder | **Warning** | Serialization | A type declares `SerializeLayout.Explicit` but a serializable property has no `[SerializeOrder]` or `[SerializeIgnore]`. |
| **`NALIX014`** | Duplicate SerializeOrder | **Warning** | Serialization | Two properties or fields inside the same class declare the exact same `SerializeOrder` index. |
| **`NALIX020`** | ResetForPool Missing Base Call | **Warning** | Lifecycle | A packet class overrides `ResetForPool()` but does not call `base.ResetForPool()`, potentially leaking headers. |
| **`NALIX022`** | Reserved Header Slot | **Warning** | Serialization | A user-defined member on a `PacketBase`-derived type uses `[SerializeHeader(0)]`, but header slot 0 is reserved by Nalix packet internals. |
| **`NALIX035`** | Reserved Opcode Range | **Warning** | Routing | A handler is mapped to an opcode in the range `0x0000 - 0x00FF`, which is strictly reserved for system packets. |
| **`NALIX037`** | Potential Allocation in Hot Path | **Info** | Performance | A high-frequency routing hot path contains a class allocation (`new` keyword). Should use `ObjectPoolManager`. |
| **`NALIX039`** | Potential IBufferLease Leak | **Warning** | Lifecycle | A local variable of type `IBufferLease` might not be returned/disposed on all execution pathways. |
| **`NALIX071`** | Disallowed crypto usage | **Warning** | Security | Direct `System.Security.Cryptography` usage in Nalix Core assemblies. Use Nalix internal crypto abstractions. Platform fallback shims (e.g. `OsCsprng`) and `FixedTimeEquals` are allowlisted. Scoped to Nalix Core assemblies only. |
| **`NALIX072`** | Allocating endpoint formatting | **Info** | Performance | `IPAddress.ToString()`, `INetworkEndpoint.Address`, or `SocketEndpoint.Address` in Nalix Core networking hot paths. Use `TryFormatAddress` or Span-based formatting. Scoped to Nalix Core assemblies only. |
| **`NALIX073`** | Unguarded `catch(Exception)` | **Warning** | Correctness | A catch clause catches `System.Exception` without an `ExceptionClassifier.IsNonFatal()` filter. Scoped to Nalix Core assemblies only (not consumer/test/sample projects). |
| **`NALIX074`** | Eager string formatting in diagnostic logging | **Info** | Performance | Interpolated strings, `string.Format`, or concatenation inside `new DiagnosticLog(...)` in Nalix Core assemblies. Only reports when NOT guarded by `IsEnabled(...)`. Scoped to Nalix Core assemblies only. |
| **`NALIX075`** | `PacketScope<T>` Not Disposed | **Error** | Pooling | A local variable of type `PacketScope<T>` is not declared with `using`, causing the pooled packet to leak. |
| **`NALIX076`** | Packet context escapes handler scope | **Warning** | Correctness | `IPacketContext<T>` or its `Packet` is captured by a field, long-lived delegate, or offloaded `Task.Run`. Extract needed data into locals before offloading. Scoped to Nalix Core assemblies only. See [examples](https://ppn.io.vn/en-us/api/analyzers/diagnostic-codes#nalix076-examples-packetcontextescapeanalyzer). |
| **`NALIX078`** | Unbounded reflection in AOT code | **Warning** | AOT | `Assembly.GetTypes()`, `Expression.Compile()`, `MakeGenericType()`, `MethodInfo.Invoke()`, `Type.GetType(string)`, and other unbounded reflection in Nalix Core assemblies. Use source-generated or compile-time alternatives. `Activator.CreateInstance` detection is intentionally deferred. Scoped to Nalix Core assemblies only. |

---

## Deprecated / Removed Diagnostics

The following diagnostics have been removed because the underlying `INetworkBufferMiddleware` type was intentionally dropped from Nalix:

| ID | Title | Status |
| :--- | :--- | :--- |
| `NALIX007` | Buffer middleware ignores stage attribute | Removed |
| `NALIX019` | Buffer middleware type invalid | Removed |
| `NALIX031` | Buffer middleware missing order | Removed |

---

## Key Namespaces

| Namespace | Purpose | Key Types |
| :--- | :--- | :--- |
| `Nalix.Analyzers` | Root namespace containing Roslyn diagnostic analyzers analyzing syntax nodes and semantic models | `NalixUsageAnalyzer` |
| `Nalix.Analyzers.Diagnostics` | Definitions and rule metadata for all compiler and IDE diagnostic codes | `DiagnosticDescriptors` |

## Installation

This package is installed as a development dependency. It executes within the compiler and IDE process and does not add runtime assembly weight:

```bash
dotnet add package Nalix.Analyzers
```

The analyzer will automatically provide real-time suggestions in Visual Studio, Rider, and VS Code. Most diagnostics include automated **Quick Fixes** provided by `Nalix.Analyzers.CodeFixes`.

## IDE Diagnostics and Warnings

- **Real-Time Analysis:** Suggestions and error squiggles appear live as you type.
- **CI/CD Integration:** Warnings and errors are captured during compilation (`dotnet build`), ensuring that high-performance standards are enforced during continuous integration before code is merged.
