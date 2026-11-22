// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Nalix.Analyzers.Diagnostics;

namespace Nalix.Analyzers.Analyzers;

// NALIX118: validation attributes on ConfigurationLoader properties.
//
// ConfigurationGenerator reads these attributes and writes the validation code, so a mismatch shows up as
// generated code that does not compile, a runtime failure while the configuration is validated, or (for
// [AllowedEnum] and [Required]) a check that is silently left out.
public sealed partial class NalixUsageAnalyzer
{
    private const string ValidationNamespace = "Nalix.Abstractions.Validation";
    private const string DataAnnotationsNamespace = "System.ComponentModel.DataAnnotations";

    private static void AnalyzeValidationAttributes(SymbolAnalysisContext context, INamedTypeSymbol typeSymbol, IPropertySymbol property)
    {
        foreach (AttributeData attribute in property.GetAttributes())
        {
            INamedTypeSymbol? attributeClass = attribute.AttributeClass;
            if (attributeClass is null)
            {
                continue;
            }

            string? problem = attributeClass.Name switch
            {
                "ValueRangeAttribute" when IsInNamespace(attributeClass, ValidationNamespace)
                    => CheckValueRange(attribute, property.Type),
                "DurationRangeAttribute" when IsInNamespace(attributeClass, ValidationNamespace)
                    => CheckDurationRange(attribute, property.Type),
                "LengthAttribute" when IsInNamespace(attributeClass, ValidationNamespace)
                    => CheckLength(property.Type),
                "AllowedEnumAttribute" when IsInNamespace(attributeClass, ValidationNamespace)
                    => property.Type.TypeKind == TypeKind.Enum
                        ? null
                        : $"has no effect: only a property whose type is an enum is checked, not '{DisplayType(property.Type)}'",
                "RequiredAttribute" when IsInNamespace(attributeClass, ValidationNamespace) || IsInNamespace(attributeClass, DataAnnotationsNamespace)
                    => property.Type.IsValueType
                        ? $"has no effect on the value type '{DisplayType(property.Type)}', which can never be null"
                        : null,
                _ => null,
            };

            if (problem is null)
            {
                continue;
            }

            string attributeName = attributeClass.Name.Substring(0, attributeClass.Name.Length - "Attribute".Length);
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.ConfigurationValidationAttributeInvalid,
                GetAttributeLocation(attribute, property, context.CancellationToken),
                attributeName,
                typeSymbol.Name + "." + property.Name,
                problem));
        }
    }

    private static string? CheckValueRange(AttributeData attribute, ITypeSymbol propertyType)
    {
        if (!IsNumericType(propertyType))
        {
            return $"needs a numeric property, but '{DisplayType(propertyType)}' is not; the generated range check does not compile";
        }

        ImmutableArray<TypedConstant> arguments = attribute.ConstructorArguments;
        if (arguments.Length != 2)
        {
            return null;
        }

        object? min = arguments[0].Value;
        object? max = arguments[1].Value;
        bool inverted = min is long minLong && max is long maxLong
            ? minLong > maxLong
            : min is IConvertible minValue && max is IConvertible maxValue
                && minValue.ToDouble(CultureInfo.InvariantCulture) > maxValue.ToDouble(CultureInfo.InvariantCulture);

        return inverted
            ? $"has a minimum ({Format(min)}) greater than its maximum ({Format(max)}), so every value is rejected"
            : null;
    }

    private static string? CheckDurationRange(AttributeData attribute, ITypeSymbol propertyType)
    {
        if (propertyType.Name != "TimeSpan" || !IsInNamespace(propertyType, "System"))
        {
            return $"needs a TimeSpan property, but '{DisplayType(propertyType)}' is not one; the generated range check does not compile";
        }

        ImmutableArray<TypedConstant> arguments = attribute.ConstructorArguments;
        if (arguments.Length != 2)
        {
            return null;
        }

        string? minText = arguments[0].Value as string;
        string? maxText = arguments[1].Value as string;
        if (!TimeSpan.TryParse(minText, CultureInfo.InvariantCulture, out TimeSpan min))
        {
            return $"has a minimum '{minText}' that is not a valid TimeSpan; validating the configuration throws a FormatException";
        }

        if (!TimeSpan.TryParse(maxText, CultureInfo.InvariantCulture, out TimeSpan max))
        {
            return $"has a maximum '{maxText}' that is not a valid TimeSpan; validating the configuration throws a FormatException";
        }

        return min > max
            ? $"has a minimum ({minText}) greater than its maximum ({maxText}), so every value is rejected"
            : null;
    }

    private static string? CheckLength(ITypeSymbol propertyType)
        => propertyType.SpecialType == SpecialType.System_String
            ? null
            : $"needs a string property, but '{DisplayType(propertyType)}' is not one; the generated length check does not compile";

    private static bool IsNumericType(ITypeSymbol type) => type.SpecialType is
        SpecialType.System_SByte or SpecialType.System_Byte or
        SpecialType.System_Int16 or SpecialType.System_UInt16 or
        SpecialType.System_Int32 or SpecialType.System_UInt32 or
        SpecialType.System_Int64 or SpecialType.System_UInt64 or
        SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_Decimal;

    private static bool IsInNamespace(ISymbol symbol, string expected)
    {
        INamespaceSymbol? ns = symbol.ContainingNamespace;
        if (ns is null || ns.IsGlobalNamespace)
        {
            return false;
        }

        // Compare segment by segment, innermost first, so no namespace string is built on the common path.
        string[] segments = expected.Split('.');
        for (int i = segments.Length - 1; i >= 0; i--)
        {
            if (ns is null || ns.IsGlobalNamespace || ns.Name != segments[i])
            {
                return false;
            }

            ns = ns.ContainingNamespace;
        }

        return ns is null || ns.IsGlobalNamespace;
    }

    private static string DisplayType(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

    private static string Format(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "?";
}
