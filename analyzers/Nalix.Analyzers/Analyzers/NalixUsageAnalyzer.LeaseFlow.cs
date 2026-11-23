// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Nalix.Analyzers.Analyzers;

// ─── NALIX039 / NALIX108 / NALIX109 / NALIX110: flow-sensitive lease ownership ──
//
// Runs a forward data-flow analysis over the method's control-flow graph. Every lease-typed
// local and owning parameter has a set of possible states at each point (null, owned, released,
// borrowed, escaped). States are joined where paths meet, so a lease that is released on only one
// of two paths is "possibly released" afterwards, as a moved value is in Rust.
//
//   * Variables that are assigned from one another and share a single source are one alias group:
//     releasing one releases all of them.
//   * A release or use of a possibly released lease is NALIX108 / NALIX109.
//   * A lease that is certainly still owned where the method returns, or where its variable is
//     overwritten, is NALIX039.
//   * Releasing a local that aliases a [Borrowed] parameter is NALIX110.
//
// What the analysis cannot follow (a lease stored in a field, passed by ref, to a delegate or a
// generic parameter, captured by a lambda, retained) makes the lease "escaped": nothing more is
// reported for it. Exception edges are approximated: a catch block starts from every state that
// was reached before a statement of its try block.
public sealed partial class NalixUsageAnalyzer
{
    private const byte LeaseNull = 1;
    private const byte LeaseOwned = 2;
    private const byte LeaseReleased = 4;
    private const byte LeaseBorrowed = 8;
    private const byte LeaseEscaped = 16;

    private const int MaxLeaseFlowBlocks = 2048;
    private const int MaxLeaseFlowContexts = 256;

    private static void AnalyzeLeaseFlow(OperationBlockAnalysisContext context, INamedTypeSymbol leaseType, INamedTypeSymbol? poolableType)
    {
        RunLeaseFlow(context, LeaseFlow.TryCreate(context, leaseType, poolableType: null));

        // The same analysis for any other pooled object: Return() is its only release (NALIX121).
        if (poolableType is not null)
        {
            RunLeaseFlow(context, LeaseFlow.TryCreate(context, leaseType, poolableType));
        }
    }

    private static void RunLeaseFlow(OperationBlockAnalysisContext context, LeaseFlow? flow)
    {
        if (flow is null)
        {
            return;
        }

        ControlFlowGraph? previous = null;
        foreach (IOperation root in context.OperationBlocks)
        {
            if (root is not IBlockOperation)
            {
                continue;
            }

            ControlFlowGraph graph = context.GetControlFlowGraph(root);
            if (ReferenceEquals(graph, previous) || graph.Blocks.Length > MaxLeaseFlowBlocks)
            {
                continue;
            }

            previous = graph;
            flow.Run(graph);
        }
    }

    private sealed partial class LeaseFlow
    {
        private readonly OperationBlockAnalysisContext _context;
        private readonly INamedTypeSymbol _leaseType;

        // Non-null in pooled mode: the analysis follows IPoolable objects that are not leases.
        private readonly INamedTypeSymbol? _poolableType;

        // Symbol -> alias group. Each group has one state.
        private readonly Dictionary<ISymbol, int> _groupOf = new(SymbolEqualityComparer.Default);
        private readonly List<ISymbol> _groupSymbol = [];
        private readonly List<int> _parent = [];
        private readonly List<int> _sources = [];
        private readonly List<bool> _untracked = [];
        private readonly List<bool> _usingResource = [];
        private bool[] _leakTracked = [];
        private string?[] _releaseDescription = [];

        // Per graph.
        private ControlFlowGraph _graph = null!;
        private Dictionary<CaptureId, ISymbol?>? _captures;
        private readonly Dictionary<int, byte[]> _states = [];
        private readonly Queue<int> _worklist = new();
        private readonly List<long> _contexts = [];
        private readonly Dictionary<long, int> _contextIds = [];
        private int[]?[] _handlers = [];
        private HashSet<long>? _reported;
        private bool _report;
        private bool _aborted;

