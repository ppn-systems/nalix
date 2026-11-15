// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Nalix.Analyzers.Generators.Internal;

internal static class SerializationMember
{
    public static List<ISymbol> Resolve(ITypeSymbol type)
    {
        List<ISymbol> allMembers = new(16);
        ITypeSymbol? currentType = type;

        // Walk inheritance chain (supports FrameBase / PacketBase patterns)
        while (currentType is not null && currentType.SpecialType == SpecialType.None)
        {
            List<ISymbol> levelMembers = new();
            ImmutableArray<ISymbol> members = currentType.GetMembers();

            // First pass: add all members that have explicit serialization attributes
            foreach (ISymbol member in members)
            {
                if (member is not (IPropertySymbol or IFieldSymbol) || member.IsStatic)
                {
                    continue;
                }

                // Ignore rule: [SerializeIgnore] excludes a member, UNLESS the same member also explicitly carries
                // [SerializeOrder]/[SerializeHeader]/[SerializeDynamicSize] (e.g. FrameBase.Header). Attributes inherited
                // from a backing field never override [SerializeIgnore]. Keeping this rule keeps the wire layout stable.
                if (IsIgnored(member) && !HasSerializationAttributeOnSelf(member))
                {
                    continue;
                }

                // A private field that a property wraps is serialized through that property, not directly.
                if (member is IFieldSymbol field && !IsPublicOrInternal(field) && FindWrapper(members, field) is not null)
                {
                    continue;
                }

                if (HasSerializationAttribute(member))
                {
                    levelMembers.Add(member);
                    continue;
                }

                // Unattributed members are included by default when public or internal.
                if (IsPublicOrInternal(member))
                {
                    levelMembers.Add(member);
                }
            }

            allMembers.InsertRange(0, levelMembers);
            currentType = currentType.BaseType;
        }

        // Deduplicate members by name across levels (Derived hides Base)
        // Actually, for serialization we want all levels, but if a member is FOUND in multiple levels
        // (e.g. shadowed), we only want one.
        // But wait! FrameBase members are shared.

        // Separate + order headers first, then payload
        IOrderedEnumerable<ISymbol> headers = allMembers
            .Where(m => GetAttribute(m, KnownNames.SerializeHeaderAttributeName) is not null)
            .OrderBy(m => (int)GetAttribute(m, KnownNames.SerializeHeaderAttributeName)!.ConstructorArguments[0].Value!);

        IEnumerable<ISymbol> payload = allMembers
            .Where(m => GetAttribute(m, KnownNames.SerializeHeaderAttributeName) is null);

        IOrderedEnumerable<ISymbol> orderedPayload = payload
            .OrderBy(m =>
            {
                AttributeData? attr = GetAttribute(m, KnownNames.SerializeOrderAttributeName);
                return attr is not null ? (int)attr.ConstructorArguments[0].Value! : int.MaxValue;
            });

        return [.. headers, .. orderedPayload];
    }

    private static bool IsPublicOrInternal(ISymbol member) =>
        member.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal;

    private static bool IsIgnored(ISymbol member)
    {
        if (HasAttributeNamed(member, KnownNames.SerializeIgnoreAttributeName))
        {
            return true;
        }

        return member is IPropertySymbol prop &&
               GetBackingField(prop) is { } field &&
               HasAttributeNamed(field, KnownNames.SerializeIgnoreAttributeName);
    }

    private static bool HasSerializationAttribute(ISymbol member) =>
        GetAttribute(member, KnownNames.SerializeHeaderAttributeName) is not null ||
        GetAttribute(member, KnownNames.SerializeOrderAttributeName) is not null ||
        GetAttribute(member, KnownNames.SerializeDynamicSizeAttributeName) is not null;

    private static bool HasSerializationAttributeOnSelf(ISymbol member) =>
        FindAttribute(member, KnownNames.SerializeHeaderAttributeName) is not null ||
        FindAttribute(member, KnownNames.SerializeOrderAttributeName) is not null ||
        FindAttribute(member, KnownNames.SerializeDynamicSizeAttributeName) is not null;

    private static bool HasAttributeNamed(ISymbol member, string attrName) =>
        member.GetAttributes().Any(a => a.AttributeClass?.Name == attrName);

    private static AttributeData? GetAttribute(ISymbol member, string attrName)
    {
        AttributeData? attr = FindAttribute(member, attrName);
        if (attr is not null || member is not IPropertySymbol prop)
        {
            return attr;
        }

        // A property inherits the attribute of its own backing field only.
        return GetBackingField(prop) is { } field ? FindAttribute(field, attrName) : null;
    }

    private static AttributeData? FindAttribute(ISymbol member, string attrName) =>
        member.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == attrName);

    /// <summary>
    /// Returns the field that stores <paramref name="prop"/>'s value: the compiler's backing field for an
    /// auto-property, or a manual wrapper's field named <c>_name</c> / <c>_camelName</c>. Never matches by type.
    /// </summary>
    private static IFieldSymbol? GetBackingField(IPropertySymbol prop)
    {
        string name = prop.Name;
        string camel = char.ToLowerInvariant(name[0]) + name.Substring(1);

        foreach (IFieldSymbol field in prop.ContainingType.GetMembers().OfType<IFieldSymbol>())
        {
            if (field.IsStatic)
            {
                continue;
            }

            if (SymbolEqualityComparer.Default.Equals(field.AssociatedSymbol, prop) ||
                field.Name == "_" + name || field.Name == "_" + camel)
            {
                return field;
            }
        }

        return null;
    }

    private static IPropertySymbol? FindWrapper(ImmutableArray<ISymbol> members, IFieldSymbol field)
    {
        foreach (ISymbol member in members)
        {
            if (member is IPropertySymbol prop && SymbolEqualityComparer.Default.Equals(GetBackingField(prop), field))
            {
                return prop;
            }
        }

        return null;
    }
}
