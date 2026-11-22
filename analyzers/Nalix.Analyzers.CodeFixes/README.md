# Nalix.Analyzers.CodeFixes

> Roslyn-backed automated IDE quick-fixes for Nalix high-performance coding standards.

**Nalix.Analyzers.CodeFixes** works in tandem with `Nalix.Analyzers` to provide seamless, one-click IDE quick-fixes for Nalix's serialization, resource pooling, and networking patterns. It automates compliance with high-performance C# coding rules directly within Visual Studio, Rider, and VS Code.

## Key Code Fix Providers

| Code Fix Provider | Diagnostic ID | Automated Quick-Fix Action |
| :--- | :--- | :--- |
| **`PacketOpcodeCodeFixProvider`** | `NALIX002` | Resolves invalid or duplicate packet opcodes by suggesting distinct identifiers. |
| **`HandlerPermissionCodeFixProvider`** | `NALIX111` | Adds `[PacketPermission(PermissionLevel.USER)]` to a handler that has none; adjust the level if the handler needs a different one. |
| **`PacketHandlerCodeFixProvider`** | `NALIX008` | Injects or corrects missing controller attribute declarations on message handling classes. |
| **`PacketRegistryDeserializerCodeFixProvider`** | `NALIX009` | Repairs incorrect deserializer factory registrations inside custom packet registries. |
| **`PacketSelfTypeCodeFixProvider`** | `NALIX010`, `NALIX011` | Automatically corrects self-referential generic type mismatches on `PacketBase<T>` inheritance. |
| **`PacketDeserializeCodeFixProvider`** | `NALIX012`, `NALIX052` | Generates boilerplate deserialization override methods for packet types. |
| **`SerializeOrderMissingCodeFixProvider`** | `NALIX013` | Appends `[SerializeOrder(next)]` or `[SerializeIgnore]` to layout properties or fields. |
| **`DuplicateSerializeOrderCodeFixProvider`** | `NALIX014` | Re-calculates and re-orders conflicting or duplicate serialize layout indices. |
| **`SerializationConflictCodeFixProvider`** | `NALIX015` | Resolves attribute conflicts (e.g. having both ignore and ordering on a single member). |
| ****`ResetForPoolCodeFixProvider`**** | `NALIX020` | Generates standard field/property `Reset()` operations for classes implementing `IPoolable`. |
| **`ConfigurationIgnoreCodeFixProvider`** | `NALIX023`, `NALIX024` | Injects `[ConfiguredIgnore]` to fields that must be excluded from static configuration binding. |
| **`MiddlewareCodeFixProvider`** | `NALIX030`, `NALIX031`, `NALIX032` | Templates and corrects required signatures for connection and request middleware pipelines. |
| **`DispatchLoopCountCodeFixProvider`** | `NALIX047` | Configures correct execution thread loop boundary counts on dispatch systems. |
| **`RedundantPacketCastCodeFixProvider`** | `NALIX055` | Safely refactors and cleans up redundant class type casts in packet handling logic. |
| **`NullMiddlewareCodeFixProvider`** | `NALIX056` | Repairs boundary checks on null references inside active middleware routing nodes. |
| **`RequestOptionsConsistencyCodeFixProvider`** | `NALIX057` | Standardizes inconsistent timeout configurations across FFI message boundaries. |
| **`GenericPacketHandlerCodeFixProvider`** | `NALIX058` | Aligns mismatched generic packet type declarations on async listeners. |

## Key Namespaces

| Namespace | Purpose | Key Types |
| :--- | :--- | :--- |
| `Nalix.Analyzers.CodeFixes` | Root namespace containing Roslyn-based `CodeFixProvider` classes implementing C# syntax refactoring | `SerializeOrderMissingCodeFixProvider`, `DuplicateSerializeOrderCodeFixProvider`, `ResetForPoolCodeFixProvider` |

## Installation

This package is installed as a development dependency. It executes entirely within the compiler or IDE process and does not add any overhead to production compilation:

```bash
dotnet add package Nalix.Analyzers.CodeFixes
```

## How It Works

When a diagnostic is raised by `Nalix.Analyzers`, the corresponding provider in `Nalix.Analyzers.CodeFixes` registers a C# `CodeAction`. When selected by a developer, the provider:
1. Parses the syntax tree to locate the target node (e.g., class, property, or attribute list).
2. Performs syntax tree manipulation via a `DocumentEditor` (e.g., inserting attributes, adding numeric literals, or generating new methods).
3. Generates a new `Document` containing the refactored, formatted source code, ensuring instant compliance with Nalix standards.

## IDE Integration Guidelines

- **Batch Fix Support:** Most providers implement `WellKnownFixAllProviders.BatchFixer` allowing you to apply corrections solution-wide (e.g., automatically adding missing serialization orders to all properties across 50+ classes in one click).
- **Code Formatting:** The code fix generation matches the project's formatting rules (spacing, naming conventions, and brackets) to maintain a cohesive codebase.
