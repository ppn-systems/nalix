// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Nalix.Analyzers.Diagnostics;

namespace Nalix.Analyzers.Analyzers;

// NALIX117: byte-wise SequenceEqual on secret-bearing data.
public sealed partial class NalixUsageAnalyzer
{
    private const int MaxSecretOperandDepth = 8;

    private static void AnalyzeSecretComparison(OperationAnalysisContext context, IInvocationOperation invocation, PolicySymbols policy)
    {
        IMethodSymbol target = invocation.TargetMethod;
        INamedTypeSymbol? container = target.ContainingType;

        // MemoryExtensions.SequenceEqual<byte>(Span/ReadOnlySpan, ReadOnlySpan) and Enumerable.SequenceEqual<byte>.
        if ((!SymbolEqualityComparer.Default.Equals(container, policy.MemoryExtensions)
             && !SymbolEqualityComparer.Default.Equals(container, policy.Enumerable))
            || target.TypeArguments.Length != 1
            || target.TypeArguments[0].SpecialType != SpecialType.System_Byte
            || invocation.Arguments.Length < 2)
        {
            return;
        }

        string? name = FindSecretOperandName(invocation.Arguments[0].Value)
            ?? FindSecretOperandName(invocation.Arguments[1].Value);

        if (name is not null)
        {
            Report(context, DiagnosticDescriptors.NonConstantTimeSecretComparison, invocation.Syntax, name);
        }
    }

    /// <summary>
    /// Walks through conversions, <c>AsSpan()</c>/<c>Slice()</c> style calls and indexers to the variable that
    /// holds the bytes, and returns its name when the name reads as secret material.
    /// </summary>
    private static string? FindSecretOperandName(IOperation? operation)
    {
        for (int depth = 0; depth < MaxSecretOperandDepth && operation is not null; depth++)
        {
            string name;
            switch (operation)
            {
                case IConversionOperation conversion:
                    operation = conversion.Operand;
                    continue;
                case IInvocationOperation invocation:
                    operation = invocation.Instance
                        ?? (invocation.Arguments.Length > 0 ? invocation.Arguments[0].Value : null);
                    continue;
                case IImplicitIndexerReferenceOperation implicitIndexer:
                    operation = implicitIndexer.Instance;
                    continue;
                case IPropertyReferenceOperation { Property.IsIndexer: true } indexer:
                    operation = indexer.Instance;
                    continue;
                case IPropertyReferenceOperation property:
                    name = property.Property.Name;
                    break;
                case IFieldReferenceOperation field:
                    name = field.Field.Name;
                    break;
                case ILocalReferenceOperation local:
                    name = local.Local.Name;
                    break;
                case IParameterReferenceOperation parameter:
                    name = parameter.Parameter.Name;
                    break;
                default:
                    return null;
            }

            return IsSecretName(name) ? name : null;
        }

        return null;
    }

    /// <summary>
    /// True when a word of the identifier (split on case changes, underscores and digits) names secret material:
    /// tag, mac, hmac, signature, proof, challenge, secret, token, password, passphrase or credential. A name that
    /// also contains "address"/"addr" is skipped so MAC addresses are not mistaken for message authentication codes.
    /// </summary>
    private static bool IsSecretName(string name)
    {
        bool secret = false;
        bool address = false;
        int i = 0;
        while (i < name.Length)
        {
            while (i < name.Length && !char.IsLetter(name[i]))
            {
                i++;
            }

            if (i >= name.Length)
            {
                break;
            }

            int start = i;
            if (char.IsUpper(name[i]))
            {
                i++;
                if (i < name.Length && char.IsUpper(name[i]))
                {
                    // Acronym such as HMAC; a trailing capital that begins a lowercase run starts the next word.
                    while (i < name.Length && char.IsUpper(name[i]))
                    {
                        i++;
                    }

                    if (i < name.Length && char.IsLower(name[i]))
                    {
                        i--;
                    }
                }
                else
                {
                    while (i < name.Length && char.IsLower(name[i]))
                    {
                        i++;
                    }
                }
            }
            else
            {
                while (i < name.Length && char.IsLower(name[i]))
                {
                    i++;
                }
            }

            int length = i - start;
            if (WordEquals(name, start, length, "address") || WordEquals(name, start, length, "addr"))
            {
                address = true;
            }
            else if (IsSecretWord(name, start, length))
            {
                secret = true;
            }
        }

        return secret && !address;
    }

    private static bool IsSecretWord(string name, int start, int length)
        => WordEquals(name, start, length, "tag")
           || WordEquals(name, start, length, "mac")
           || WordEquals(name, start, length, "hmac")
           || WordEquals(name, start, length, "signature")
           || WordEquals(name, start, length, "proof")
           || WordEquals(name, start, length, "challenge")
           || WordEquals(name, start, length, "secret")
           || WordEquals(name, start, length, "token")
           || WordEquals(name, start, length, "password")
           || WordEquals(name, start, length, "passphrase")
           || WordEquals(name, start, length, "credential");

    private static bool WordEquals(string name, int start, int length, string word)
        => length == word.Length && string.Compare(name, start, word, 0, length, StringComparison.OrdinalIgnoreCase) == 0;
}
