// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;
using Nalix.Analyzers.Diagnostics;

namespace Nalix.Analyzers.Analyzers;

// The transfer functions of the lease flow analysis: what each operation does to the state of a
// lease, and which diagnostics it raises. See NalixUsageAnalyzer.LeaseFlow.cs for the model.
public sealed partial class NalixUsageAnalyzer
{
    private sealed partial class LeaseFlow
    {
        // ── Transfer ────────────────────────────────────────────────────────

        private ISymbol? TrackedSymbol(IOperation? operation)
        {
            while (operation is IConversionOperation conversion)
            {
                operation = conversion.Operand;
            }

            ISymbol? symbol = operation switch
            {
                ILocalReferenceOperation local => local.Local,
                IParameterReferenceOperation parameter => parameter.Parameter,
                IFlowCaptureReferenceOperation capture when _captures is not null && _captures.TryGetValue(capture.Id, out ISymbol? captured) => captured,
                _ => null
            };

            return this.GroupOf(symbol) >= 0 ? symbol : null;
        }

        private void Visit(IOperation operation, byte[] state)
        {
            foreach (IOperation child in operation.ChildOperations)
            {
                this.Visit(child, state);
            }

            switch (operation)
            {
                case ILocalReferenceOperation or IParameterReferenceOperation or IFlowCaptureReferenceOperation:
                    this.VisitReference(operation, state);
                    break;

                case IInvocationOperation invocation:
                    this.VisitInvocation(invocation, state);
                    break;

                case IObjectCreationOperation creation:
                    this.VisitArguments(creation.Arguments, creation, state);
                    break;

                case ISimpleAssignmentOperation assignment:
                    this.VisitAssignment(assignment, state);
                    break;

                default:
                    break;
            }
        }

        private void VisitReference(IOperation reference, byte[] state)
        {
            if (this.TrackedSymbol(reference) is not { } symbol)
            {
                return;
            }

            int group = this.GroupOf(symbol);
            IOperation node = reference;
            IOperation? parent = node.Parent;
            while (parent is IConversionOperation)
            {
                node = parent;
                parent = parent.Parent;
            }

            switch (parent)
            {
                // Handled when the parent itself is visited, after all of its operands.
                case IArgumentOperation:
                case ISimpleAssignmentOperation:
                case IInvocationOperation invocation when ReferenceEquals(invocation.Instance, node)
                                                          && (IsParameterlessDispose(invocation) || invocation.TargetMethod.Name == "Retain"):
                    return;

                // Neither reads the buffer nor hands the lease on.
                case IFlowCaptureOperation:
                case IIsNullOperation:
                case IIsPatternOperation:
                case IBinaryOperation { OperatorKind: BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals }:
                case IExpressionStatementOperation:
                    return;

                // The value of a return statement: checked here, handed to the caller by the block.
                case null:
                    this.Use(symbol, group, reference, state);
                    return;

                case IInvocationOperation member when ReferenceEquals(member.Instance, node):
                case IPropertyReferenceOperation:
                case IFieldReferenceOperation:
                case IEventReferenceOperation:
                    this.Use(symbol, group, reference, state);
                    return;

                default:
                    // Anything else (a tuple, an array, an await, a method group...) may keep the lease.
                    this.Use(symbol, group, reference, state);
                    state[group] = LeaseEscaped;
                    return;
            }
        }

        private void VisitInvocation(IInvocationOperation invocation, byte[] state)
        {
            if (this.TrackedSymbol(invocation.Instance) is { } receiver)
            {
                int group = this.GroupOf(receiver);
                if (invocation.TargetMethod.Name == "Retain")
                {
                    // A second owner exists from here on; who releases which reference is not followed.
                    state[group] = LeaseEscaped;
                }
                else if (IsParameterlessDispose(invocation))
                {
                    this.Release(receiver, group, invocation, "disposed", state);
                }
            }

            if (invocation.TargetMethod.Name != "ReferenceEquals")
            {
                this.VisitArguments(invocation.Arguments, invocation, state);
            }
        }