        private LeaseFlow(OperationBlockAnalysisContext context, INamedTypeSymbol leaseType, INamedTypeSymbol? poolableType)
        {
            _context = context;
            _leaseType = leaseType;
            _poolableType = poolableType;
        }

        private bool Pooled => _poolableType is not null;

        private bool IsTrackedType(ITypeSymbol type)
            => _poolableType is null
                ? IsLeaseType(type, _leaseType)
                : IsLeaseType(type, _poolableType) && !IsLeaseType(type, _leaseType);

        // ── Alias groups ────────────────────────────────────────────────────

        public static LeaseFlow? TryCreate(OperationBlockAnalysisContext context, INamedTypeSymbol leaseType, INamedTypeSymbol? poolableType)
        {
            LeaseFlow flow = new(context, leaseType, poolableType);

            if (context.OwningSymbol is IMethodSymbol method)
            {
                // A method that returns bool keeps its lease parameters only on success, so leaving
                // one unreleased is not a leak.
                bool conditional = !flow.Pooled && method.ReturnType.SpecialType == SpecialType.System_Boolean;
                foreach (IParameterSymbol parameter in method.Parameters)
                {
                    if (parameter.RefKind == RefKind.None && flow.IsTrackedType(parameter.Type))
                    {
                        int group = flow.GroupFor(parameter);
                        flow._sources[group]++;
                        flow._untracked[group] |= conditional;
                    }
                }
            }

            foreach (IOperation root in context.OperationBlocks)
            {
                foreach (IOperation operation in root.DescendantsAndSelf())
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    flow.Collect(operation);
                }
            }

            return flow._groupSymbol.Count == 0 ? null : flow.Seal();
        }

        private int GroupFor(ISymbol symbol)
        {
            if (!_groupOf.TryGetValue(symbol, out int group))
            {
                group = _groupSymbol.Count;
                _groupOf.Add(symbol, group);
                _groupSymbol.Add(symbol);
                _parent.Add(group);
                _sources.Add(0);
                _untracked.Add(false);
                _usingResource.Add(false);
            }

            return group;
        }

        private int Find(int group)
        {
            while (_parent[group] != group)
            {
                group = _parent[group] = _parent[_parent[group]];
            }

            return group;
        }

        // A lease-typed local or by-value parameter that belongs to the analyzed method itself.
        private bool IsTrackable(ISymbol? symbol)
        {
            ITypeSymbol? type = symbol switch
            {
                ILocalSymbol local => local.Type,
                IParameterSymbol { RefKind: RefKind.None } parameter => parameter.Type,
                _ => null
            };

            return type is not null
                   && this.IsTrackedType(type)
                   && SymbolEqualityComparer.Default.Equals(symbol!.ContainingSymbol, _context.OwningSymbol);
        }

        private static ISymbol? ReferencedSymbol(IOperation? operation)
        {
            while (operation is IConversionOperation conversion)
            {
                operation = conversion.Operand;
            }

            return operation switch
            {
                ILocalReferenceOperation local => local.Local,
                IParameterReferenceOperation parameter => parameter.Parameter,
                _ => null
            };
        }

