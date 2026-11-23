// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Nalix.Analyzers.Diagnostics;

namespace Nalix.Analyzers.Analyzers;

// ─── NALIX108 / NALIX109 / NALIX110 / NALIX119: IBufferLease ownership ──────
//
// Ownership contract (shared with NALIX039):
//   * A parameter, field or property marked [Borrowed] does NOT own the lease.
//   * A parameter inherits [Borrowed] from the interface member or base method it implements.
//   * Any other parameter of a lease type takes ownership of the argument.
//   * Dispose() releases ownership. Retain() on a borrowed parameter grants one release;
//     on an owned lease it adds an owner (analysis is skipped).
//   * A borrowed lease must not outlive the call: it cannot be returned, stored (nor its
//     Memory), or captured by a lambda queued to Task/ThreadPool without Retain().
//
// This file holds the signature-based checks for [Borrowed] parameters (NALIX110, NALIX119).
// The flow-sensitive checks for owned leases live in NalixUsageAnalyzer.LeaseFlow.cs.
public sealed partial class NalixUsageAnalyzer
{
    private enum LeaseEventKind : byte
    {
        Use,
        Release
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

    private readonly struct RetainInfo
    {
        public RetainInfo(int count, int firstPosition)
        {
            this.Count = count;
            this.FirstPosition = firstPosition;
        }

        public int Count { get; }

        public int FirstPosition { get; }
    }

    private static void AnalyzeLeaseOwnership(OperationBlockAnalysisContext context, SymbolSet symbols)
    {
        INamedTypeSymbol? leaseType = symbols.BufferLeaseType;
        if (leaseType is null)
        {
            return;
        }

        List<LeaseEvent>? borrowedViolations = null;
        Dictionary<ISymbol, RetainInfo>? retains = null;

        if (context.OwningSymbol is IMethodSymbol owningMethod)
        {
            ReportBorrowedContractMismatch(context, owningMethod, leaseType);
        }

        foreach (IOperation root in context.OperationBlocks)
        {
            foreach (IOperation operation in root.DescendantsAndSelf())
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                switch (operation)
                {
                    case ILocalReferenceOperation localReference:
                        ClassifyLeaseReference(context, leaseType, localReference, localReference.Local, localReference.Local.Type, ref borrowedViolations, ref retains);
                        break;
                    case IParameterReferenceOperation parameterReference:
                        ClassifyLeaseReference(context, leaseType, parameterReference, parameterReference.Parameter, parameterReference.Parameter.Type, ref borrowedViolations, ref retains);
                        break;

                    default:
                        break;
                }
            }
        }

        if (borrowedViolations is not null)
        {
            ReportBorrowedViolations(context, borrowedViolations, retains);
        }

        AnalyzeLeaseFlow(context, leaseType, symbols.PoolableInterface);
    }