        private void VisitArguments(System.Collections.Immutable.ImmutableArray<IArgumentOperation> arguments, IOperation call, byte[] state)
        {
            foreach (IArgumentOperation argument in arguments)
            {
                if (this.TrackedSymbol(argument.Value) is not { } symbol)
                {
                    continue;
                }

                int group = this.GroupOf(symbol);
                IParameterSymbol? target = argument.Parameter;
                if (target is null || target.RefKind is RefKind.Ref or RefKind.Out)
                {
                    state[group] = LeaseEscaped;
                }
                else if (TakesOwnership(target, call, _leaseType))
                {
                    this.Release(symbol, group, argument, $"passed to '{GetCalleeName(call)}', which takes ownership (parameter '{target.Name}' is not [Borrowed])", state);
                }
                else
                {
                    this.Use(symbol, group, argument, state);

                    // A [Borrowed] parameter gives the lease back, and a guard such as ThrowIfNull only
                    // looks at it. A delegate, a generic parameter, an 'object' or a method that returns bool may keep it.
                    if (!IsBorrowedParameter(target) && !IsGuardCall(call))
                    {
                        state[group] = LeaseEscaped;
                    }
                }
            }
        }

        private static bool IsGuardCall(IOperation call)
            => call is IInvocationOperation invocation
               && (invocation.TargetMethod.Name.StartsWith("Throw", System.StringComparison.Ordinal)
                   || invocation.TargetMethod.ContainingType?.Name == "Debug");

        private void VisitAssignment(ISimpleAssignmentOperation assignment, byte[] state)
        {
            ISymbol? target = this.TrackedSymbol(assignment.Target);
            ISymbol? source = this.TrackedSymbol(assignment.Value);
            int targetGroup = this.GroupOf(target);
            int sourceGroup = this.GroupOf(source);

            if (source is not null)
            {
                this.Use(source, sourceGroup, assignment.Value, state);
                if (target is null)
                {
                    // Stored in a field, a property, an element or an out parameter.
                    state[sourceGroup] = LeaseEscaped;
                    return;
                }
            }

            if (target is null || targetGroup == sourceGroup)
            {
                return;
            }

            if (source is not null)
            {
                // Another name for a lease that is followed separately.
                state[targetGroup] = LeaseEscaped;
                return;
            }

            if (IsNullValue(assignment.Value))
            {
                this.ReportOverwrite(target, targetGroup, assignment, state);
                state[targetGroup] = LeaseNull;
                return;
            }

            IOperation value = assignment.Value;
            while (value is IConversionOperation conversion)
            {
                value = conversion.Operand;
            }

            if (value is IAwaitOperation awaited)
            {
                value = awaited.Operation;
            }

            if (value is IInvocationOperation or IObjectCreationOperation)
            {
                this.ReportOverwrite(target, targetGroup, assignment, state);
                state[targetGroup] = LeaseOwned;
            }
            else
            {
                // A field, a property, an element: whoever holds it owns it.
                state[targetGroup] = LeaseEscaped;
            }
        }

        private static bool IsNullValue(IOperation value)
        {
            while (value is IConversionOperation conversion)
            {
                value = conversion.Operand;
            }

            return value is IDefaultValueOperation || (value.ConstantValue.HasValue && value.ConstantValue.Value is null);
        }

        private bool TryGetNullTest(IOperation? condition, out int group, out bool nullWhenTrue)
        {
            group = -1;
            nullWhenTrue = true;

            while (true)
            {
                switch (condition)
                {
                    case IConversionOperation conversion:
                        condition = conversion.Operand;
                        continue;

                    case IUnaryOperation { OperatorKind: UnaryOperatorKind.Not } not:
                        nullWhenTrue = !nullWhenTrue;
                        condition = not.Operand;
                        continue;

                    case IIsNullOperation isNull:
                        group = this.GroupOf(this.TrackedSymbol(isNull.Operand));
                        return group >= 0;

                    case IBinaryOperation { OperatorKind: BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals } binary:
                        IOperation? other = IsNullValue(binary.RightOperand) ? binary.LeftOperand
                            : IsNullValue(binary.LeftOperand) ? binary.RightOperand
                            : null;
                        if (binary.OperatorKind == BinaryOperatorKind.NotEquals)
                        {
                            nullWhenTrue = !nullWhenTrue;
                        }

                        group = this.GroupOf(this.TrackedSymbol(other));
                        return group >= 0;

                    case IIsPatternOperation isPattern:
                        IPatternOperation pattern = isPattern.Pattern;
                        if (pattern is INegatedPatternOperation negated)
                        {
                            nullWhenTrue = !nullWhenTrue;
                            pattern = negated.Pattern;
                        }

                        if (pattern is not IConstantPatternOperation constant || !IsNullValue(constant.Value))
                        {
                            return false;
                        }

                        group = this.GroupOf(this.TrackedSymbol(isPattern.Value));
                        return group >= 0;

                    default:
                        return false;
                }
            }
        }