        private void Collect(IOperation operation)
        {
            switch (operation)
            {
                case IVariableDeclaratorOperation declarator when this.IsTrackable(declarator.Symbol):
                    this.CollectDefinition(declarator.Symbol, declarator.GetVariableInitializer()?.Value);
                    break;

                case ISimpleAssignmentOperation assignment when ReferencedSymbol(assignment.Target) is { } target && this.IsTrackable(target):
                    this.CollectDefinition(target, assignment.Value);
                    break;

                case IDeclarationPatternOperation { DeclaredSymbol: { } declared } pattern when this.IsTrackable(declared):
                    // 'x is Lease y' makes y another name for x; any other pattern source is not followed.
                    if (pattern.Parent is IIsPatternOperation isPattern
                        && ReferencedSymbol(isPattern.Value) is { } matched
                        && this.IsTrackable(matched))
                    {
                        this.Union(declared, matched);
                    }
                    else
                    {
                        _untracked[this.GroupFor(declared)] = !this.Pooled;
                    }

                    break;

                case IDeclarationExpressionOperation declaration when ReferencedSymbol(declaration.Expression) is { } outLocal && this.IsTrackable(outLocal):
                    _untracked[this.GroupFor(outLocal)] = !this.Pooled;
                    break;

                case IForEachLoopOperation forEach:
                    foreach (ILocalSymbol local in forEach.Locals)
                    {
                        if (this.IsTrackable(local))
                        {
                            _untracked[this.GroupFor(local)] = !this.Pooled;
                        }
                    }

                    break;

                case IUsingDeclarationOperation usingDeclaration:
                    this.CollectUsing(usingDeclaration.DeclarationGroup);
                    break;

                case IUsingOperation { Resources: IVariableDeclarationGroupOperation resources }:
                    this.CollectUsing(resources);
                    break;

                case ILocalReferenceOperation or IParameterReferenceOperation:
                    ISymbol? symbol = ReferencedSymbol(operation);
                    if (symbol is not null && this.IsTrackable(symbol))
                    {
                        int group = this.GroupFor(symbol);

                        // The order in which a lambda or local function runs is unknown.
                        if (!SymbolEqualityComparer.Default.Equals(GetEnclosingFunction(operation, _context.OwningSymbol), _context.OwningSymbol))
                        {
                            _untracked[group] = true;
                        }
                    }

                    break;

                default:
                    break;
            }
        }

        private void CollectDefinition(ISymbol target, IOperation? value)
        {
            int group = this.GroupFor(target);
            if (value is null || IsNullValue(value))
            {
                return;
            }

            if (ReferencedSymbol(value) is { } source && this.IsTrackable(source))
            {
                this.Union(target, source);
                return;
            }

            _sources[group]++;
        }

        private void CollectUsing(IVariableDeclarationGroupOperation group)
        {
            foreach (IVariableDeclarationOperation declaration in group.Declarations)
            {
                foreach (IVariableDeclaratorOperation declarator in declaration.Declarators)
                {
                    if (this.IsTrackable(declarator.Symbol))
                    {
                        _usingResource[this.GroupFor(declarator.Symbol)] = true;
                    }
                }
            }
        }

        // Recorded as a pending pair: groups are merged in Seal(), once every source is counted.
        private readonly List<int> _unions = [];

        private void Union(ISymbol a, ISymbol b)
        {
            _unions.Add(this.GroupFor(a));
            _unions.Add(this.GroupFor(b));
        }

        private LeaseFlow Seal()
        {
            int count = _groupSymbol.Count;
            for (int i = 0; i < _unions.Count; i += 2)
            {
                int a = this.Find(_unions[i]);
                int b = this.Find(_unions[i + 1]);
                if (a != b)
                {
                    _parent[b] = a;
                }
            }

            int[] roots = new int[count];
            int[] sources = new int[count];
            int[] members = new int[count];
            bool[] untracked = new bool[count];
            bool[] usingResource = new bool[count];
            for (int i = 0; i < count; i++)
            {
                int root = roots[i] = this.Find(i);
                sources[root] += _sources[i];
                members[root]++;
                untracked[root] |= _untracked[i];
                usingResource[root] |= _usingResource[i];
            }

            _leakTracked = new bool[count];
            _releaseDescription = new string?[count];
            for (int i = 0; i < count; i++)
            {
                int root = roots[i];
                if (members[root] > 1 && sources[root] > 1)
                {
                    // The names do not always refer to the same lease, so each is followed on its
                    // own and none of them can prove a leak.
                    _parent[i] = i;
                    continue;
                }

                _parent[i] = root;
                // A pooled object in a 'using' is released by its own Dispose, which is not followed.
                _untracked[i] = untracked[root] || (this.Pooled && usingResource[root]);
                _usingResource[i] = !this.Pooled && usingResource[root];
                _leakTracked[i] = !this.Pooled && !untracked[root] && !usingResource[root];
            }

            return this;
        }

