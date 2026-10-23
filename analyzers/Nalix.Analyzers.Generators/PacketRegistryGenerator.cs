// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Nalix.Analyzers.Generators.Internal;

namespace Nalix.Analyzers.Generators;

/// <summary>
/// Generates packet registration and fast-dispatch code for packets marked with PacketAttribute.
/// </summary>
[Generator]
public sealed class PacketRegistryGenerator : IIncrementalGenerator
{
    public readonly struct PacketRegistryModel : IEquatable<PacketRegistryModel>
    {
        public string FullyQualifiedName { get; }
        public string ErrorMessageName { get; }
        public string GeneratedNamespace { get; }
        public ushort? StaticOpCode { get; }
        public Location? Location { get; }

        public PacketRegistryModel(
            string fullyQualifiedName,
            string errorMessageName,
            string generatedNamespace,
            ushort? staticOpCode,
            Location? location)
        {
            this.FullyQualifiedName = fullyQualifiedName;
            this.ErrorMessageName = errorMessageName;
            this.GeneratedNamespace = generatedNamespace;
            this.StaticOpCode = staticOpCode;
            this.Location = location;
        }

        public bool Equals(PacketRegistryModel other) =>
            this.FullyQualifiedName == other.FullyQualifiedName &&
            this.ErrorMessageName == other.ErrorMessageName &&
            this.GeneratedNamespace == other.GeneratedNamespace &&
            this.StaticOpCode == other.StaticOpCode;

        public override bool Equals(object obj) => obj is PacketRegistryModel other && this.Equals(other);
        public override int GetHashCode() => (this.FullyQualifiedName, this.ErrorMessageName, this.GeneratedNamespace, this.StaticOpCode).GetHashCode();
    }

    #region Constants and Diagnostics

    #endregion Constants and Diagnostics

    #region IIncrementalGenerator Implementation