    private static void ReportBorrowedViolations(
        OperationBlockAnalysisContext context,
        List<LeaseEvent> violations,
        Dictionary<ISymbol, RetainInfo>? retains)
    {
        violations.Sort(static (a, b) => a.Anchor.Syntax.SpanStart.CompareTo(b.Anchor.Syntax.SpanStart));

        HashSet<ISymbol>? reported = null;
        for (int i = 0; i < violations.Count; i++)
        {
            LeaseEvent violation = violations[i];
            RetainInfo retain = default;
            _ = retains?.TryGetValue(violation.Symbol, out retain);
            int retainCount = retain.Count;

            // Retain() only covers releases that come after it. Inside a loop the textual order
            // says nothing, so those are left alone.
            if (retainCount == 0
                || (violation.Anchor.Syntax.SpanStart < retain.FirstPosition && !IsInsideLoop(violation.Anchor)))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.BorrowedBufferLeaseReleased,
                    violation.Anchor.Syntax.GetLocation(),
                    violation.Symbol.Name,
                    violation.Description));
                continue;
            }

            // A single Retain() makes this method the owner of exactly one reference. A second
            // release that always follows the first one gives away the caller's reference.
            if (retainCount != 1
                || (reported is not null && reported.Contains(violation.Symbol))
                || !TryGetUnconditionalStatement(violation.Anchor, out IBlockOperation? block, out int statementIndex))
            {
                continue;
            }

            for (int j = i + 1; j < violations.Count; j++)
            {
                LeaseEvent next = violations[j];
                if (SymbolEqualityComparer.Default.Equals(next.Symbol, violation.Symbol)
                    && IsInLaterStatement(next.Anchor, block!, statementIndex))
                {
                    reported ??= new HashSet<ISymbol>(SymbolEqualityComparer.Default);
                    _ = reported.Add(violation.Symbol);
                    context.ReportDiagnostic(Diagnostic.Create(
                        DiagnosticDescriptors.BorrowedBufferLeaseReleased,
                        next.Anchor.Syntax.GetLocation(),
                        violation.Symbol.Name,
                        "released more than once after a single Retain()"));
                    break;
                }
            }
        }
    }

    // NALIX119: an implementation that declares [Borrowed] while the member it implements takes
    // ownership. Callers that go through the interface hand the lease over and never release it.
    private static void ReportBorrowedContractMismatch(OperationBlockAnalysisContext context, IMethodSymbol method, INamedTypeSymbol leaseType)
    {
        foreach (IParameterSymbol parameter in method.Parameters)
        {
            if (IsLeaseType(parameter.Type, leaseType)
                && IsBorrowed(parameter)
                && TryFindBaseParameter(parameter, borrowed: false, out IParameterSymbol? baseParameter)
                && parameter.Locations.Length > 0)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.BorrowedContractMismatch,
                    parameter.Locations[0],
                    parameter.Name,
                    baseParameter!.ContainingSymbol.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat)));
            }
        }
    }

    private static void ClassifyLeaseReference(
        OperationBlockAnalysisContext context,
        INamedTypeSymbol leaseType,
        IOperation reference,
        ISymbol symbol,
        ITypeSymbol? type,
        ref List<LeaseEvent>? borrowedViolations,
        ref Dictionary<ISymbol, RetainInfo>? retains)
    {
        if (type is null || !IsLeaseType(type, leaseType))
        {
            return;
        }

        bool isBorrowedParameter = symbol is IParameterSymbol parameterSymbol && IsBorrowedParameter(parameterSymbol);

        // References captured by a lambda or local function are not tracked: their
        // execution order relative to the enclosing method is unknown.
        if (!SymbolEqualityComparer.Default.Equals(symbol.ContainingSymbol, GetEnclosingFunction(reference, context.OwningSymbol)))
        {
            if (isBorrowedParameter && GetDeferredLambdaTarget(reference) is { } queuedTo)
            {
                borrowedViolations ??= [];
                borrowedViolations.Add(new LeaseEvent(symbol, LeaseEventKind.Release, reference, $"captured by a lambda queued to '{queuedTo}', which can run after this method returns"));
            }

            return;
        }

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
                    Retain(symbol, isBorrowedParameter, invocation, ref retains);
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
                        Retain(symbol, isBorrowedParameter, whenNotNull, ref retains);
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
                    return;
                }

                if (TakesOwnership(target, argument.Parent, leaseType))
                {
                    kind = LeaseEventKind.Release;
                    description = $"passed to '{GetCalleeName(argument.Parent)}', which takes ownership (parameter '{target.Name}' is not [Borrowed])";
                }

                break;

            case ISimpleAssignmentOperation assignment when ReferenceEquals(assignment.Value, node):
                anchor = assignment;
                if (isBorrowedParameter && GetAssignedMember(assignment.Target) is { } member && !IsBorrowed(member))
                {
                    borrowedViolations ??= [];
                    borrowedViolations.Add(new LeaseEvent(symbol, LeaseEventKind.Release, assignment, $"stored in '{member.Name}', which is not [Borrowed]"));
                }

                break;

            case IReturnOperation returnOperation when isBorrowedParameter && ReferenceEquals(returnOperation.ReturnedValue, node):
                borrowedViolations ??= [];
                borrowedViolations.Add(new LeaseEvent(symbol, LeaseEventKind.Release, returnOperation, "returned, which hands it to a new owner"));
                break;

            case IPropertyReferenceOperation { Property.Name: "Memory" } memory when isBorrowedParameter && ReferenceEquals(memory.Instance, node):
                if (GetStoredMember(memory) is { } memoryTarget)
                {
                    borrowedViolations ??= [];
                    borrowedViolations.Add(new LeaseEvent(symbol, LeaseEventKind.Release, memory, $"exposed through Memory stored in '{memoryTarget.Name}', which outlives the call"));
                }

                break;

            default:
                break;
        }

        if (kind == LeaseEventKind.Release && isBorrowedParameter)
        {
            borrowedViolations ??= [];
            borrowedViolations.Add(new LeaseEvent(symbol, kind, anchor, description));
        }
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

    // A parameter is borrowed when it, or the parameter it implements or overrides, is marked [Borrowed].
    private static bool IsBorrowedParameter(IParameterSymbol parameter)
        => IsBorrowed(parameter) || TryFindBaseParameter(parameter, borrowed: true, out _);

    // Finds the matching parameter of an overridden method or implemented interface member whose
    // borrowed state equals 'borrowed'.
    private static bool TryFindBaseParameter(IParameterSymbol parameter, bool borrowed, out IParameterSymbol? baseParameter)
    {
        baseParameter = null;
        if (parameter.ContainingSymbol is not IMethodSymbol method)
        {
            return false;
        }

        int ordinal = parameter.Ordinal;
        if (method.OverriddenMethod is { } overridden
            && overridden.Parameters.Length > ordinal
            && IsBorrowedParameter(overridden.Parameters[ordinal]) == borrowed)
        {
            baseParameter = overridden.Parameters[ordinal];
            return true;
        }

        if (method.ContainingType is not { } type || method.IsStatic)
        {
            return false;
        }

        foreach (INamedTypeSymbol @interface in type.AllInterfaces)
        {
            foreach (ISymbol member in @interface.GetMembers())
            {
                if (member is IMethodSymbol interfaceMethod
                    && interfaceMethod.Parameters.Length > ordinal
                    && SymbolEqualityComparer.Default.Equals(type.FindImplementationForInterfaceMember(interfaceMethod), method)
                    && IsBorrowedParameter(interfaceMethod.Parameters[ordinal]) == borrowed)
                {
                    baseParameter = interfaceMethod.Parameters[ordinal];
                    return true;
                }
            }
        }

        return false;
    }

    private static void Retain(ISymbol symbol, bool isBorrowedParameter, IOperation call, ref Dictionary<ISymbol, RetainInfo>? retains)
    {
        if (!isBorrowedParameter)
        {
            return;
        }

        retains ??= new Dictionary<ISymbol, RetainInfo>(SymbolEqualityComparer.Default);
        int position = call.Syntax.SpanStart;
        retains[symbol] = retains.TryGetValue(symbol, out RetainInfo info)
            ? new RetainInfo(info.Count + 1, Math.Min(info.FirstPosition, position))
            : new RetainInfo(1, position);
    }

    private static bool IsInsideLoop(IOperation operation)
    {
        for (IOperation? current = operation.Parent; current is not null; current = current.Parent)
        {
            if (current is ILoopOperation)
            {
                return true;
            }
        }

        return false;
    }

    // The field or property a value is assigned to, looking through implicit conversions.
    private static ISymbol? GetStoredMember(IOperation value)
    {
        IOperation node = value;
        while (node.Parent is IConversionOperation { IsImplicit: true } conversion)
        {
            node = conversion;
        }

        return node.Parent is ISimpleAssignmentOperation assignment
               && ReferenceEquals(assignment.Value, node)
               && GetAssignedMember(assignment.Target) is { } member
               && !IsBorrowed(member)
            ? member
            : null;
    }

    // "Task.Run" when the reference sits in a lambda passed to Task, TaskFactory or ThreadPool,
    // which run it after the borrowing method may have returned.
    private static string? GetDeferredLambdaTarget(IOperation reference)
    {
        for (IOperation? current = reference.Parent; current is not null; current = current.Parent)
        {
            if (current is not IAnonymousFunctionOperation)
            {
                continue;
            }

            IOperation? parent = current.Parent;
            while (parent is IDelegateCreationOperation or IConversionOperation)
            {
                parent = parent.Parent;
            }

            return parent is IArgumentOperation { Parent: IInvocationOperation invocation }
                   && invocation.TargetMethod.ContainingType?.Name is "Task" or "TaskFactory" or "ThreadPool"
                ? $"{invocation.TargetMethod.ContainingType.Name}.{invocation.TargetMethod.Name}"
                : null;
        }

        return null;
    }

    private static bool IsParameterlessDispose(IInvocationOperation invocation)
        => invocation.TargetMethod.Name == "Dispose" && invocation.Arguments.Length == 0;

    // A parameter takes ownership when it is a lease-typed, non-[Borrowed] parameter of a
    // regular method or constructor. Delegate/event invocations and generic parameters
    // (e.g. Tuple.Create<T>) cannot carry [Borrowed], so they are treated as borrowing.
    private static bool TakesOwnership(IParameterSymbol target, IOperation? call, INamedTypeSymbol leaseType)
    {
        if (IsBorrowedParameter(target)
            || target.OriginalDefinition.Type is ITypeParameterSymbol
            || !IsLeaseType(target.Type, leaseType)
            || IsConditionalTransfer(call))
        {
            return false;
        }

        return call is not IInvocationOperation { TargetMethod.MethodKind: MethodKind.DelegateInvoke };
    }

    // A method that returns bool ('TryEnqueue(lease)', 'Push(lease)') transfers ownership only on
    // success; the caller disposes on failure, so it is not treated as an unconditional release.
    private static bool IsConditionalTransfer(IOperation? call)
        => call is IInvocationOperation invocation
           && invocation.TargetMethod.ReturnType.SpecialType == SpecialType.System_Boolean;

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

                default:
                    break;
            }
        }

        return owningSymbol;
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