        private int GroupOf(ISymbol? symbol)
            => symbol is not null && _groupOf.TryGetValue(symbol, out int group) ? this.Find(group) : -1;

        // ── Fixpoint ────────────────────────────────────────────────────────

        public void Run(ControlFlowGraph graph)
        {
            _graph = graph;
            _states.Clear();
            _worklist.Clear();
            _contexts.Clear();
            _contextIds.Clear();
            _contexts.Add(0);
            _aborted = false;
            _report = false;

            this.CollectCaptures();
            this.CollectHandlers();

            int groups = _groupSymbol.Count;
            byte[] entry = new byte[groups];
            for (int i = 0; i < groups; i++)
            {
                // A pooled object is live wherever it comes from; a lease local starts unassigned.
                entry[i] = this.Pooled ? LeaseOwned : LeaseNull;
            }

            foreach (KeyValuePair<ISymbol, int> pair in _groupOf)
            {
                if (pair.Key is IParameterSymbol parameter)
                {
                    byte state = !this.Pooled && IsBorrowedParameter(parameter) ? LeaseBorrowed : LeaseOwned;
                    if (parameter.NullableAnnotation == NullableAnnotation.Annotated)
                    {
                        state |= LeaseNull;
                    }

                    entry[this.Find(pair.Value)] = state;
                }
            }

            this.Join(0, 0, entry);

            while (_worklist.Count > 0 && !_aborted)
            {
                _context.CancellationToken.ThrowIfCancellationRequested();
                this.Process(_worklist.Dequeue());
            }

            if (_aborted)
            {
                return;
            }

            // The states are final: walk every reached block once more and report.
            _report = true;
            _reported ??= [];
            List<int> keys = [.. _states.Keys];
            keys.Sort();
            foreach (int key in keys)
            {
                this.Process(key);
            }
        }

        private void Process(int key)
        {
            int blockCount = _graph.Blocks.Length;
            BasicBlock block = _graph.Blocks[key % blockCount];
            int contextId = key / blockCount;
            byte[] state = (byte[])_states[key].Clone();

            foreach (IOperation operation in block.Operations)
            {
                if (MayThrow(operation))
                {
                    this.FlowToHandlers(block, contextId, state);
                }

                this.Visit(operation, state);
            }

            ControlFlowBranch? fallThrough = block.FallThroughSuccessor;
            if (block.BranchValue is { } branchValue)
            {
                if (MayThrow(branchValue))
                {
                    this.FlowToHandlers(block, contextId, state);
                }

                this.Visit(branchValue, state);

                if (fallThrough?.Semantics == ControlFlowBranchSemantics.Return
                    && this.TrackedSymbol(branchValue) is { } returned
                    && this.GroupOf(returned) is int returnedGroup and >= 0)
                {
                    // Returned to the caller, who is the new owner.
                    state[returnedGroup] = this.Pooled ? state[returnedGroup] : LeaseEscaped;
                }
            }

            if (block.ConditionKind != ControlFlowConditionKind.None && block.ConditionalSuccessor is { } conditional)
            {
                bool takenWhen = block.ConditionKind == ControlFlowConditionKind.WhenTrue;
                bool refines = this.TryGetNullTest(block.BranchValue, out int group, out bool nullWhenTrue);

                this.Follow(conditional, block, contextId, refines ? Refine(state, group, nullWhenTrue == takenWhen) : state);
                if (fallThrough is not null)
                {
                    this.Follow(fallThrough, block, contextId, refines ? Refine(state, group, nullWhenTrue != takenWhen) : state);
                }
            }
            else if (fallThrough is not null)
            {
                this.Follow(fallThrough, block, contextId, state);
            }
        }