        // ── Events ──────────────────────────────────────────────────────────

        private bool IsFollowed(int group, byte current)
            => !_untracked[group] && (current & LeaseEscaped) == 0;

        private void Use(ISymbol symbol, int group, IOperation anchor, byte[] state)
        {
            byte current = state[group];
            if (!_usingResource[group] && this.IsFollowed(group, current) && (current & LeaseReleased) != 0)
            {
                this.Report(DiagnosticDescriptors.BufferLeaseUsedAfterRelease, anchor, symbol.Name, this.Describe(group, current));
            }
        }

        private void Release(ISymbol symbol, int group, IOperation anchor, string description, byte[] state)
        {
            if (_usingResource[group])
            {
                // The automatic Dispose at the end of the scope is the lowered, implicit one.
                if (!anchor.IsImplicit)
                {
                    this.Report(
                        DiagnosticDescriptors.BufferLeaseReleasedTwice,
                        anchor,
                        symbol.Name,
                        "declared with 'using' and is disposed automatically at the end of its scope");
                }

                return;
            }

            byte current = state[group];
            if (!this.IsFollowed(group, current))
            {
                return;
            }

            if ((current & LeaseReleased) != 0)
            {
                this.Report(DiagnosticDescriptors.BufferLeaseReleasedTwice, anchor, symbol.Name, this.Describe(group, current));
            }
            else if (current == LeaseBorrowed && symbol is ILocalSymbol)
            {
                // The [Borrowed] parameter itself is reported by the signature-based check.
                this.Report(DiagnosticDescriptors.BorrowedBufferLeaseReleased, anchor, symbol.Name, description + " through a local that aliases the borrowed parameter");
            }

            if ((current & LeaseBorrowed) != 0)
            {
                return;
            }

            if (current != LeaseNull)
            {
                _releaseDescription[group] ??= description;
                state[group] = LeaseReleased;
            }
        }

        private string Describe(int group, byte current)
        {
            string description = _releaseDescription[group] ?? "released";
            return (current & (LeaseOwned | LeaseNull)) != 0 ? description + " on another path" : description;
        }

        private void ReportOverwrite(ISymbol target, int group, IOperation assignment, byte[] state)
        {
            byte current = state[group];
            if (_leakTracked[group] && this.IsFollowed(group, current) && (current & (LeaseOwned | LeaseReleased)) == LeaseOwned)
            {
                this.Report(DiagnosticDescriptors.PotentialBufferLeaseLeak, assignment, target.Name, null);
            }
        }

        private void ReportLeaks(byte[] exit)
        {
            for (int i = 0; i < _groupSymbol.Count; i++)
            {
                if (this.Find(i) != i || !_leakTracked[i] || !this.IsFollowed(i, exit[i]) || (exit[i] & (LeaseOwned | LeaseReleased)) != LeaseOwned)
                {
                    continue;
                }

                ISymbol symbol = _groupSymbol[i];
                if (symbol.Locations.Length > 0 && _reported!.Add(((long)symbol.Locations[0].SourceSpan.Start << 3) | 7))
                {
                    _context.ReportDiagnostic(Diagnostic.Create(
                        DiagnosticDescriptors.PotentialBufferLeaseLeak,
                        symbol.Locations[0],
                        symbol.Name));
                }
            }
        }

        private void Report(DiagnosticDescriptor descriptor, IOperation anchor, string name, string? description)
        {
            if (!_report)
            {
                return;
            }

            long rule = descriptor.Id switch
            {
                "NALIX108" => 1,
                "NALIX109" => 2,
                "NALIX110" => 3,
                _ => 4
            };

            if (!_reported!.Add(((long)anchor.Syntax.SpanStart << 3) | rule))
            {
                return;
            }

            _context.ReportDiagnostic(description is null
                ? Diagnostic.Create(descriptor, anchor.Syntax.GetLocation(), name)
                : Diagnostic.Create(descriptor, anchor.Syntax.GetLocation(), name, description));
        }
    }
}
