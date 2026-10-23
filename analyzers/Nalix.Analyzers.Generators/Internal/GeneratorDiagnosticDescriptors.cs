// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Microsoft.CodeAnalysis;

namespace Nalix.Analyzers.Generators.Internal;

/// <summary>
/// Centralized diagnostic descriptors shared across all source generators.
/// <para>
/// ID ranges:<br/>
/// • NALIX001–NALIX058 — <c>Nalix.Analyzers</c> (Roslyn analyzers)<br/>
/// • NALIX059–NALIX069 — <c>Nalix.Analyzers.Generators</c> (source generators)
/// </para>
/// </summary>
internal static class GeneratorDiagnosticDescriptors
{
    // ═══════════════════════════════════════════════════════════════════════════
    //  SerializeFormatterGenerator
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>NALIX059: Type marked [GenerateFormatter] has no public/internal static Create() method.</summary>
    public static readonly DiagnosticDescriptor MissingStaticCreateMethod = new(
        id: "NALIX059",
        title: "Missing static Create() method",
        messageFormat: "Type '{0}' is marked [GenerateFormatter] but neither it nor any of its base types has a public/internal static Create() method. " +
                       "This is required for the pooling pattern used by LiteSerializer.Fill.",
        category: "Nalix.Codec.Serialization",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    // ═══════════════════════════════════════════════════════════════════════════
    //  PacketSchemaGenerator
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>NALIX060: Packet class inherits from PacketBase but is not partial.</summary>
    public static readonly DiagnosticDescriptor PacketClassMustBePartial = new(
        id: "NALIX060",
        title: "Packet class must be partial",
        messageFormat: "The class '{0}' inherits from PacketBase but is not partial. Source generation requires partial classes to override Length and ResetForPool.",
        category: "Nalix.Codec.Serialization",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    // ═══════════════════════════════════════════════════════════════════════════
    //  InstanceGenerator — [Injectable] validation
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>NALIX063: [Injectable] class has no public or internal constructor.</summary>
    public static readonly DiagnosticDescriptor NoAccessibleConstructor = new(
        id: "NALIX063",
        title: "No accessible constructor found",
        messageFormat: "Class '{0}' marked with [Injectable] must have at least one public or internal constructor",
        category: "Nalix.Framework.Injection",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>NALIX064: [Injectable] class has ambiguous constructors with the same non-config arity.</summary>
    public static readonly DiagnosticDescriptor AmbiguousConstructor = new(
        id: "NALIX064",
        title: "Ambiguous constructors in injectable class",
        messageFormat: "Class '{0}' marked with [Injectable] has ambiguous constructors with the same parameter count that are not disjoint",
        category: "Nalix.Framework.Injection",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>NALIX065: SingletonBase subclass is missing an accessible parameterless constructor.</summary>
    public static readonly DiagnosticDescriptor SingletonMissingParameterlessConstructor = new(
        id: "NALIX065",
        title: "SingletonBase subclass missing accessible parameterless constructor",
        messageFormat: "SingletonBase subclass '{0}' must have a public or internal parameterless constructor for source-generated activation",
        category: "Nalix.Framework.Injection",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    // ═══════════════════════════════════════════════════════════════════════════
    //  RpcClientGenerator
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>NALIX066: [RpcService] method return type is not one of the shapes the RPC client proxy can generate.</summary>
    public static readonly DiagnosticDescriptor RpcServiceUnsupportedGeneratedReturnType = new(
        id: "NALIX066",
        title: "Unsupported [RpcService] method return type",
        messageFormat: "Method '{0}' on [RpcService] interface '{1}' has return type '{2}', which the RPC client generator cannot emit a body for. " +
                       "Supported return types: void-equivalent ValueTask/Task, ValueTask<T>/Task<T>, RpcCall<T>, RpcStream<T>.",
        category: "Nalix.SDK.Transport",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    // ═══════════════════════════════════════════════════════════════════════════
    //  PacketHandlerGenerator
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>NALIX067: Trailing handler parameter is missing [FromScope] or is not a reference type; the method was dropped from the dispatch table.</summary>
    public static readonly DiagnosticDescriptor InvalidScopeParameter = new(
        id: "NALIX067",
        title: "Handler method dropped from dispatch table",
        messageFormat: "Method '{0}' has a trailing parameter '{1}' that is missing [FromScope] or is not a reference type; " +
                       "the method was silently excluded from the generated dispatch table for [PacketHandler] controller '{2}'",
        category: "Nalix.Runtime.Dispatching",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    // ═══════════════════════════════════════════════════════════════════════════
    //  PacketRegistryGenerator
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>NALIX068: Two or more [Packet] types declare the same StaticOpCode in the same compilation.</summary>
    public static readonly DiagnosticDescriptor DuplicatePacketStaticOpCode = new(
        id: "NALIX068",
        title: "Duplicate packet StaticOpCode",
        messageFormat: "Packet '{0}' declares StaticOpCode 0x{1:X4} ({1}), which is already registered by packet '{2}'",
        category: "Nalix.Codec.DataFrames",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