        private static byte[] Refine(byte[] state, int group, bool isNull)
        {
            byte[] refined = (byte[])state.Clone();
            byte current = refined[group];
            if ((current & LeaseEscaped) == 0)
            {
                refined[group] = isNull ? LeaseNull : (byte)(current & ~LeaseNull);
            }

            return refined;
        }

        // A finally block runs once per branch that leaves its try block. Each such run is a context,
        // so the state after the finally block returns only to the branch it came from.
        private void Follow(ControlFlowBranch branch, BasicBlock source, int contextId, byte[] state)
        {
            if (branch.Semantics is ControlFlowBranchSemantics.Throw
                or ControlFlowBranchSemantics.Rethrow
                or ControlFlowBranchSemantics.Error
                or ControlFlowBranchSemantics.None)
            {
                return;
            }

            if (branch.Semantics == ControlFlowBranchSemantics.StructuredExceptionHandling)
            {
                if (contextId == 0)
                {
                    // The finally block was entered by an exception, which is not followed.
                    return;
                }

                long entered = _contexts[contextId];
                int parentContext = (int)(entered >> 40);
                BasicBlock from = _graph.Blocks[(int)((entered >> 12) & 0xFFFFFFF)];
                ControlFlowBranch? origin = ((entered >> 11) & 1) == 1 ? from.ConditionalSuccessor : from.FallThroughSuccessor;
                int index = (int)(entered & 0x7FF);

                // Only the last block of the finally region this context runs ends the run.
                if (origin is null || source.Ordinal != origin.FinallyRegions[index].LastBlockOrdinal)
                {
                    return;
                }

                if (index + 1 < origin.FinallyRegions.Length)
                {
                    int next = this.ContextFor(parentContext, from.Ordinal, origin.IsConditionalSuccessor, index + 1);
                    this.Join(origin.FinallyRegions[index + 1].FirstBlockOrdinal, next, state);
                }
                else if (origin.Destination is { } target)
                {
                    this.Join(target.Ordinal, parentContext, state);
                }

                return;
            }

            if (branch.FinallyRegions.Length > 0)
            {
                int first = this.ContextFor(contextId, source.Ordinal, branch.IsConditionalSuccessor, 0);
                this.Join(branch.FinallyRegions[0].FirstBlockOrdinal, first, state);
            }
            else if (branch.Destination is { } destination)
            {
                this.Join(destination.Ordinal, contextId, state);
            }
        }

        private int ContextFor(int parentContext, int sourceOrdinal, bool conditional, int finallyIndex)
        {
            long key = ((long)parentContext << 40) | ((long)sourceOrdinal << 12) | (conditional ? 1L << 11 : 0L) | (uint)finallyIndex;
            if (!_contextIds.TryGetValue(key, out int id))
            {
                if (_contexts.Count >= MaxLeaseFlowContexts || finallyIndex > 0x7FF)
                {
                    _aborted = true;
                    return 0;
                }

                id = _contexts.Count;
                _contexts.Add(key);
                _contextIds.Add(key, id);
            }

            return id;
        }

        private void Join(int blockOrdinal, int contextId, byte[] state)
        {
            if (_aborted)
            {
                return;
            }

            if (_report)
            {
                // The states are final. Each branch into the exit block is one way to leave the
                // method: a lease that is certainly still owned there is never released.
                if (blockOrdinal == _graph.Blocks.Length - 1 && contextId == 0)
                {
                    this.ReportLeaks(state);
                }

                return;
            }

            int key = blockOrdinal + (contextId * _graph.Blocks.Length);
            if (!_states.TryGetValue(key, out byte[]? existing))
            {
                _states.Add(key, (byte[])state.Clone());
                _worklist.Enqueue(key);
                return;
            }

            bool changed = false;
            for (int i = 0; i < existing.Length; i++)
            {
                byte merged = (byte)(existing[i] | state[i]);
                if (merged != existing[i])
                {
                    existing[i] = merged;
                    changed = true;
                }
            }

            if (changed)
            {
                _worklist.Enqueue(key);
            }
        }

