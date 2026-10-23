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
/// Incremental generator that emits Length, ResetForPool and metadata schema for packets.
/// Eliminates runtime reflection and delegate overhead in PacketTypeCache.
/// All wire-size calculations are performed entirely at compile-time;
/// neither WireSizeProvider nor PacketBaseElementSizer is referenced from generated code.
/// </summary>
[Generator]
public sealed class PacketSchemaGenerator : IIncrementalGenerator
{
    public readonly struct PacketSchemaModel : IEquatable<PacketSchemaModel>
    {
        public string HintName { get; }
        public string SourceCode { get; }
        public string FullTypeName { get; }
        public int StaticSize { get; }
        public string? GeneratedNamespace { get; }
        public Diagnostic? Diagnostic { get; }

        public PacketSchemaModel(string hintName, string sourceCode, string fullTypeName, int staticSize, string? generatedNamespace, Diagnostic? diagnostic)
        {
            this.HintName = hintName;
            this.SourceCode = sourceCode;
            this.FullTypeName = fullTypeName;
            this.StaticSize = staticSize;
            this.GeneratedNamespace = generatedNamespace;
            this.Diagnostic = diagnostic;
        }

        public bool Equals(PacketSchemaModel other)
        {
            if (this.HintName != other.HintName ||
                this.SourceCode != other.SourceCode ||
                this.FullTypeName != other.FullTypeName ||
                this.StaticSize != other.StaticSize ||
                this.GeneratedNamespace != other.GeneratedNamespace)
            {
                return false;
            }
            if (this.Diagnostic == null && other.Diagnostic == null)
            {
                return true;
            }

            if (this.Diagnostic != null && other.Diagnostic != null)
            {
                return this.Diagnostic.Equals(other.Diagnostic);
            }

            return false;
        }

        public override bool Equals(object obj) => obj is PacketSchemaModel other && this.Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = (hash * 23) + (this.HintName?.GetHashCode() ?? 0);
                hash = (hash * 23) + (this.SourceCode?.GetHashCode() ?? 0);
                hash = (hash * 23) + (this.FullTypeName?.GetHashCode() ?? 0);
                hash = (hash * 23) + this.StaticSize.GetHashCode();
                hash = (hash * 23) + (this.GeneratedNamespace?.GetHashCode() ?? 0);
                hash = (hash * 23) + (this.Diagnostic?.GetHashCode() ?? 0);
                return hash;
            }
        }
    }

    /// <inheritdoc/>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<PacketSchemaModel> provider = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) =>
                    node is ClassDeclarationSyntax cds &&
                    !cds.Modifiers.Any(static m =>
                        m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.AbstractKeyword) ||
                        m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.StaticKeyword)),
                transform: static (ctx, _) => GetSemanticTarget(ctx))
            .Where(static m => m.FullTypeName != null);

        context.RegisterSourceOutput(provider.Collect(), this.Execute);
    }

    private static PacketSchemaModel GetSemanticTarget(GeneratorSyntaxContext context)
    {
        if (context.Node is not ClassDeclarationSyntax classDef)
        {
            return default;
        }

        if (context.SemanticModel.GetDeclaredSymbol(classDef) is not INamedTypeSymbol symbol)
        {
            return default;
        }

        ITypeSymbol? baseType = symbol.BaseType;
        bool isPacket = false;
        while (baseType != null)
        {
            if (baseType.Name == KnownNames.PacketBaseName && baseType is INamedTypeSymbol { IsGenericType: true })
            {
                isPacket = true;
                break;
            }
            baseType = baseType.BaseType;
        }

        if (!isPacket)
        {
            return default;
        }

        string fullTypeName = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        string hintName = fullTypeName
            .Replace("global::", "")
            .Replace("<", "_").Replace(">", "_")
            .Replace(",", "_").Replace(" ", "");
        string generatedNamespace = SourceGenNamespaces.Get(symbol);

        if (!IS_PARTIAL(symbol))
        {
            return new PacketSchemaModel(
                hintName, "", fullTypeName, 0, generatedNamespace,
                Diagnostic.Create(GeneratorDiagnosticDescriptors.PacketClassMustBePartial, symbol.Locations.FirstOrDefault() ?? Location.None, symbol.Name));
        }

        string sourceCode = GENERATE_PACKET_PARTIAL(symbol, out int staticSize);
        return new PacketSchemaModel(hintName, sourceCode, fullTypeName, staticSize, generatedNamespace, null);
    }

    private void Execute(SourceProductionContext context, ImmutableArray<PacketSchemaModel> targets)
    {
        if (targets.IsDefaultOrEmpty)
        {
            return;
        }

        HashSet<string> processed = new();
        List<PacketSchemaModel> distinctTargets = new();

        foreach (PacketSchemaModel m in targets)
        {
            if (m.Diagnostic != null)
            {
                context.ReportDiagnostic(m.Diagnostic);
            }

            if (m.FullTypeName != null && processed.Add(m.FullTypeName) && !string.IsNullOrEmpty(m.SourceCode))
            {
                distinctTargets.Add(m);
            }
        }

        if (distinctTargets.Count == 0)
        {
            return;
        }

        string generatedNamespace = distinctTargets[0].GeneratedNamespace!;

        StringBuilder initBuilder = new();
        _ = initBuilder.AppendLine("// <auto-generated/>");
        _ = initBuilder.AppendLine($"namespace {generatedNamespace};");
        _ = initBuilder.AppendLine();
        _ = initBuilder.AppendLine("internal static class PacketSchemaGenerated");
        _ = initBuilder.AppendLine("{");
        _ = initBuilder.AppendLine("    [System.Runtime.CompilerServices.ModuleInitializer]");
        _ = initBuilder.AppendLine("    internal static void Initialize()");
        _ = initBuilder.AppendLine("    {");

        foreach (PacketSchemaModel m in distinctTargets)
        {
            _ = initBuilder.AppendLine($"        global::{KnownNames.PacketBaseNamespace}.{KnownNames.PacketSchemaName}<{m.FullTypeName}>.StaticSize = {m.StaticSize};");
            context.AddSource($"{m.HintName}.Packet.g.cs", SourceText.From(m.SourceCode, Encoding.UTF8));
        }

        _ = initBuilder.AppendLine("    }");
        _ = initBuilder.AppendLine("}");

        context.AddSource("PacketSchemaGenerated.g.cs", SourceText.From(initBuilder.ToString(), Encoding.UTF8));
    }

    private static string GENERATE_PACKET_PARTIAL(ITypeSymbol type, out int staticSize)
    {
        string ns = type.ContainingNamespace.IsGlobalNamespace
                            ? ""
                            : $"namespace {type.ContainingNamespace.ToDisplayString()};";
        string typeName = type.Name;
        staticSize = 0;

        List<ISymbol> members = SerializationMember.Resolve(type);

        StringBuilder sb = new();
        _ = sb.AppendLine("// <auto-generated/>");
        _ = sb.AppendLine("#pragma warning disable CS1591");
        _ = sb.AppendLine("#nullable enable");
        _ = sb.AppendLine();

        if (!string.IsNullOrEmpty(ns))
        {
            _ = sb.AppendLine(ns);
        }

        _ = sb.AppendLine();

        // Nesting support
        List<ITypeSymbol> nesting = new();
        INamedTypeSymbol? parent = type.ContainingType;
        while (parent != null)
        {
            nesting.Insert(0, parent);
            parent = parent.ContainingType;
        }

        foreach (ITypeSymbol parentType in nesting)
        {
            _ = sb.AppendLine($"partial class {parentType.Name}");
            _ = sb.AppendLine("{");
        }

        _ = sb.AppendLine($"partial class {typeName}");
        _ = sb.AppendLine("{");

        // ---- Length property ----
        bool isFixedSize = type.AllInterfaces.Any(i => i.ToDisplayString().Contains(KnownNames.IFixedSizeSerializableName));

        if (isFixedSize)
        {
            foreach (ISymbol member in members)
            {
                ITypeSymbol memberType = GET_MEMBER_TYPE(member);
                int fixedMemberSize = GET_FIXED_SIZE(memberType);

                staticSize += fixedMemberSize > 0
                    ? fixedMemberSize
                    : 4; // minimum length-prefix for dynamic fields
            }

            _ = sb.AppendLine("    /// <inheritdoc/>");
            _ = sb.AppendLine($"    [global::{KnownNames.SerializationAbstractionsNamespace}.{KnownNames.SerializeIgnoreShortName}]");
            _ = sb.AppendLine($"    public static int Size => {staticSize};");
            _ = sb.AppendLine();
            _ = sb.AppendLine("    /// <inheritdoc/>");
            _ = sb.AppendLine($"    [global::{KnownNames.SerializationAbstractionsNamespace}.{KnownNames.SerializeIgnoreShortName}]");
            _ = sb.AppendLine("    public override int Length => Size;");
        }
        else
        {
            _ = sb.AppendLine("    /// <inheritdoc/>");
            _ = sb.AppendLine($"    [global::{KnownNames.AbstractionsNamespace}.{KnownNames.SkipCleanName}]");
            _ = sb.AppendLine($"    [global::{KnownNames.SerializationAbstractionsNamespace}.{KnownNames.SerializeIgnoreShortName}]");
            _ = sb.AppendLine("    public override int Length");
            _ = sb.AppendLine("    {");
            _ = sb.AppendLine("        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
            _ = sb.AppendLine("        get");
            _ = sb.AppendLine("        {");
            _ = sb.AppendLine("            int size = 0;");

            foreach (ISymbol member in members)
            {
                ITypeSymbol memberType = GET_MEMBER_TYPE(member);
                string memberName = member.Name;
                int fixedSize = GET_FIXED_SIZE(memberType);

                if (fixedSize > 0)
                {
                    _ = sb.AppendLine($"            size += {fixedSize}; // {memberType.Name} {memberName}");
                    staticSize += fixedSize;
                }
                else
                {
                    string sizeExpr = BUILD_SIZE_EXPRESSION(memberType, $"this.{memberName}");
                    _ = sb.AppendLine($"            size += {sizeExpr};");
                    staticSize += GET_MIN_SIZE(memberType);
                }
            }

            _ = sb.AppendLine("            return size;");
            _ = sb.AppendLine("        }");
            _ = sb.AppendLine("    }");
            _ = sb.AppendLine();
        }

        // ---- ResetForPool ----
        bool hasResetOverride = type.GetMembers()
            .OfType<IMethodSymbol>()
            .Any(m => m.Name == "ResetForPool" && m.IsOverride && !m.IsImplicitlyDeclared);

        if (!hasResetOverride)
        {
            _ = sb.AppendLine("    /// <inheritdoc/>");
            _ = sb.AppendLine("    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]");
            _ = sb.AppendLine("    public override void ResetForPool()");
            _ = sb.AppendLine("    {");
            _ = sb.AppendLine("        base.ResetForPool();");
            _ = sb.AppendLine();

            List<ISymbol> resetMembers = RESOLVE_RESET_MEMBERS(type, members);
            foreach (ISymbol member in resetMembers)
            {
                ITypeSymbol memberType = GET_MEMBER_TYPE(member);
                _ = sb.AppendLine(BUILD_RESET_STATEMENT(memberType, member.Name));
            }

            _ = sb.AppendLine("    }");
        }

        _ = sb.AppendLine("}");

        for (int i = 0; i < nesting.Count; i++)
        {
            _ = sb.AppendLine("}");
        }

        return sb.ToString();
    }

    private static int GET_FIXED_SIZE(ITypeSymbol type)
    {
        if (type.TypeKind == TypeKind.Enum)
        {
            return GET_FIXED_SIZE(((INamedTypeSymbol)type).EnumUnderlyingType!);
        }

#pragma warning disable IDE0010 // Add missing cases
        switch (type.SpecialType)
        {
            case SpecialType.System_Boolean:
            case SpecialType.System_Byte:
            case SpecialType.System_SByte: return 1;

            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
            case SpecialType.System_Char: return 2;

            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
            case SpecialType.System_Single: return 4;

            case SpecialType.System_Int64:
            case SpecialType.System_UInt64:
            case SpecialType.System_Double:
            case SpecialType.System_DateTime: return 8;

            case SpecialType.System_Decimal: return 16;
        }
#pragma warning restore IDE0010 // Add missing cases

        // ---- All generic / named types ----
        if (type is not INamedTypeSymbol named)
        {
            return 0;
        }

        // Nullable<T>  →  NOT fixed (1-byte tag + optional payload)
        if (named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            return 0;
        }

        // ValueTuple arities 2-5  →  fixed only if ALL items are fixed
        if (IS_VALUE_TUPLE(named))
        {
            return GET_VALUE_TUPLE_FIXED_SIZE(named);
        }

        // [FixedSize(N)] attribute
        int attrSize = GET_FIXED_SIZE_FROM_ATTRIBUTE(named);
        if (attrSize > 0)
        {
            return attrSize;
        }

        // Hard-coded legacy names  (kept for backwards-compat)
        if (named.Name == "Bytes32")
        {
            return 32;
        }

        if (named.Name == "Bytes64")
        {
            return 64;
        }

        // Well-known BCL structs that happen to be unmanaged
        switch (named.Name)
        {
            case KnownNames.GuidName: return 16;
            case KnownNames.TimeSpanName: return 8;
            case KnownNames.TimeOnlyName: return 8;
            case KnownNames.DateOnlyName: return 4;
            case KnownNames.DateTimeOffsetName: return 16;
            default:
                break;
        }

        // Generic unmanaged struct: walk fields and sum up
        // (covers user-defined structs that carry only blittable fields)
        if (named.TypeKind == TypeKind.Struct && IS_UNMANAGED_STRUCT(named))
        {
            return COMPUTE_STRUCT_FIELD_SUM(named);
        }

        // Everything else (string, arrays, collections, …) is dynamic
        return 0;
    }

    private static string BUILD_SIZE_EXPRESSION(ITypeSymbol type, string accessor)
    {
        // ---- Fixed-size shortcut ----
        int fixedSize = GET_FIXED_SIZE(type);
        if (fixedSize > 0)
        {
            return fixedSize.ToString();
        }

        // ---- string ----
        if (type.SpecialType == SpecialType.System_String)
        {
            return $"({accessor} is null ? 4 : 4 + global::System.Text.Encoding.UTF8.GetByteCount({accessor}))";
        }

        // ---- Nullable<T> ----
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullableNamed)
        {
            ITypeSymbol inner = nullableNamed.TypeArguments[0];
            int innerFixed = GET_FIXED_SIZE(inner);
            string innerExpr = innerFixed > 0
                ? $"(1 + {innerFixed})"
                : $"(1 + {BUILD_SIZE_EXPRESSION(inner, $"{accessor}.Value")})";

            return $"({accessor}.HasValue ? {innerExpr} : 1)";
        }

        // ---- Arrays ----
        if (type is IArrayTypeSymbol arrayType)
        {
            return BUILD_ARRAY_SIZE_EXPRESSION(arrayType, accessor);
        }

        if (type is INamedTypeSymbol named)
        {
            // ---- ValueTuple (mixed fixed+dynamic items) ----
            if (IS_VALUE_TUPLE(named))
            {
                return BUILD_VALUE_TUPLE_SIZE_EXPRESSION(named, accessor);
            }

            if (named.IsGenericType)
            {
                ITypeSymbol[] args = [.. named.TypeArguments];
                string def = named.OriginalDefinition.ToDisplayString();

                // List<T> / HashSet<T> / Queue<T> / Stack<T>
                if (def is "System.Collections.Generic.List<T>" or "System.Collections.Generic.HashSet<T>" or "System.Collections.Generic.Queue<T>" or "System.Collections.Generic.Stack<T>")
                {
                    return BUILD_SEQUENCE_SIZE_EXPRESSION(args[0], accessor, countProperty: "Count");
                }

                // Dictionary<TKey, TValue>
                if (def == "System.Collections.Generic.Dictionary<TKey, TValue>")
                {
                    return BUILD_DICTIONARY_SIZE_EXPRESSION(args[0], args[1], accessor);
                }

                // Memory<T> / ReadOnlyMemory<T>  (T must be unmanaged)
                if (def is "System.Memory<T>" or "System.ReadOnlyMemory<T>")
                {
                    ITypeSymbol elemType = args[0];
                    int elemFixed = GET_FIXED_SIZE(elemType);
                    if (elemFixed > 0)
                    {
                        return $"(4 + ({accessor}.Length * {elemFixed}))";
                    }

                    // Unsupported element type — emit 4 (length-prefix only)
                    return "4";
                }
            }

            // ---- IPacket (concrete or polymorphic) ----
            if (IMPLEMENTSI_PACKET(named))
            {
                return $"({accessor} is null ? 0 : {accessor}.Length)";
            }
        }

        // ---- Custom Classes / Structs (Fallback) ----
        if (type.TypeKind is TypeKind.Class or TypeKind.Struct)
        {
            INamedTypeSymbol namedType = (INamedTypeSymbol)type;
            List<IPropertySymbol> props = [.. namedType.GetMembers()
                .OfType<IPropertySymbol>()
                .Where(p => p.DeclaredAccessibility == Accessibility.Public && !p.IsStatic && !p.IsReadOnly)];
            List<IFieldSymbol> fields = [.. namedType.GetMembers()
                .OfType<IFieldSymbol>()
                .Where(f => f.DeclaredAccessibility == Accessibility.Public && !f.IsStatic && !f.IsReadOnly)];

            if (props.Count == 0 && fields.Count == 0)
            {
                return "0";
            }

            if (type.IsTupleType)
            {
                INamedTypeSymbol tuple = (INamedTypeSymbol)type;

                // ValueTuples have both named elements and Item1, Item2 aliases. 
                // We use the underlying elements to ensure we only count each logical field once.
                List<string> tupleParts = new();
                for (int i = 0; i < tuple.TupleElements.Length; i++)
                {
                    IFieldSymbol e = tuple.TupleElements[i];

                    // Use Item1, Item2... as the accessor to avoid confusion between names and aliases
                    tupleParts.Add(BUILD_SIZE_EXPRESSION(e.Type, $"{accessor}.Item{i + 1}"));
                }
                return $"({string.Join(" + ", tupleParts)})";
            }

            List<string> parts = new();
            foreach (IPropertySymbol p in props)
            {
                parts.Add(BUILD_SIZE_EXPRESSION(p.Type, $"{accessor}.{p.Name}"));
            }

            foreach (IFieldSymbol f in fields)
            {
                parts.Add(BUILD_SIZE_EXPRESSION(f.Type, $"{accessor}.{f.Name}"));
            }

            string sum = string.Join(" + ", parts);
            if (type.IsReferenceType)
            {
                return $"({accessor} is null ? 0 : {sum})";
            }

            return $"({sum})";
        }

        return "0";
    }

    private static int GET_MIN_SIZE(ITypeSymbol type)
    {
        int fixedSize = GET_FIXED_SIZE(type);
        if (fixedSize > 0)
        {
            return fixedSize;
        }

        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T })
        {
            return 1;
        }

        if (type is INamedTypeSymbol named && IMPLEMENTSI_PACKET(named))
        {
            return 0;
        }

        if (type.IsTupleType && type is INamedTypeSymbol tuple)
        {
            int size = 0;
            for (int i = 0; i < tuple.TupleElements.Length; i++)
            {
                size += GET_MIN_SIZE(tuple.TupleElements[i].Type);
            }

            return size;
        }

        return 4;
    }

    private static string BUILD_ARRAY_SIZE_EXPRESSION(IArrayTypeSymbol arrayType, string accessor)
    {
        ITypeSymbol elem = arrayType.ElementType;
        string nullCheck = $"{accessor} is null";

        // string[]
        if (elem.SpecialType == SpecialType.System_String)
        {
            // emit: accessor is null ? 4 : 4 + Σ GetStringWireSize(element)
            // We inline it as a helper call to keep the generated code readable.
            return BUILD_STRING_ARRAY_SIZE_EXPRESSION(accessor);
        }

        // Nullable<T>[]  e.g. int?[]
        if (elem is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullableElem)
        {
            ITypeSymbol inner = nullableElem.TypeArguments[0];
            int innerFixed = GET_FIXED_SIZE(inner);
            string perItemExpr = innerFixed > 0
                ? $"(item.HasValue ? 1 + {innerFixed} : 1)"
                : $"(item.HasValue ? 1 + {BUILD_SIZE_EXPRESSION(inner, "item.Value")} : 1)";

            return $"({nullCheck} ? 4 : ((global::System.Func<int>)(() => {{ int _s = 4; foreach (var item in {accessor}) _s += {perItemExpr}; return _s; }}))())";
        }

        // Enum[] or unmanaged[]  →  fixed per element
        int elemFixed = GET_FIXED_SIZE(elem);
        if (elemFixed > 0)
        {
            return $"({nullCheck} ? 4 : 4 + ({accessor}.Length * {elemFixed}))";
        }

        // Enum[] with unknown underlying (shouldn't happen, but guard)
        if (elem.TypeKind == TypeKind.Enum)
        {
            int underlyingSize = GET_FIXED_SIZE(((INamedTypeSymbol)elem).EnumUnderlyingType!);
            if (underlyingSize > 0)
            {
                return $"({nullCheck} ? 4 : 4 + ({accessor}.Length * {underlyingSize}))";
            }
        }

        // Reference element arrays (e.g. object[]) — per-element dynamic
        return $"({nullCheck} ? 4 : ((global::System.Func<int>)(() => {{ int _s = 4; foreach (var item in {accessor}) _s += {BUILD_SIZE_EXPRESSION(elem, "item")}; return _s; }}))())";
    }

    // Inline loop for string[]:
    //   accessor is null ? 4
    //       : (() => { int _s = 4; foreach (var s in accessor) _s += s is null ? 4 : 4 + UTF8.GetByteCount(s); return _s; })()
    private static string BUILD_STRING_ARRAY_SIZE_EXPRESSION(string accessor) =>
        $"({accessor} is null ? 4 : ((global::System.Func<int>)(() => {{ int _s = 4; foreach (var _str in {accessor}) _s += _str is null ? 4 : 4 + global::System.Text.Encoding.UTF8.GetByteCount(_str); return _s; }}))())";

    // -------------------------------------------------------------------------
    // Sequence (List / HashSet / Queue / Stack) size helper
    // -------------------------------------------------------------------------

    private static string BUILD_SEQUENCE_SIZE_EXPRESSION(ITypeSymbol elemType, string accessor, string countProperty)
    {
        string nullCheck = $"{accessor} is null";
        int elemFixed = GET_FIXED_SIZE(elemType);

        // Fast path: fixed-size elements
        if (elemFixed > 0)
        {
            return $"({nullCheck} ? 4 : 4 + ({accessor}.{countProperty} * {elemFixed}))";
        }

        // string elements
        if (elemType.SpecialType == SpecialType.System_String)
        {
            return BUILD_STRING_ARRAY_SIZE_EXPRESSION(accessor);
        }

        // Nullable<T> elements
        if (elemType is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullableElem)
        {
            ITypeSymbol inner = nullableElem.TypeArguments[0];
            int innerFixed = GET_FIXED_SIZE(inner);
            string perItemExpr = innerFixed > 0
                ? $"(item.HasValue ? 1 + {innerFixed} : 1)"
                : $"(item.HasValue ? 1 + {BUILD_SIZE_EXPRESSION(inner, "item.Value")} : 1)";

            return $"({nullCheck} ? 4 : ((global::System.Func<int>)(() => {{ int _s = 4; foreach (var item in {accessor}) _s += {perItemExpr}; return _s; }}))())";
        }

        // Dynamic elements — recursive
        return $"({nullCheck} ? 4 : ((global::System.Func<int>)(() => {{ int _s = 4; foreach (var item in {accessor}) _s += {BUILD_SIZE_EXPRESSION(elemType, "item")}; return _s; }}))())";
    }

    private static string BUILD_DICTIONARY_SIZE_EXPRESSION(ITypeSymbol keyType, ITypeSymbol valueType, string accessor)
    {
        string nullCheck = $"{accessor} is null";
        int keyFixed = GET_FIXED_SIZE(keyType);
        int valFixed = GET_FIXED_SIZE(valueType);

        if (keyFixed > 0 && valFixed > 0)
        {
            return $"({nullCheck} ? 4 : 4 + ({accessor}.Count * ({keyFixed} + {valFixed})))";
        }

        // Mixed or both dynamic — foreach loop
        string keyExpr = keyFixed > 0 ? keyFixed.ToString() : BUILD_SIZE_EXPRESSION(keyType, "kvp.Key");
        string valExpr = valFixed > 0 ? valFixed.ToString() : BUILD_SIZE_EXPRESSION(valueType, "kvp.Value");

        return $"({nullCheck} ? 4 : ((global::System.Func<int>)(() => {{ int _s = 4; foreach (var kvp in {accessor}) {{ _s += {keyExpr}; _s += {valExpr}; }} return _s; }}))())";
    }

    private static bool IS_VALUE_TUPLE(INamedTypeSymbol type)
    {
        string name = type.OriginalDefinition.ToDisplayString();
        return name is "System.ValueTuple<T1, T2>" or "System.ValueTuple<T1, T2, T3>" or "System.ValueTuple<T1, T2, T3, T4>" or "System.ValueTuple<T1, T2, T3, T4, T5>";
    }

    private static int GET_VALUE_TUPLE_FIXED_SIZE(INamedTypeSymbol type)
    {
        int total = 0;
        foreach (ITypeSymbol arg in type.TypeArguments)
        {
            int s = GET_FIXED_SIZE(arg);
            if (s == 0)
            {
                return 0; // at least one item is dynamic → whole tuple is dynamic
            }

            total += s;
        }
        return total;
    }

    private static string BUILD_VALUE_TUPLE_SIZE_EXPRESSION(INamedTypeSymbol type, string accessor)
    {
        // Arity 2-5: concatenate Item1..Item5
        string[] items = new string[type.TypeArguments.Length];
        for (int i = 0; i < type.TypeArguments.Length; i++)
        {
            ITypeSymbol arg = type.TypeArguments[i];
            string itemAccessor = $"{accessor}.Item{i + 1}";
            int s = GET_FIXED_SIZE(arg);
            items[i] = s > 0 ? s.ToString() : BUILD_SIZE_EXPRESSION(arg, itemAccessor);
        }
        return $"({string.Join(" + ", items)})";
    }

    private static bool IS_UNMANAGED_STRUCT(INamedTypeSymbol type)
    {
        // Roslyn does not expose IsUnmanaged directly; we approximate by checking
        // that every instance field is itself a fixed-size type (no reference types).
        foreach (IFieldSymbol field in type.GetMembers().OfType<IFieldSymbol>())
        {
            if (field.IsStatic)
            {
                continue;
            }

            if (field.Type.IsReferenceType)
            {
                return false;
            }

            if (GET_FIXED_SIZE(field.Type) == 0)
            {
                return false;
            }
        }
        return true;
    }

    private static int COMPUTE_STRUCT_FIELD_SUM(INamedTypeSymbol type)
    {
        int sum = 0;
        foreach (IFieldSymbol field in type.GetMembers().OfType<IFieldSymbol>())
        {
            if (field.IsStatic)
            {
                continue;
            }

            int s = GET_FIXED_SIZE(field.Type);
            if (s == 0)
            {
                return 0; // safety: unknown field → cannot determine size
            }

            sum += s;
        }
        return sum;
    }

    private static int GET_FIXED_SIZE_FROM_ATTRIBUTE(INamedTypeSymbol type)
    {
        foreach (AttributeData attr in type.GetAttributes())
        {
            if (attr.AttributeClass?.Name == KnownNames.SerializeDynamicSizeAttributeName)
            {
                if (attr.ConstructorArguments.Length > 0 && attr.ConstructorArguments[0].Value is int size)
                {
                    return size;
                }
            }
        }
        return 0;
    }

    private static bool IMPLEMENTSI_PACKET(INamedTypeSymbol type) => type.AllInterfaces.Any(i => i.Name == KnownNames.IPacketInterfaceName);

    private static bool IS_PARTIAL(ITypeSymbol type)
    {
        foreach (SyntaxReference syntaxRef in type.DeclaringSyntaxReferences)
        {
            if (syntaxRef.GetSyntax() is ClassDeclarationSyntax cds && cds.Modifiers.Any(m => m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PartialKeyword)))
            {
                return true;
            }
        }
        return false;
    }

    private static ITypeSymbol GET_MEMBER_TYPE(ISymbol member) => member switch
    {
        IPropertySymbol p => p.Type,
        IFieldSymbol f => f.Type,
        _ => throw new InvalidOperationException($"Unexpected member kind: {member.Kind}")
    };

    private static List<ISymbol> RESOLVE_RESET_MEMBERS(ITypeSymbol type, List<ISymbol> serializedMembers)
    {
        List<ISymbol> result = new(serializedMembers.Count + 4);
        HashSet<string> seenNames = new(StringComparer.Ordinal);

        foreach (ISymbol member in serializedMembers)
        {
            if (member is IPropertySymbol { IsReadOnly: true } or IPropertySymbol { SetMethod.IsInitOnly: true } or IFieldSymbol { IsReadOnly: true })
            {
                continue;
            }

            if (member.Name is "Header" or "_header")
            {
                continue;
            }

            if (HAS_SKIP_CLEAN_OR_INJECT(member))
            {
                continue;
            }

            if (seenNames.Add(member.Name))
            {
                result.Add(member);
            }
        }

        // Also include non-serialized mutable members (e.g. [SerializeIgnore] fields/auto-properties)
        // declared in the packet hierarchy down to PacketBase/FrameBase, unless marked [SkipClean] or [Inject].
        List<ISymbol> extraMembers = new();
        ITypeSymbol? current = type;
        while (current is not null
               && current.SpecialType == SpecialType.None
               && current.Name != KnownNames.PacketBaseName
               && current.Name != "FrameBase")
        {
            ImmutableArray<ISymbol> levelMembers = current.GetMembers();
            bool isTargetType = SymbolEqualityComparer.Default.Equals(current, type);

            foreach (ISymbol member in levelMembers)
            {
                if (member.IsStatic || member.IsImplicitlyDeclared || seenNames.Contains(member.Name))
                {
                    continue;
                }

                if (!isTargetType && member.DeclaredAccessibility == Accessibility.Private)
                {
                    continue;
                }

                if (HAS_SKIP_CLEAN_OR_INJECT(member))
                {
                    continue;
                }

                if (member is IPropertySymbol prop)
                {
                    if (prop.IsReadOnly
                        || prop.IsIndexer
                        || prop.SetMethod is null
                        || prop.SetMethod.IsInitOnly
                        || (!isTargetType && prop.SetMethod.DeclaredAccessibility == Accessibility.Private))
                    {
                        continue;
                    }

                    bool isAutoProperty = levelMembers
                        .OfType<IFieldSymbol>()
                        .Any(f => SymbolEqualityComparer.Default.Equals(f.AssociatedSymbol, prop));
                    if (!isAutoProperty)
                    {
                        continue;
                    }

                    if (seenNames.Add(prop.Name))
                    {
                        extraMembers.Add(prop);
                    }
                }
                else if (member is IFieldSymbol field)
                {
                    if (field.IsReadOnly || field.IsConst)
                    {
                        continue;
                    }

                    bool wrappedByResetProperty = levelMembers
                        .OfType<IPropertySymbol>()
                        .Any(p => seenNames.Contains(p.Name)
                                  && SymbolEqualityComparer.Default.Equals(p.Type, field.Type)
                                  && !levelMembers.OfType<IFieldSymbol>().Any(bf => SymbolEqualityComparer.Default.Equals(bf.AssociatedSymbol, p)));
                    if (wrappedByResetProperty)
                    {
                        continue;
                    }

                    if (seenNames.Add(field.Name))
                    {
                        extraMembers.Add(field);
                    }
                }
            }

            current = current.BaseType;
        }

        result.AddRange(extraMembers);
        return result;
    }

    private static bool HAS_SKIP_CLEAN_OR_INJECT(ISymbol member)
    {
        foreach (AttributeData attr in member.GetAttributes())
        {
            string? name = attr.AttributeClass?.Name;
            if (name is KnownNames.SkipCleanName or "SkipCleanAttribute" or "Inject" or KnownNames.InjectableAttributeName or "InjectAttribute")
            {
                return true;
            }
        }

        return false;
    }

    private static string BUILD_RESET_STATEMENT(ITypeSymbol type, string memberName)
    {
        if (type is INamedTypeSymbol named && named.IsGenericType)
        {
            string def = named.OriginalDefinition.ToDisplayString();
            string fullType = GET_CONSTRUCTIBLE_COLLECTION_TYPE_NAME(named);
            if (def is "System.Collections.Generic.List<T>" or
                "System.Collections.Generic.HashSet<T>" or
                "System.Collections.Generic.Queue<T>" or
                "System.Collections.Generic.Stack<T>" or
                "System.Collections.Generic.Dictionary<TKey, TValue>")
            {
                return $"        this.{memberName} ??= new {fullType}();\n        this.{memberName}.Clear();";
            }
        }

        string resetValue = GET_RESET_VALUE(type);
        return $"        this.{memberName} = {resetValue};";
    }

    private static string GET_RESET_VALUE(ITypeSymbol type)
    {
        if (type.SpecialType == SpecialType.System_String)
        {
            return "string.Empty";
        }

        if (type is IArrayTypeSymbol arrayType)
        {
            return $"global::System.Array.Empty<{arrayType.ElementType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}>()";
        }

        if (type is INamedTypeSymbol named && named.IsGenericType)
        {
            string def = named.OriginalDefinition.ToDisplayString();
            string fullType = GET_CONSTRUCTIBLE_COLLECTION_TYPE_NAME(named);
            if (def is "System.Collections.Generic.List<T>" or "System.Collections.Generic.HashSet<T>" or "System.Collections.Generic.Queue<T>" or "System.Collections.Generic.Stack<T>" or "System.Collections.Generic.Dictionary<TKey, TValue>")
            {
                return $"new {fullType}()";
            }
        }

        if (type.IsReferenceType)
        {
            return "null!";
        }

        return "default";
    }

    private static string GET_FULL_TYPE_NAME(ITypeSymbol type)
        => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
            SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions |
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier));

    private static string GET_CONSTRUCTIBLE_COLLECTION_TYPE_NAME(INamedTypeSymbol type)
    {
        string def = type.OriginalDefinition.ToDisplayString();
        string arg0 = GET_FULL_TYPE_NAME(type.TypeArguments[0]);

        return def switch
        {
            "System.Collections.Generic.List<T>" => $"global::System.Collections.Generic.List<{arg0}>",
            "System.Collections.Generic.HashSet<T>" => $"global::System.Collections.Generic.HashSet<{arg0}>",
            "System.Collections.Generic.Queue<T>" => $"global::System.Collections.Generic.Queue<{arg0}>",
            "System.Collections.Generic.Stack<T>" => $"global::System.Collections.Generic.Stack<{arg0}>",
            "System.Collections.Generic.Dictionary<TKey, TValue>" => $"global::System.Collections.Generic.Dictionary<{arg0}, {GET_FULL_TYPE_NAME(type.TypeArguments[1])}>",
            _ => GET_FULL_TYPE_NAME(type)
        };
    }
}
