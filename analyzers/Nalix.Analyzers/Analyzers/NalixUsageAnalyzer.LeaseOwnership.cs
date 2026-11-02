// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Nalix.Analyzers.Diagnostics;

namespace Nalix.Analyzers.Analyzers;

// ─── NALIX108 / NALIX109 / NALIX110: IBufferLease ownership ─────────────────
//
// Ownership contract (shared with NALIX039):
//   * A parameter, field or property marked [Borrowed] does NOT own the lease.
//   * Any other parameter of a lease type takes ownership of the argument.
//   * Dispose() releases ownership. Retain() adds an owner (analysis is skipped).
//
// The analysis is intraprocedural; cross-method correctness comes from the
// [Borrowed] contract on signatures. To avoid false positives it only reports
// when the first release runs unconditionally and the second event sits in a
// later statement of the same block.
public sealed partial class NalixUsageAnalyzer
{
    private enum LeaseEventKind : byte
    {
        Use,
        Release,
        Reassign
    }

    private readonly struct LeaseEvent
    {
        public LeaseEvent(ISymbol symbol, LeaseEventKind kind, IOperation anchor, string description)
        {
            this.Symbol = symbol;
            this.Kind = kind;
            this.Anchor = anchor;
            this.Description = description;
        }

        public ISymbol Symbol { get; }

        public LeaseEventKind Kind { get; }

        public IOperation Anchor { get; }

        public string Description { get; }
    }

    private static void AnalyzeLeaseOwnership(OperationBlockAnalysisContext context, SymbolSet symbols)
    {
        INamedTypeSymbol? leaseType = symbols.BufferLeaseType;
        if (leaseType is null)
        {
            return;
        }

        List<LeaseEvent>? events = null;
        List<LeaseEvent>? borrowedViolations = null;
        HashSet<ISymbol>? excluded = null;
        HashSet<ISymbol>? usingLocals = null;

        foreach (IOperation root in context.OperationBlocks)
        {
            foreach (IOperation operation in root.DescendantsAndSelf())
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                switch (operation)
                {
                    case IUsingDeclarationOperation usingDeclaration:
                        CollectUsingLocals(usingDeclaration.DeclarationGroup, ref usingLocals);
                        break;
                    case IUsingOperation { Resources: IVariableDeclarationGroupOperation group }:
                        CollectUsingLocals(group, ref usingLocals);
                        break;
                    case ILocalReferenceOperation localReference:
                        ClassifyLeaseReference(context, leaseType, localReference, localReference.Local, localReference.Local.Type, ref events, ref borrowedViolations, ref excluded);
                        break;
                    case IParameterReferenceOperation parameterReference:
                        ClassifyLeaseReference(context, leaseType, parameterReference, parameterReference.Parameter, parameterReference.Parameter.Type, ref events, ref borrowedViolations, ref excluded);
                        break;
                }
            }
        }