        // False for statements that only move values around, so that a release directly before a
        // 'return' is not seen by the catch blocks as "released, then something threw".
        private static bool MayThrow(IOperation operation)
        {
            switch (operation)
            {
                case ILiteralOperation or ILocalReferenceOperation or IParameterReferenceOperation or IFlowCaptureReferenceOperation
                    or IDefaultValueOperation or IInstanceReferenceOperation or IIsNullOperation or IFlowCaptureOperation
                    or IExpressionStatementOperation or IDiscardOperation:
                    break;

                case IConversionOperation { OperatorMethod: null, IsChecked: false }:
                case IFieldReferenceOperation { Instance: null }:
                case IPropertyReferenceOperation { Instance: null }:
                case ISimpleAssignmentOperation { Target: ILocalReferenceOperation or IParameterReferenceOperation or IDiscardOperation }:
                case IUnaryOperation { OperatorMethod: null }:
                    break;

                default:
                    return true;
            }

            foreach (IOperation child in operation.ChildOperations)
            {
                if (MayThrow(child))
                {
                    return true;
                }
            }

            return false;
        }

        private void FlowToHandlers(BasicBlock block, int contextId, byte[] state)
        {
            if (_report || _handlers[block.Ordinal] is not { } handlers)
            {
                return;
            }

            foreach (int handler in handlers)
            {
                this.Join(handler, contextId, state);
            }
        }

        private void CollectHandlers()
        {
            _handlers = new int[]?[_graph.Blocks.Length];
            List<int> found = [];
            foreach (BasicBlock block in _graph.Blocks)
            {
                found.Clear();
                for (ControlFlowRegion? region = block.EnclosingRegion; region is not null; region = region.EnclosingRegion)
                {
                    if (region.Kind != ControlFlowRegionKind.Try || region.EnclosingRegion is not { Kind: ControlFlowRegionKind.TryAndCatch } tryAndCatch)
                    {
                        continue;
                    }

                    foreach (ControlFlowRegion nested in tryAndCatch.NestedRegions)
                    {
                        if (nested.Kind is ControlFlowRegionKind.Catch or ControlFlowRegionKind.FilterAndHandler)
                        {
                            found.Add(nested.FirstBlockOrdinal);
                        }
                    }
                }

                if (found.Count > 0)
                {
                    _handlers[block.Ordinal] = [.. found];
                }
            }
        }

        private void CollectCaptures()
        {
            _captures = null;
            foreach (BasicBlock block in _graph.Blocks)
            {
                foreach (IOperation operation in block.Operations)
                {
                    foreach (IOperation descendant in operation.DescendantsAndSelf())
                    {
                        if (descendant is not IFlowCaptureOperation capture)
                        {
                            continue;
                        }

                        // A capture can be filled from another capture ('a ?? b' copies the tested value).
                        IOperation value = capture.Value;
                        while (value is IConversionOperation conversion)
                        {
                            value = conversion.Operand;
                        }

                        ISymbol? symbol = value is IFlowCaptureReferenceOperation inner
                            ? (_captures is not null && _captures.TryGetValue(inner.Id, out ISymbol? copied) ? copied : null)
                            : ReferencedSymbol(value);
                        if (symbol is not null && this.GroupOf(symbol) < 0)
                        {
                            symbol = null;
                        }

                        _captures ??= [];
                        if (_captures.TryGetValue(capture.Id, out ISymbol? known))
                        {
                            // 'a ?? b' and similar: the capture holds one of several values.
                            if (!SymbolEqualityComparer.Default.Equals(known, symbol))
                            {
                                this.Untrack(known);
                                this.Untrack(symbol);
                                _captures[capture.Id] = null;
                            }
                        }
                        else
                        {
                            _captures.Add(capture.Id, symbol);
                        }
                    }
                }
            }
        }

        private void Untrack(ISymbol? symbol)
        {
            int group = this.GroupOf(symbol);
            if (group >= 0)
            {
                _untracked[group] = true;
                _leakTracked[group] = false;
            }
        }
    }
}