    /// <summary>
    /// Registers the incremental source-generation pipeline.
    /// </summary>
    /// <param name="context">The Roslyn incremental generator initialization context.</param>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<PacketRegistryModel> packets = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                KnownNames.PacketAttributeMetadataName,
                predicate: static (node, _) => node is TypeDeclarationSyntax { BaseList: not null },
                transform: static (ctx, _) => GET_PACKET_TYPE(ctx))
            .Where(static symbol => symbol.FullyQualifiedName != null);

        IncrementalValueProvider<ImmutableArray<PacketRegistryModel>> collected = packets.Collect();

        context.RegisterSourceOutput(collected, static (spc, source) => Execute(source, spc));
    }

    #endregion IIncrementalGenerator Implementation

    #region Private Helpers

    private static void Execute(ImmutableArray<PacketRegistryModel> targets, SourceProductionContext context)
    {
        HashSet<string> seen = new();

        List<PacketRegistryModel> distinctPackets = [.. targets
            .Where(p => p.FullyQualifiedName != null && seen.Add(p.FullyQualifiedName))
            .OrderBy(static p => p.FullyQualifiedName)];

        if (distinctPackets.Count == 0)
        {
            return;
        }

        List<PacketRegistryModel> validPackets = new(distinctPackets.Count);
        Dictionary<ushort, PacketRegistryModel> seenOpcodes = new();

        foreach (PacketRegistryModel packet in distinctPackets)
        {
            if (packet.StaticOpCode.HasValue)
            {
                ushort opCode = packet.StaticOpCode.Value;
                if (seenOpcodes.TryGetValue(opCode, out PacketRegistryModel existing))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        GeneratorDiagnosticDescriptors.DuplicatePacketStaticOpCode,
                        packet.Location ?? Location.None,
                        packet.ErrorMessageName,
                        opCode,
                        existing.ErrorMessageName));
                    continue;
                }

                seenOpcodes.Add(opCode, packet);
            }

            validPackets.Add(packet);
        }

        if (validPackets.Count == 0)
        {
            return;
        }

        string generatedNamespace = validPackets[0].GeneratedNamespace;

        EMIT_BOOTSTRAP(context, validPackets, generatedNamespace);
    }

    private static PacketRegistryModel GET_PACKET_TYPE(GeneratorAttributeSyntaxContext context)
    {
        if (context.TargetSymbol is not INamedTypeSymbol symbol)
        {
            return default;
        }

        if (symbol.IsAbstract || symbol.TypeKind == TypeKind.Interface || symbol.IsGenericType)
        {
            return default;
        }

        if (!INHERITS_PACKET_BASE_OF_SELF(symbol))
        {
            return default;
        }

        ushort? staticOpCode = TRY_GET_STATIC_OPCODE(symbol, context.SemanticModel, out Location? location);

        return new PacketRegistryModel(
            symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
            SourceGenNamespaces.Get(symbol),
            staticOpCode,
            location);
    }

    private static ushort? TRY_GET_STATIC_OPCODE(INamedTypeSymbol symbol, SemanticModel semanticModel, out Location? location)
    {
        IPropertySymbol? prop = symbol.GetMembers("StaticOpCode")
            .OfType<IPropertySymbol>()
            .FirstOrDefault(static p => p.IsStatic);

        location = prop?.Locations.FirstOrDefault(static l => l.IsInSource)
            ?? symbol.Locations.FirstOrDefault(static l => l.IsInSource);

        if (prop is null)
        {
            return null;
        }

        foreach (SyntaxReference syntaxRef in prop.DeclaringSyntaxReferences)
        {
            if (syntaxRef.GetSyntax() is not PropertyDeclarationSyntax propSyntax)
            {
                continue;
            }

            ExpressionSyntax? expr = propSyntax.ExpressionBody?.Expression ?? propSyntax.Initializer?.Value;
            if (expr is null && propSyntax.AccessorList is not null)
            {
                AccessorDeclarationSyntax? getter = propSyntax.AccessorList.Accessors
                    .FirstOrDefault(static a => a.Keyword.ValueText == "get");
                expr = getter?.ExpressionBody?.Expression
                    ?? getter?.Body?.Statements.OfType<ReturnStatementSyntax>().FirstOrDefault()?.Expression;
            }

            if (expr is null)
            {
                continue;
            }

            SemanticModel model = ReferenceEquals(semanticModel.SyntaxTree, expr.SyntaxTree)
                ? semanticModel
                : semanticModel.Compilation.GetSemanticModel(expr.SyntaxTree);

            Optional<object?> constant = model.GetConstantValue(expr);
            if (!constant.HasValue || constant.Value is null)
            {
                continue;
            }

            switch (constant.Value)
            {
                case ushort u:
                    return u;
                case int i and >= 0 and <= ushort.MaxValue:
                    return (ushort)i;
                case short s and >= 0:
                    return (ushort)s;
                case byte b:
                    return b;
                case uint ui and <= ushort.MaxValue:
                    return (ushort)ui;
                case long l and >= 0 and <= ushort.MaxValue:
                    return (ushort)l;
                case ulong ul and <= ushort.MaxValue:
                    return (ushort)ul;
                default:
                    break;
            }
        }

        return null;
    }

    private static bool INHERITS_PACKET_BASE_OF_SELF(INamedTypeSymbol symbol)
    {
        INamedTypeSymbol? current = symbol.BaseType;
        while (current is not null)
        {
            if (current.Name == KnownNames.PacketBaseName &&
                current.ContainingNamespace.ToDisplayString() == KnownNames.PacketBaseNamespace &&
                current.TypeArguments.Length == 1 &&
                SymbolEqualityComparer.Default.Equals(current.TypeArguments[0], symbol))
            {
                return true;
            }

            current = current.BaseType;
        }

        return false;
    }

    private static void EMIT_BOOTSTRAP(SourceProductionContext context, IReadOnlyList<PacketRegistryModel> targets, string generatedNamespace)
    {
        StringBuilder sb = new();
        _ = sb.AppendLine("// <auto-generated/>");
        _ = sb.AppendLine("// Copyright (c) 2026 PPN Corporation. All rights reserved.");
        _ = sb.AppendLine("// Licensed under the Apache License, Version 2.0.");
        _ = sb.AppendLine();
        _ = sb.AppendLine("// DO NOT EDIT MANUALLY.");
        _ = sb.AppendLine("// Design goals:");
        _ = sb.AppendLine("// - Zero runtime reflection");
        _ = sb.AppendLine("// - O(1) packet dispatch");
        _ = sb.AppendLine("// - Allocation-free hot path");
        _ = sb.AppendLine("// - AOT-friendly code generation");
        _ = sb.AppendLine();
        _ = sb.AppendLine("#pragma warning disable CS1591");
        _ = sb.AppendLine("#nullable enable");
        _ = sb.AppendLine();
        _ = sb.AppendLine("using System.Runtime.CompilerServices;");
        _ = sb.AppendLine("using System.Threading;");
        _ = sb.AppendLine();
        _ = sb.AppendLine($"namespace {generatedNamespace};");
        _ = sb.AppendLine();
        _ = sb.AppendLine("internal static class PacketRegistryGenerated");
        _ = sb.AppendLine("{");
        _ = sb.AppendLine("    private static int s_registered;");
        _ = sb.AppendLine();
        _ = sb.AppendLine("    // Ensures packet registration happens automatically at module load time.");
        _ = sb.AppendLine("    // This avoids manual initialization and guarantees availability before any network usage.");
        _ = sb.AppendLine("    [ModuleInitializer]");
        _ = sb.AppendLine("    internal static void Initialize() => Register();");
        _ = sb.AppendLine();
        _ = sb.AppendLine("    internal static void Register()");
        _ = sb.AppendLine("    {");
        _ = sb.AppendLine("        if (Interlocked.Exchange(ref s_registered, 1) != 0)");
        _ = sb.AppendLine("        {");
        _ = sb.AppendLine("            return;");
        _ = sb.AppendLine("        }");
        _ = sb.AppendLine();

        foreach (PacketRegistryModel packet in targets)
        {
            _ = sb.AppendLine($"        global::{KnownNames.PacketBaseNamespace}.{KnownNames.PacketRegistryName}.RegisterGenerated<{packet.FullyQualifiedName}>(");
            _ = sb.AppendLine($"            \"{ESCAPE(packet.ErrorMessageName)}\",");
            _ = sb.AppendLine($"            static raw => {packet.FullyQualifiedName}.Deserialize(raw));");
            _ = sb.AppendLine();
        }

        _ = sb.AppendLine("    }");
        _ = sb.AppendLine("}");

        context.AddSource("PacketRegistryGenerated.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
    }

    private static string ESCAPE(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    #endregion Private Helpers
}