        if (borrowedViolations is not null)
        {
            foreach (LeaseEvent violation in borrowedViolations)
            {
                // Retain() makes this method an additional owner, so releasing is allowed.
                if (excluded is null || !excluded.Contains(violation.Symbol))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        DiagnosticDescriptors.BorrowedBufferLeaseReleased,
                        violation.Anchor.Syntax.GetLocation(),
                        violation.Symbol.Name,
                        violation.Description));
                }
            }
        }

        if (events is null)
        {
            return;
        }

        events.Sort(static (a, b) => a.Anchor.Syntax.SpanStart.CompareTo(b.Anchor.Syntax.SpanStart));

        HashSet<ISymbol> reported = new(SymbolEqualityComparer.Default);
        for (int i = 0; i < events.Count; i++)
        {
            LeaseEvent first = events[i];
            if (first.Kind != LeaseEventKind.Release
                || reported.Contains(first.Symbol)
                || (excluded is not null && excluded.Contains(first.Symbol)))
            {
                continue;
            }

            // A lease declared with 'using' is disposed automatically at scope end,
            // so any explicit release is a second release.
            if (usingLocals is not null && usingLocals.Contains(first.Symbol))
            {
                _ = reported.Add(first.Symbol);
                context.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.BufferLeaseReleasedTwice,
                    first.Anchor.Syntax.GetLocation(),
                    first.Symbol.Name,
                    "declared with 'using' and is disposed automatically at the end of its scope"));
                continue;
            }

            if (!TryGetUnconditionalStatement(first.Anchor, out IBlockOperation? block, out int statementIndex))
            {
                continue;
            }

            for (int j = i + 1; j < events.Count; j++)
            {
                LeaseEvent next = events[j];
                if (!SymbolEqualityComparer.Default.Equals(next.Symbol, first.Symbol))
                {
                    continue;
                }

                if (next.Kind == LeaseEventKind.Reassign)
                {
                    break;
                }

                if (!IsInLaterStatement(next.Anchor, block!, statementIndex))
                {
                    continue;
                }

                _ = reported.Add(first.Symbol);
                context.ReportDiagnostic(Diagnostic.Create(
                    next.Kind == LeaseEventKind.Release
                        ? DiagnosticDescriptors.BufferLeaseReleasedTwice
                        : DiagnosticDescriptors.BufferLeaseUsedAfterRelease,
                    next.Anchor.Syntax.GetLocation(),
                    first.Symbol.Name,
                    first.Description));
                break;
            }
        }
    }

    private static void ClassifyLeaseReference(
        OperationBlockAnalysisContext context,
        INamedTypeSymbol leaseType,
        IOperation reference,
        ISymbol symbol,
        ITypeSymbol? type,
        ref List<LeaseEvent>? events,
        ref List<LeaseEvent>? borrowedViolations,
        ref HashSet<ISymbol>? excluded)
    {
        if (type is null || !IsLeaseType(type, leaseType))
        {
            return;
        }

        // References captured by a lambda or local function are not tracked: their
        // execution order relative to the enclosing method is unknown.
        if (!SymbolEqualityComparer.Default.Equals(symbol.ContainingSymbol, GetEnclosingFunction(reference, context.OwningSymbol)))
        {
            Exclude(symbol, ref excluded);
            return;
        }

        bool isBorrowedParameter = symbol is IParameterSymbol parameterSymbol && IsBorrowed(parameterSymbol);

        IOperation node = reference;
        IOperation? parent = node.Parent;
        while (parent is IConversionOperation { IsImplicit: true })
        {
            node = parent;
            parent = parent.Parent;
        }

        LeaseEventKind kind = LeaseEventKind.Use;
        IOperation anchor = node;
        string description = string.Empty;

        switch (parent)
        {
            case IInvocationOperation invocation when ReferenceEquals(invocation.Instance, node):
                if (invocation.TargetMethod.Name == "Retain")
                {
                    Exclude(symbol, ref excluded);
                    return;
                }

                anchor = invocation;
                if (IsParameterlessDispose(invocation))
                {
                    kind = LeaseEventKind.Release;
                    description = "disposed";
                }

                break;

            case IConditionalAccessOperation conditionalAccess when ReferenceEquals(conditionalAccess.Operation, node):
                anchor = conditionalAccess;
                if (conditionalAccess.WhenNotNull is IInvocationOperation whenNotNull)
                {
                    if (whenNotNull.TargetMethod.Name == "Retain")
                    {
                        Exclude(symbol, ref excluded);
                        return;
                    }

                    if (IsParameterlessDispose(whenNotNull))
                    {
                        kind = LeaseEventKind.Release;
                        description = "disposed";
                    }
                }

                break;

            case IArgumentOperation argument:
                anchor = argument;
                IParameterSymbol? target = argument.Parameter;
                if (target is null)
                {
                    break;
                }

                if (target.RefKind is RefKind.Ref or RefKind.Out)
                {
                    Exclude(symbol, ref excluded);
                    return;
                }

                if (TakesOwnership(target, argument.Parent, leaseType))
                {
                    kind = LeaseEventKind.Release;
                    description = $"passed to '{GetCalleeName(argument.Parent)}', which takes ownership (parameter '{target.Name}' is not [Borrowed])";
                }

                break;

            case ISimpleAssignmentOperation assignment when ReferenceEquals(assignment.Target, node):
                kind = LeaseEventKind.Reassign;
                anchor = assignment;
                break;

            case ISimpleAssignmentOperation assignment when ReferenceEquals(assignment.Value, node):
                anchor = assignment;
                if (isBorrowedParameter && GetAssignedMember(assignment.Target) is { } member && !IsBorrowed(member))
                {
                    borrowedViolations ??= [];
                    borrowedViolations.Add(new LeaseEvent(symbol, LeaseEventKind.Release, assignment, $"stored in '{member.Name}', which is not [Borrowed]"));
                }

                break;
        }

        if (kind == LeaseEventKind.Release && isBorrowedParameter)
        {
            borrowedViolations ??= [];
            borrowedViolations.Add(new LeaseEvent(symbol, kind, anchor, description));
            kind = LeaseEventKind.Use;
        }

        events ??= [];
        events.Add(new LeaseEvent(symbol, kind, anchor, description));
    }

    private static bool IsLeaseType(ITypeSymbol type, INamedTypeSymbol leaseType)
    {
        if (SymbolEqualityComparer.Default.Equals(type, leaseType))
        {
            return true;
        }

        foreach (INamedTypeSymbol @interface in type.AllInterfaces)
        {
            if (SymbolEqualityComparer.Default.Equals(@interface, leaseType))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsBorrowed(ISymbol symbol)
    {
        foreach (AttributeData attribute in symbol.GetAttributes())
        {
            if (attribute.AttributeClass?.Name is "BorrowedAttribute" or "NoDisposeAttribute")
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsParameterlessDispose(IInvocationOperation invocation)
        => invocation.TargetMethod.Name == "Dispose" && invocation.Arguments.Length == 0;

    // A parameter takes ownership when it is a lease-typed, non-[Borrowed] parameter of a
    // regular method or constructor. Delegate/event invocations and generic parameters
    // (e.g. Tuple.Create<T>) cannot carry [Borrowed], so they are treated as borrowing.
    private static bool TakesOwnership(IParameterSymbol target, IOperation? call, INamedTypeSymbol leaseType)
    {
        if (IsBorrowed(target)
            || target.OriginalDefinition.Type is ITypeParameterSymbol
            || !IsLeaseType(target.Type, leaseType)
            || IsConditionalTransfer(call))
        {
            return false;
        }

        return call is not IInvocationOperation { TargetMethod.MethodKind: MethodKind.DelegateInvoke };
    }

    // 'bool TryXxx(lease)' transfers ownership only on success; the caller typically
    // disposes on failure, so it is not treated as an unconditional release.
    private static bool IsConditionalTransfer(IOperation? call)
        => call is IInvocationOperation invocation
           && invocation.TargetMethod.ReturnType.SpecialType == SpecialType.System_Boolean
           && invocation.TargetMethod.Name.StartsWith("Try", StringComparison.Ordinal);

    private static string GetCalleeName(IOperation? call) => call switch
    {
        IInvocationOperation invocation => invocation.TargetMethod.Name,
        IObjectCreationOperation creation => creation.Type?.Name ?? "constructor",
        _ => "method"
    };

    private static ISymbol? GetAssignedMember(IOperation target) => target switch
    {
        IFieldReferenceOperation field => field.Field,
        IPropertyReferenceOperation property => property.Property,
        _ => null
    };

    private static ISymbol GetEnclosingFunction(IOperation operation, ISymbol owningSymbol)
    {
        for (IOperation? current = operation.Parent; current is not null; current = current.Parent)
        {
            switch (current)
            {
                case IAnonymousFunctionOperation lambda:
                    return lambda.Symbol;
                case ILocalFunctionOperation localFunction:
                    return localFunction.Symbol;
            }
        }

        return owningSymbol;
    }

    private static void Exclude(ISymbol symbol, ref HashSet<ISymbol>? excluded)
    {
        excluded ??= new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        _ = excluded.Add(symbol);
    }

    private static void CollectUsingLocals(IVariableDeclarationGroupOperation group, ref HashSet<ISymbol>? usingLocals)
    {
        foreach (IVariableDeclarationOperation declaration in group.Declarations)
        {
            foreach (IVariableDeclaratorOperation declarator in declaration.Declarators)
            {
                usingLocals ??= new HashSet<ISymbol>(SymbolEqualityComparer.Default);
                _ = usingLocals.Add(declarator.Symbol);
            }
        }
    }

    // True when 'anchor' always runs as part of one statement of a block, i.e. no
    // branch, loop, try/catch, short-circuit or lambda sits between them.
    private static bool TryGetUnconditionalStatement(IOperation anchor, out IBlockOperation? block, out int statementIndex)
    {
        IOperation current = anchor;
        while (current.Parent is { } parent)
        {
            if (parent is IBlockOperation parentBlock)
            {
                block = parentBlock;
                statementIndex = parentBlock.Operations.IndexOf(current);
                return statementIndex >= 0;
            }

            if (parent is IConditionalOperation
                or ILoopOperation
                or ISwitchOperation
                or ISwitchCaseOperation
                or ISwitchExpressionOperation
                or ICatchClauseOperation
                or IConditionalAccessOperation
                or ICoalesceOperation
                or IAnonymousFunctionOperation
                or ILocalFunctionOperation
                or IBinaryOperation { OperatorKind: BinaryOperatorKind.ConditionalAnd or BinaryOperatorKind.ConditionalOr })
            {
                break;
            }

            current = parent;
        }

        block = null;
        statementIndex = -1;
        return false;
    }

    private static bool IsInLaterStatement(IOperation operation, IBlockOperation block, int statementIndex)
    {
        for (IOperation current = operation; current.Parent is { } parent; current = parent)
        {
            if (ReferenceEquals(parent, block))
            {
                return block.Operations.IndexOf(current) > statementIndex;
            }
        }

        return false;
    }
}
