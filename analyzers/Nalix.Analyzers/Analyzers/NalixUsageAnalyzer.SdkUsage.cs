// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Nalix.Analyzers.Diagnostics;

namespace Nalix.Analyzers.Analyzers;

// NALIX115 / NALIX116: client-side SDK usage, plus the shared invocation dispatcher for the policy rules
// (NALIX117 lives in NalixUsageAnalyzer.SecretComparison.cs).
public sealed partial class NalixUsageAnalyzer
{
    private static void AnalyzePolicyInvocation(OperationAnalysisContext context, SymbolSet symbols, PolicySymbols policy)
    {
        IInvocationOperation invocation = (IInvocationOperation)context.Operation;

        // Cheapest filter first: this runs for every invocation in the compilation.
        switch (invocation.TargetMethod.Name)
        {
            case "On":
            case "OnExact":
            case "SubscribeTemp":
                AnalyzeDiscardedSubscription(context, invocation, policy);
                break;
            case "RequestAsync":
                AnalyzeRetryOnNonIdempotentPacket(context, invocation, symbols, policy);
                break;
            case "SequenceEqual":
                AnalyzeSecretComparison(context, invocation, policy);
                break;
            default:
                break;
        }
    }

    // ─── NALIX115 ────────────────────────────────────────────────────────────

    private static void AnalyzeRetryOnNonIdempotentPacket(
        OperationAnalysisContext context,
        IInvocationOperation invocation,
        SymbolSet symbols,
        PolicySymbols policy)
    {
        IMethodSymbol target = invocation.TargetMethod;
        if (policy.PacketIdempotentAttribute is null
            || symbols.RequestExtensionsType is null
            || !SymbolEqualityComparer.Default.Equals(target.ContainingType, symbols.RequestExtensionsType))
        {
            return;
        }

        IOperation? request = null;
        IOperation? options = null;
        foreach (IArgumentOperation argument in invocation.Arguments)
        {
            // Omitted optional arguments are implicit and carry the parameter default, not a caller choice.
            if (argument.IsImplicit)
            {
                continue;
            }

            switch (argument.Parameter?.Name)
            {
                case "request":
                    request = argument.Value;
                    break;
                case "options":
                    options = argument.Value;
                    break;
                default:
                    break;
            }
        }

        if (request is null
            || options is null
            || IsNullLiteral(options)
            || !TryGetTimeoutAndRetryValues(options, symbols, out _, out int? retryCount)
            || retryCount is not > 0)
        {
            return;
        }

        // [PacketIdempotent] is not inherited, so only the exact static type of the request counts. An interface
        // or abstract type says nothing about the packet that is actually sent, so stay silent.
        if (UnwrapConversions(request).Type is not INamedTypeSymbol { TypeKind: TypeKind.Class or TypeKind.Struct, IsAbstract: false } packetType
            || HasAttributeExact(packetType, policy.PacketIdempotentAttribute))
        {
            return;
        }

        Report(context, DiagnosticDescriptors.RetryOnNonIdempotentPacket, invocation.Syntax, packetType.Name, retryCount.Value);
    }

    // ─── NALIX116 ────────────────────────────────────────────────────────────

    private static void AnalyzeDiscardedSubscription(OperationAnalysisContext context, IInvocationOperation invocation, PolicySymbols policy)
    {
        IMethodSymbol target = invocation.TargetMethod;
        if (target.ReturnType.SpecialType != SpecialType.System_IDisposable)
        {
            return;
        }

        INamedTypeSymbol? container = target.ContainingType;
        if (!SymbolEqualityComparer.Default.Equals(container, policy.TransportSessionSubscriptions)
            && !SymbolEqualityComparer.Default.Equals(container, policy.SubscriptionExtensions))
        {
            return;
        }

        // Only a result that is dropped on the floor is reported. Assigning, returning, passing it on or
        // wrapping it in `using` all keep a handle that can end the subscription.
        IOperation? parent = invocation.Parent;
        bool discarded = parent is IExpressionStatementOperation
            || (parent is ISimpleAssignmentOperation { Target: IDiscardOperation } assignment
                && assignment.Parent is IExpressionStatementOperation);

        if (discarded)
        {
            Report(context, DiagnosticDescriptors.DiscardedSubscription, invocation.Syntax, target.Name);
        }
    }

    private static IOperation UnwrapConversions(IOperation operation)
    {
        while (operation is IConversionOperation conversion)
        {
            operation = conversion.Operand;
        }

        return operation;
    }
}
