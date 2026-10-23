// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Nalix.Analyzers.Diagnostics;

namespace Nalix.Analyzers.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed partial class NalixUsageAnalyzer : DiagnosticAnalyzer
{
    private const int UnusuallyLargeSerializeOrderGapThreshold = 128;

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [
            DiagnosticDescriptors.DuplicateControllerOpcode,
            DiagnosticDescriptors.ControllerMethodRequiresOpcode,
            DiagnosticDescriptors.InvalidControllerHandlerSignature,
            DiagnosticDescriptors.PacketContextTypeMismatch,
            DiagnosticDescriptors.HandlerPacketTypeMismatch,
            DiagnosticDescriptors.MiddlewareTypeMismatch,
            DiagnosticDescriptors.ControllerMissingPacketHandlerAttribute,
            DiagnosticDescriptors.PacketRegistryPacketMissingDeserializer,
            DiagnosticDescriptors.PacketBaseSelfTypeMismatch,
            DiagnosticDescriptors.PacketDeserializerSelfTypeMismatch,
            DiagnosticDescriptors.PacketBaseMissingDeserializeMethod,
            DiagnosticDescriptors.ExplicitSerializationMemberMissingOrder,
            DiagnosticDescriptors.DuplicateSerializeOrder,
            DiagnosticDescriptors.SerializeIgnoreConflictsWithOrder,
            DiagnosticDescriptors.SerializeDynamicSizeOnFixedMember,
            DiagnosticDescriptors.PacketDeserializeSignatureInvalid,
            DiagnosticDescriptors.PacketRegistryPacketMustBeConcrete,
            DiagnosticDescriptors.ResetForPoolShouldCallBase,
            DiagnosticDescriptors.NegativeSerializeOrder,
            DiagnosticDescriptors.ReservedPacketHeaderSlot,
            DiagnosticDescriptors.UnsupportedConfigurationPropertyType,
            DiagnosticDescriptors.ConfigurationPropertyNotBindable,
            DiagnosticDescriptors.MetadataProviderClearsOpcode,
            DiagnosticDescriptors.MetadataProviderOverwritesOpcodeWithoutGuard,
            DiagnosticDescriptors.RequestOptionsRetryCountNegative,
            DiagnosticDescriptors.RequestOptionsTimeoutNegative,
            DiagnosticDescriptors.PacketMiddlewareMissingOrder,
            DiagnosticDescriptors.InboundMiddlewareAlwaysExecuteIgnored,
            DiagnosticDescriptors.MiddlewareRegistrationDuplicateOrder,
            DiagnosticDescriptors.SerializeHeaderConflictsWithOrder,
            DiagnosticDescriptors.ReservedOpCodeRange,
            DiagnosticDescriptors.GlobalDuplicateOpcode,
            DiagnosticDescriptors.AllocationInHotPath,
            DiagnosticDescriptors.OpCodeDocMismatch,
            DiagnosticDescriptors.PotentialBufferLeaseLeak,
            DiagnosticDescriptors.NetworkHostingMissingBufferPoolManager,
            DiagnosticDescriptors.NetworkHostingMissingConnectionHub,
            DiagnosticDescriptors.NetworkHostingHandlerTypeInvalid,
            DiagnosticDescriptors.NetworkHostingMetadataProviderTypeInvalid,
            DiagnosticDescriptors.NetworkHostingMissingTcpBinding,
            DiagnosticDescriptors.NetworkHostingUdpWithoutTcpBinding,
            DiagnosticDescriptors.UnusuallyLargeSerializeOrderGap,
            DiagnosticDescriptors.DispatchLoopCountOutOfRange,
            DiagnosticDescriptors.UnsupportedControllerHandlerReturnType,
            DiagnosticDescriptors.PacketOpcodeOnNonControllerType,
            DiagnosticDescriptors.FixedSizeSerializableHasDynamicMember,
            DiagnosticDescriptors.PacketDeserializeSpanOverloadMissing,
            DiagnosticDescriptors.DuplicatePacketHandlerName,
            DiagnosticDescriptors.RedundantPacketContextPacketCast,
            DiagnosticDescriptors.MiddlewareRegistrationNullLiteral,
            DiagnosticDescriptors.RequestOptionsInfiniteTimeoutWithRetry,
            DiagnosticDescriptors.GenericPacketHandlerMethod,
            DiagnosticDescriptors.UnguardedCatchException,
            DiagnosticDescriptors.DisallowedCryptographyUsage,
            DiagnosticDescriptors.AllocatingEndpointFormatting,
            DiagnosticDescriptors.UnboundedReflectionInAotCode,
            DiagnosticDescriptors.EagerStringFormattingInDiagnosticLog,
            DiagnosticDescriptors.PacketScopeNotDisposed,
            DiagnosticDescriptors.PacketContextEscapesHandlerScope,
            DiagnosticDescriptors.RpcServiceInvalidReturnType,
            DiagnosticDescriptors.RpcServiceInvalidParameters,
            DiagnosticDescriptors.RpcServiceContainsInvalidMembers,
            DiagnosticDescriptors.RpcServiceMissingAttribute,
            DiagnosticDescriptors.InjectFieldCannotBeReadOnly,
            DiagnosticDescriptors.RequiredSymbolUnresolved,
            DiagnosticDescriptors.DuplicatePacketStaticOpCode,
            DiagnosticDescriptors.PacketResetForPoolMissingMemberReset
        ];

    public override void Initialize(AnalysisContext context)
    {
        if (context is null)
        {
            return;
        }

        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(static startContext =>
        {
            SymbolSet? symbols = SymbolSet.Create(startContext.Compilation, out string? missingSymbol);
            if (symbols is null)
            {
                startContext.RegisterCompilationEndAction(endContext =>
                    endContext.ReportDiagnostic(Diagnostic.Create(
                        DiagnosticDescriptors.RequiredSymbolUnresolved,
                        Location.None,
                        missingSymbol)));
                return;
            }

            ConcurrentDictionary<string, (IMethodSymbol Method, INamedTypeSymbol Controller)> globalOpcodes = new();
            ConcurrentDictionary<string, INamedTypeSymbol> controllerNames = new(StringComparer.OrdinalIgnoreCase);
            ConcurrentDictionary<ushort, INamedTypeSymbol> globalPacketOpcodes = new();

            startContext.RegisterSymbolAction(
                symbolContext => AnalyzeNamedType(symbolContext, symbols, globalOpcodes, controllerNames, globalPacketOpcodes),
                SymbolKind.NamedType);

            startContext.RegisterSymbolAction(
                symbolContext => AnalyzeMethod(symbolContext, symbols),
                SymbolKind.Method);

            startContext.RegisterOperationAction(
                operationContext => AnalyzeObjectCreation(operationContext, symbols),
                OperationKind.ObjectCreation);

            startContext.RegisterOperationAction(
                operationContext => AnalyzeInvocation(operationContext, symbols),
                OperationKind.Invocation);

            startContext.RegisterSyntaxNodeAction(
                syntaxContext => AnalyzeMethodDeclaration(syntaxContext, symbols),
                Microsoft.CodeAnalysis.CSharp.SyntaxKind.MethodDeclaration);

            startContext.RegisterOperationAction(
                operationContext => AnalyzeCatchClause(operationContext, symbols),
                OperationKind.CatchClause);

            startContext.RegisterOperationAction(
                operationContext => AnalyzeCryptographyUsage(operationContext),
                OperationKind.Invocation,
                OperationKind.ObjectCreation);

            startContext.RegisterOperationAction(
                operationContext => AnalyzeEndpointFormatting(operationContext),
                OperationKind.Invocation,
                OperationKind.PropertyReference);

            startContext.RegisterOperationAction(
                operationContext => AnalyzeReflectionUsage(operationContext),
                OperationKind.Invocation);

            startContext.RegisterOperationAction(
                operationContext => AnalyzeDiagnosticLogCreation(operationContext),
                OperationKind.ObjectCreation);

            startContext.RegisterSyntaxNodeAction(
                syntaxContext => AnalyzeLocalDeclaration(syntaxContext, symbols),
                Microsoft.CodeAnalysis.CSharp.SyntaxKind.LocalDeclarationStatement);

            startContext.RegisterSyntaxNodeAction(
                AnalyzeFieldDeclaration,
                Microsoft.CodeAnalysis.CSharp.SyntaxKind.FieldDeclaration);

            startContext.RegisterOperationAction(
                operationContext => AnalyzeContextEscape(operationContext, symbols),
                OperationKind.SimpleAssignment,
                OperationKind.Invocation,
                OperationKind.EventAssignment);
        });
    }

    private static void AnalyzeFieldDeclaration(SyntaxNodeAnalysisContext context)
    {
        FieldDeclarationSyntax fieldDeclaration = (FieldDeclarationSyntax)context.Node;

        bool hasInjectAttribute = false;
        foreach (AttributeListSyntax attributeList in fieldDeclaration.AttributeLists)
        {
            foreach (AttributeSyntax attribute in attributeList.Attributes)
            {
                String name = attribute.Name.ToString();
                if (name == "Inject"
                    || name == "InjectAttribute"
                    || name.EndsWith(".Inject", StringComparison.Ordinal)
                    || name.EndsWith(".InjectAttribute", StringComparison.Ordinal))
                {
                    hasInjectAttribute = true;
                    break;
                }
            }

            if (hasInjectAttribute)
            {
                break;
            }
        }

        if (!hasInjectAttribute)
        {
            return;
        }

        bool isReadOnly = false;
        foreach (SyntaxToken modifier in fieldDeclaration.Modifiers)
        {
            if (modifier.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.ReadOnlyKeyword))
            {
                isReadOnly = true;
                break;
            }
        }

        if (isReadOnly)
        {
            foreach (VariableDeclaratorSyntax variable in fieldDeclaration.Declaration.Variables)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.InjectFieldCannotBeReadOnly,
                    variable.GetLocation(),
                    variable.Identifier.Text));
            }
        }
    }

    private static void AnalyzeNamedType(
        SymbolAnalysisContext context,
        SymbolSet symbols,
        ConcurrentDictionary<string, (IMethodSymbol Method, INamedTypeSymbol Controller)> globalOpcodes,
        ConcurrentDictionary<string, INamedTypeSymbol> controllerNames,
        ConcurrentDictionary<ushort, INamedTypeSymbol> globalPacketOpcodes)
    {
        INamedTypeSymbol typeSymbol = (INamedTypeSymbol)context.Symbol;

        if (typeSymbol.TypeKind is not (TypeKind.Class or TypeKind.Struct) || typeSymbol.IsAbstract || typeSymbol.IsImplicitlyDeclared)
        {
            return;
        }

        AnalyzePacketType(context, typeSymbol, symbols, globalPacketOpcodes);
        AnalyzeSerializationType(context, typeSymbol, symbols);
        AnalyzeConfigurationType(context, typeSymbol, symbols);
        AnalyzeMetadataProviderType(context, typeSymbol, symbols);
        AnalyzeMiddlewareType(context, typeSymbol, symbols);
        AnalyzeRpcServiceType(context, typeSymbol, symbols);

        if (HasAttribute(typeSymbol, symbols.ControllerAttribute))
        {
            AnalyzeControllerType(context, typeSymbol, symbols, globalOpcodes, controllerNames);
        }
    }

    private static void AnalyzeConfigurationType(SymbolAnalysisContext context, INamedTypeSymbol typeSymbol, SymbolSet symbols)
    {
        if (!HasBaseType(typeSymbol, symbols.ConfigurationLoaderType))
        {
            return;
        }

        foreach (IPropertySymbol property in typeSymbol.GetMembers().OfType<IPropertySymbol>())
        {
            if (property.IsImplicitlyDeclared
                || property.DeclaredAccessibility != Accessibility.Public
                || property.IsStatic
                || HasAttribute(property, symbols.ConfigurationIgnoreAttribute))
            {
                continue;
            }

            if (property.SetMethod is null)
            {
                Report(
                    context,
                    DiagnosticDescriptors.ConfigurationPropertyNotBindable,
                    property,
                    property.Name,
                    typeSymbol.Name,
                    "does not declare a setter");
                continue;
            }

            if (property.SetMethod.DeclaredAccessibility != Accessibility.Public)
            {
                Report(
                    context,
                    DiagnosticDescriptors.ConfigurationPropertyNotBindable,
                    property,
                    property.Name,
                    typeSymbol.Name,
                    "does not have a public setter");
                continue;
            }

            if (!IsSupportedConfigurationType(property.Type))
            {
                Report(
                    context,
                    DiagnosticDescriptors.UnsupportedConfigurationPropertyType,
                    property,
                    property.Name,
                    typeSymbol.Name,
                    property.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
            }
        }
    }

    private static void AnalyzeMetadataProviderType(SymbolAnalysisContext context, INamedTypeSymbol typeSymbol, SymbolSet symbols)
    {
        if (!Implements(typeSymbol, symbols.PacketMetadataProviderType))
        {
            return;
        }

        foreach (IMethodSymbol method in typeSymbol.GetMembers().OfType<IMethodSymbol>())
        {
            if (method.Name != "Populate"
                || method.MethodKind != MethodKind.Ordinary
                || method.Parameters.Length != 2
                || !IsSymbol(method.Parameters[0].Type, symbols.MethodInfoType)
                || !IsSymbol(method.Parameters[1].Type, symbols.PacketMetadataBuilderType))
            {
                continue;
            }

            foreach (SyntaxReference syntaxReference in method.DeclaringSyntaxReferences)
            {
                if (syntaxReference.GetSyntax(context.CancellationToken) is not MethodDeclarationSyntax methodSyntax)
                {
                    continue;
                }

                AnalyzePopulateMethod(context, typeSymbol, method, methodSyntax, symbols);
            }
        }
    }

    private static void AnalyzeMiddlewareType(SymbolAnalysisContext context, INamedTypeSymbol typeSymbol, SymbolSet symbols)
    {
        if (!ImplementsOpenGeneric(typeSymbol, symbols.PacketMiddlewareType))
        {
            return;
        }

        AttributeData? orderAttribute = typeSymbol.GetAttributes()
            .FirstOrDefault(a => IsSymbol(a.AttributeClass, symbols.MiddlewareOrderAttribute));
        AttributeData? stageAttribute = typeSymbol.GetAttributes()
            .FirstOrDefault(a => IsSymbol(a.AttributeClass, symbols.MiddlewareStageAttribute));

        if (orderAttribute is null)
        {
            Report(context, DiagnosticDescriptors.PacketMiddlewareMissingOrder, typeSymbol, typeSymbol.Name);
        }

        if (stageAttribute is not null && IsInboundAlwaysExecute(stageAttribute, symbols.MiddlewareStageType))
        {
            Report(context, DiagnosticDescriptors.InboundMiddlewareAlwaysExecuteIgnored, typeSymbol, typeSymbol.Name);
        }
    }

    private static void AnalyzePopulateMethod(
        SymbolAnalysisContext context,
        INamedTypeSymbol typeSymbol,
        IMethodSymbol methodSymbol,
        MethodDeclarationSyntax methodSyntax,
        SymbolSet _)
    {
        SyntaxNode? bodyRoot = (SyntaxNode?)methodSyntax.Body ?? methodSyntax.ExpressionBody?.Expression;
        if (bodyRoot is null)
        {
            return;
        }

        string builderParameterName = methodSymbol.Parameters[1].Name;
        bool hasOpcodeGuard = bodyRoot.DescendantNodesAndSelf().Any(node => IsOpcodeGuard(node, builderParameterName));
        bool reportedOverwrite = false;

        foreach (AssignmentExpressionSyntax assignment in bodyRoot.DescendantNodesAndSelf().OfType<AssignmentExpressionSyntax>())
        {
            if (!IsBuilderOpcodeAccess(assignment.Left, builderParameterName))
            {
                continue;
            }

            if (assignment.Right.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.NullLiteralExpression))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.MetadataProviderClearsOpcode,
                    assignment.GetLocation(),
                    typeSymbol.Name));
                continue;
            }

            if (!hasOpcodeGuard && !reportedOverwrite)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.MetadataProviderOverwritesOpcodeWithoutGuard,
                    assignment.GetLocation(),
                    typeSymbol.Name));
                reportedOverwrite = true;
            }
        }
    }

    private static void AnalyzeSerializationType(SymbolAnalysisContext context, INamedTypeSymbol typeSymbol, SymbolSet symbols)
    {
        AttributeData? serializePackable = typeSymbol.GetAttributes()
            .FirstOrDefault(a => IsSymbol(a.AttributeClass, symbols.SerializePackableAttribute));

        if (serializePackable is null)
        {
            return;
        }

        bool isExplicitLayout = IsExplicitSerializeLayout(serializePackable, symbols.SerializeLayoutType);
        bool isPacketBaseType = InheritsPacketBase(typeSymbol, symbols);
        List<(ISymbol Member, int Order)> serializeOrderedMembers = [];

        List<ISymbol> allMembers = [];
        for (INamedTypeSymbol? current = typeSymbol; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
        {
            allMembers.AddRange(current.GetMembers());
        }

        foreach (ISymbol member in allMembers)
        {
            if (member.Kind is not (SymbolKind.Property or SymbolKind.Field) || member.IsImplicitlyDeclared || member.IsStatic)
            {
                continue;
            }

            bool hasIgnore = HasAttribute(member, symbols.SerializeIgnoreAttribute);
            int? headerOrder = GetSerializeOrder(member, symbols.SerializeHeaderAttribute);
            int? order = GetSerializeOrder(member, symbols.SerializeOrderAttribute);
            bool hasDynamic = HasAttribute(member, symbols.SerializeDynamicSizeAttribute);
            ITypeSymbol memberType = member is IPropertySymbol property ? property.Type : ((IFieldSymbol)member).Type;

            if (hasIgnore && order.HasValue)
            {
                Report(context, DiagnosticDescriptors.SerializeIgnoreConflictsWithOrder, member, member.Name);
            }

            if (headerOrder.HasValue && order.HasValue)
            {
                Report(
                    context,
                    DiagnosticDescriptors.SerializeHeaderConflictsWithOrder,
                    member,
                    member.Name);
            }

            int? finalOrder = headerOrder ?? order;

            if (finalOrder.HasValue)
            {
                if (order.HasValue)
                {
                    serializeOrderedMembers.Add((member, order.Value));
                }

                if (finalOrder.Value < 0)
                {
                    Report(context, DiagnosticDescriptors.NegativeSerializeOrder, member, member.Name, finalOrder.Value);
                }

                if (isPacketBaseType
                    && headerOrder.HasValue && headerOrder.Value == 0
                    && SymbolEqualityComparer.Default.Equals(member.ContainingType, typeSymbol))
                {
                    Report(
                        context,
                        DiagnosticDescriptors.ReservedPacketHeaderSlot,
                        member,
                        member.Name);
                }


            }
            else if (isExplicitLayout && !hasIgnore)
            {
                Report(context, DiagnosticDescriptors.ExplicitSerializationMemberMissingOrder, member, member.Name);
            }

            if (hasDynamic && IsFixedSizeSerializationType(memberType))
            {
                Report(
                    context,
                    DiagnosticDescriptors.SerializeDynamicSizeOnFixedMember,
                    member,
                    member.Name,
                    memberType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
            }
        }

        foreach (IGrouping<int, (ISymbol Member, int Order)> duplicateGroup in serializeOrderedMembers.GroupBy(static x => x.Order).Where(static g => g.Count() > 1))
        {
            foreach ((ISymbol member, _) in duplicateGroup)
            {
                Report(
                    context,
                    DiagnosticDescriptors.DuplicateSerializeOrder,
                    member,
                    member.Name,
                    duplicateGroup.Key,
                    typeSymbol.Name);
            }
        }

        bool hasDuplicateSerializeOrder = serializeOrderedMembers
            .GroupBy(static x => x.Order)
            .Any(static g => g.Count() > 1);

        if (hasDuplicateSerializeOrder)
        {
            return;
        }

        List<(ISymbol Member, int Order)> sortedSerializeOrders = [.. serializeOrderedMembers.OrderBy(static x => x.Order)];
        for (int i = 1; i < sortedSerializeOrders.Count; i++)
        {
            (ISymbol currentMember, int currentOrder) = sortedSerializeOrders[i];
            int previousOrder = sortedSerializeOrders[i - 1].Order;
            int gap = currentOrder - previousOrder;
            if (gap >= UnusuallyLargeSerializeOrderGapThreshold)
            {
                Report(
                    context,
                    DiagnosticDescriptors.UnusuallyLargeSerializeOrderGap,
                    currentMember,
                    currentMember.Name,
                    currentOrder,
                    previousOrder);
            }
        }
    }

    private static void AnalyzeMethodDeclaration(SyntaxNodeAnalysisContext context, SymbolSet symbols)
    {
        MethodDeclarationSyntax methodDeclaration = (MethodDeclarationSyntax)context.Node;
        if (context.SemanticModel.GetDeclaredSymbol(methodDeclaration, context.CancellationToken) is not IMethodSymbol methodSymbol)
        {
            return;
        }

        AnalyzeRedundantPacketContextPacketCast(context, methodDeclaration, methodSymbol, symbols);

        if (methodDeclaration.Identifier.ValueText != "ResetForPool"
            || methodDeclaration.ParameterList.Parameters.Count != 0
            || !methodSymbol.IsOverride
            || methodSymbol.ContainingType is not INamedTypeSymbol containingType
            || !InheritsPacketBase(containingType, symbols))
        {
            return;
        }

        bool callsBaseReset = methodDeclaration.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Any(invocation =>
                invocation.Expression is MemberAccessExpressionSyntax
                {
                    Expression: BaseExpressionSyntax,
                    Name.Identifier.ValueText: "ResetForPool"
                });

        if (!callsBaseReset)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.ResetForPoolShouldCallBase,
                methodDeclaration.Identifier.GetLocation(),
                containingType.Name));
        }

        AnalyzePacketResetForPoolCompleteness(context, methodDeclaration, containingType, symbols);
    }

    private static void AnalyzePacketResetForPoolCompleteness(
        SyntaxNodeAnalysisContext context,
        MethodDeclarationSyntax methodDeclaration,
        INamedTypeSymbol containingType,
        SymbolSet symbols)
    {
        ImmutableArray<ISymbol> declaredMembers = containingType.GetMembers();
        List<ISymbol> mutableMembers = [];

        foreach (ISymbol member in declaredMembers)
        {
            if (member.IsStatic || member.IsImplicitlyDeclared)
            {
                continue;
            }

            if (HasSkipCleanOrInject(member, symbols))
            {
                continue;
            }

            if (member is IPropertySymbol prop)
            {
                if (prop.IsReadOnly || prop.IsIndexer || prop.SetMethod is null || prop.SetMethod.IsInitOnly)
                {
                    continue;
                }

                bool isAutoProperty = declaredMembers
                    .OfType<IFieldSymbol>()
                    .Any(f => SymbolEqualityComparer.Default.Equals(f.AssociatedSymbol, prop));
                if (!isAutoProperty)
                {
                    continue;
                }

                mutableMembers.Add(prop);
            }
            else if (member is IFieldSymbol field)
            {
                if (field.IsReadOnly || field.IsConst)
                {
                    continue;
                }

                mutableMembers.Add(field);
            }
        }

        if (mutableMembers.Count == 0)
        {
            return;
        }

        HashSet<string> touchedNames = new(StringComparer.Ordinal);
        CollectTouchedMemberNames(methodDeclaration, containingType, touchedNames, context.CancellationToken, depth: 0);

        foreach (ISymbol member in mutableMembers)
        {
            if (!touchedNames.Contains(member.Name))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.PacketResetForPoolMissingMemberReset,
                    methodDeclaration.Identifier.GetLocation(),
                    containingType.Name,
                    member.Name));
            }
        }
    }

    private static void CollectTouchedMemberNames(
        SyntaxNode root,
        INamedTypeSymbol containingType,
        HashSet<string> touchedNames,
        System.Threading.CancellationToken cancellationToken,
        int depth)
    {
        foreach (SyntaxNode node in root.DescendantNodes())
        {
            switch (node)
            {
                case AssignmentExpressionSyntax assignment:
                    if (TryExtractInstanceMemberName(assignment.Left, out string? assignedName))
                    {
                        _ = touchedNames.Add(assignedName!);
                    }
                    break;

                case PrefixUnaryExpressionSyntax prefixUnary:
                    if (TryExtractInstanceMemberName(prefixUnary.Operand, out string? prefixName))
                    {
                        _ = touchedNames.Add(prefixName!);
                    }
                    break;

                case PostfixUnaryExpressionSyntax postfixUnary:
                    if (TryExtractInstanceMemberName(postfixUnary.Operand, out string? postfixName))
                    {
                        _ = touchedNames.Add(postfixName!);
                    }
                    break;

                case ArgumentSyntax argument:
                    if (TryExtractInstanceMemberName(argument.Expression, out string? argMemberName))
                    {
                        _ = touchedNames.Add(argMemberName!);
                    }
                    break;

                case InvocationExpressionSyntax invocation:
                    if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
                    {
                        if (TryExtractInstanceMemberName(memberAccess.Expression, out string? targetMemberName))
                        {
                            _ = touchedNames.Add(targetMemberName!);
                        }
                        else if (depth < 2
                                 && memberAccess.Expression is ThisExpressionSyntax
                                 && memberAccess.Name.Identifier.ValueText != "ResetForPool")
                        {
                            CollectHelperMethodTouchedNames(
                                containingType,
                                memberAccess.Name.Identifier.ValueText,
                                touchedNames,
                                cancellationToken,
                                depth + 1);
                        }
                    }
                    else if (depth < 2
                             && invocation.Expression is IdentifierNameSyntax helperId
                             && helperId.Identifier.ValueText != "ResetForPool")
                    {
                        CollectHelperMethodTouchedNames(
                            containingType,
                            helperId.Identifier.ValueText,
                            touchedNames,
                            cancellationToken,
                            depth + 1);
                    }
                    break;

                case ConditionalAccessExpressionSyntax conditionalAccess:
                    if (TryExtractInstanceMemberName(conditionalAccess.Expression, out string? condTargetName))
                    {
                        _ = touchedNames.Add(condTargetName!);
                    }
                    break;

                default:
                    break;
            }
        }
    }

    private static void CollectHelperMethodTouchedNames(
        INamedTypeSymbol containingType,
        string methodName,
        HashSet<string> touchedNames,
        System.Threading.CancellationToken cancellationToken,
        int depth)
    {
        foreach (IMethodSymbol method in containingType.GetMembers(methodName).OfType<IMethodSymbol>())
        {
            foreach (SyntaxReference syntaxRef in method.DeclaringSyntaxReferences)
            {
                SyntaxNode syntax = syntaxRef.GetSyntax(cancellationToken);
                CollectTouchedMemberNames(syntax, containingType, touchedNames, cancellationToken, depth);
            }
        }
    }

    private static bool TryExtractInstanceMemberName(ExpressionSyntax expression, out string? memberName)
    {
        switch (expression)
        {
            case IdentifierNameSyntax id:
                memberName = id.Identifier.ValueText;
                return true;

            case MemberAccessExpressionSyntax
            {
                Expression: ThisExpressionSyntax,
                Name.Identifier.ValueText: var thisMemberName
            }:
                memberName = thisMemberName;
                return true;

            default:
                memberName = null;
                return false;
        }
    }

    private static bool HasSkipCleanOrInject(ISymbol member, SymbolSet symbols)
    {
        if (HasAttribute(member, symbols.SkipCleanAttribute) || HasAttribute(member, symbols.InjectAttribute))
        {
            return true;
        }

        foreach (AttributeData attr in member.GetAttributes())
        {
            string? attrName = attr.AttributeClass?.Name;
            if (attrName is "SkipClean" or "SkipCleanAttribute" or "Inject" or "InjectAttribute")
            {
                return true;
            }
        }

        return false;
    }

    private static void AnalyzeRedundantPacketContextPacketCast(
        SyntaxNodeAnalysisContext context,
        MethodDeclarationSyntax methodDeclaration,
        IMethodSymbol methodSymbol,
        SymbolSet symbols)
    {
        if (methodSymbol.Parameters.Length == 0)
        {
            return;
        }

        if (methodSymbol.Parameters[0].Type is not INamedTypeSymbol firstParamType
            || !firstParamType.IsGenericType
            || !IsSymbol(firstParamType.ConstructedFrom, symbols.PacketContextType)
            || firstParamType.TypeArguments.Length != 1)
        {
            return;
        }

        ITypeSymbol packetType = firstParamType.TypeArguments[0];
        string contextParamName = methodSymbol.Parameters[0].Name;

        foreach (CastExpressionSyntax cast in methodDeclaration.DescendantNodes().OfType<CastExpressionSyntax>())
        {
            ITypeSymbol? castType = context.SemanticModel.GetTypeInfo(cast.Type, context.CancellationToken).Type;
            if (castType is null || !IsSymbol(castType, packetType))
            {
                continue;
            }

            if (cast.Expression is MemberAccessExpressionSyntax
                {
                    Expression: IdentifierNameSyntax { Identifier.ValueText: var identifierName },
                    Name.Identifier.ValueText: "Packet"
                }
                && identifierName == contextParamName)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.RedundantPacketContextPacketCast,
                    cast.GetLocation(),
                    methodSymbol.Name,
                    packetType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    contextParamName));
            }
        }
    }

    private static void AnalyzePacketType(
        SymbolAnalysisContext context,
        INamedTypeSymbol typeSymbol,
        SymbolSet symbols,
        ConcurrentDictionary<ushort, INamedTypeSymbol> globalPacketOpcodes)
    {
        INamedTypeSymbol? baseType = typeSymbol.BaseType;
        bool isPacketBaseSelfType = false;
        if (baseType is not null
            && baseType.IsGenericType
            && IsSymbol(baseType.ConstructedFrom, symbols.PacketBaseType))
        {
            ITypeSymbol selfTypeArgument = baseType.TypeArguments[0];
            if (!IsSymbol(selfTypeArgument, typeSymbol))
            {
                Location? location = typeSymbol.Locations.FirstOrDefault(static l => l.IsInSource);
                if (location is not null)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        DiagnosticDescriptors.PacketBaseSelfTypeMismatch,
                        location,
                        typeSymbol.Name,
                        selfTypeArgument.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
                }
            }
            else
            {
                isPacketBaseSelfType = true;
            }
        }

        foreach (INamedTypeSymbol implementedInterface in typeSymbol.AllInterfaces.OfType<INamedTypeSymbol>())
        {
            if (!implementedInterface.IsGenericType
                || !IsSymbol(implementedInterface.ConstructedFrom, symbols.PacketDeserializerType))
            {
                continue;
            }

            ITypeSymbol selfTypeArgument = implementedInterface.TypeArguments[0];
            if (!IsSymbol(selfTypeArgument, typeSymbol))
            {
                Location? location = typeSymbol.Locations.FirstOrDefault(static l => l.IsInSource);
                if (location is not null)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        DiagnosticDescriptors.PacketDeserializerSelfTypeMismatch,
                        location,
                        typeSymbol.Name,
                        selfTypeArgument.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
                }
            }
        }

        if (isPacketBaseSelfType)
        {
            AnalyzePacketBaseDeserializeContract(context, typeSymbol);

            if (HasPacketRegistrationAttribute(typeSymbol, symbols)
                && TryGetPacketStaticOpCode(typeSymbol, context.CancellationToken, out ushort staticOpCode, out Location? opCodeLocation))
            {
                INamedTypeSymbol existing = globalPacketOpcodes.GetOrAdd(staticOpCode, typeSymbol);
                if (!IsSymbol(existing, typeSymbol) && opCodeLocation is not null)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        DiagnosticDescriptors.DuplicatePacketStaticOpCode,
                        opCodeLocation,
                        typeSymbol.Name,
                        staticOpCode,
                        existing.Name));
                }
            }
        }

        AnalyzeFixedSizeSerializableDynamics(context, typeSymbol, symbols);
        AnalyzeDeserializeOverloads(context, typeSymbol, symbols);
    }

    private static bool HasPacketRegistrationAttribute(INamedTypeSymbol typeSymbol, SymbolSet symbols)
    {
        if (HasAttribute(typeSymbol, symbols.PacketAttribute))
        {
            return true;
        }

        foreach (AttributeData attr in typeSymbol.GetAttributes())
        {
            if (attr.AttributeClass?.Name is "Packet" or "PacketAttribute")
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetPacketStaticOpCode(
        INamedTypeSymbol typeSymbol,
        System.Threading.CancellationToken cancellationToken,
        out ushort staticOpCode,
        out Location? location)
    {
        staticOpCode = default;
        IPropertySymbol? prop = typeSymbol.GetMembers("StaticOpCode")
            .OfType<IPropertySymbol>()
            .FirstOrDefault(static p => p.IsStatic);

        location = prop?.Locations.FirstOrDefault(static l => l.IsInSource)
            ?? typeSymbol.Locations.FirstOrDefault(static l => l.IsInSource);

        if (prop is null)
        {
            return false;
        }

        foreach (SyntaxReference syntaxRef in prop.DeclaringSyntaxReferences)
        {
            if (syntaxRef.GetSyntax(cancellationToken) is not PropertyDeclarationSyntax propSyntax)
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

            if (expr is not null && TryEvaluateStaticOpCodeExpression(expr, typeSymbol, out staticOpCode))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryEvaluateStaticOpCodeExpression(
        ExpressionSyntax expr,
        INamedTypeSymbol containingType,
        out ushort staticOpCode)
    {
        switch (expr)
        {
            case LiteralExpressionSyntax literal:
                return TryConvertConstantToUShort(literal.Token.Value, out staticOpCode);

            case ParenthesizedExpressionSyntax parenthesized:
                return TryEvaluateStaticOpCodeExpression(parenthesized.Expression, containingType, out staticOpCode);

            case CastExpressionSyntax cast:
                return TryEvaluateStaticOpCodeExpression(cast.Expression, containingType, out staticOpCode);

            case IdentifierNameSyntax id:
                for (INamedTypeSymbol? current = containingType; current is not null; current = current.BaseType)
                {
                    IFieldSymbol? constField = current.GetMembers(id.Identifier.ValueText)
                        .OfType<IFieldSymbol>()
                        .FirstOrDefault(static f => f.HasConstantValue);
                    if (constField is not null && TryConvertConstantToUShort(constField.ConstantValue, out staticOpCode))
                    {
                        return true;
                    }
                }
                break;

            default:
                break;
        }

        staticOpCode = default;
        return false;
    }

    private static bool TryConvertConstantToUShort(object? value, out ushort staticOpCode)
    {
        switch (value)
        {
            case ushort u:
                staticOpCode = u;
                return true;
            case int i and >= 0 and <= ushort.MaxValue:
                staticOpCode = (ushort)i;
                return true;
            case short s and >= 0:
                staticOpCode = (ushort)s;
                return true;
            case byte b:
                staticOpCode = b;
                return true;
            case uint ui and <= ushort.MaxValue:
                staticOpCode = (ushort)ui;
                return true;
            case long l and >= 0 and <= ushort.MaxValue:
                staticOpCode = (ushort)l;
                return true;
            case ulong ul and <= ushort.MaxValue:
                staticOpCode = (ushort)ul;
                return true;
            default:
                staticOpCode = default;
                return false;
        }
    }

    private static void AnalyzeControllerType(
        SymbolAnalysisContext context,
        INamedTypeSymbol typeSymbol,
        SymbolSet symbols,
        ConcurrentDictionary<string, (IMethodSymbol Method, INamedTypeSymbol Controller)> globalOpcodes,
        ConcurrentDictionary<string, INamedTypeSymbol> controllerNames)
    {
        string ns = typeSymbol.ContainingNamespace.ToDisplayString();
        string controllerName = GetPacketHandlerName(typeSymbol, symbols.ControllerAttribute) ?? typeSymbol.Name;
        string controllerKey = $"{ns}.{controllerName}";

        INamedTypeSymbol existing = controllerNames.GetOrAdd(controllerKey, typeSymbol);
        if (!IsSymbol(existing, typeSymbol))
        {
            Report(
                context,
                DiagnosticDescriptors.DuplicatePacketHandlerName,
                typeSymbol,
                controllerName,
                existing.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                typeSymbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
        }

        ImmutableArray<IMethodSymbol> methods = [.. typeSymbol.GetMembers()
            .OfType<IMethodSymbol>()
            .Where(static method => method.MethodKind == MethodKind.Ordinary && method.DeclaredAccessibility == Accessibility.Public && !method.IsImplicitlyDeclared)];

        foreach (IGrouping<ushort, IMethodSymbol> duplicateGroup in methods
                     .Select(method => (Method: method, Opcode: GetOpcode(method, symbols.PacketOpcodeAttribute)))
                     .Where(entry => entry.Opcode.HasValue)
                     .GroupBy(entry => entry.Opcode!.Value, entry => entry.Method)
                     .Where(group => group.Count() > 1))
        {
            foreach (IMethodSymbol duplicateMethod in duplicateGroup)
            {
                Location? location = duplicateMethod.Locations.FirstOrDefault(static l => l.IsInSource);
                if (location is null)
                {
                    continue;
                }

                context.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.DuplicateControllerOpcode,
                    location,
                    duplicateMethod.Name,
                    duplicateGroup.Key));
            }
        }

        foreach (IMethodSymbol methodSymbol in methods)
        {
            if (methodSymbol.IsOverride)
            {
                continue;
            }

            ushort? opcode = GetOpcode(methodSymbol, symbols.PacketOpcodeAttribute);
            bool isCandidate = IsNalixHandlerCandidate(methodSymbol, symbols);

            if (opcode.HasValue)
            {
                if (methodSymbol.IsGenericMethod)
                {
                    Report(context, DiagnosticDescriptors.GenericPacketHandlerMethod, methodSymbol, methodSymbol.Name);
                }

                if (opcode.Value < 0x0100 && !HasInternalByPass(methodSymbol, typeSymbol, symbols))
                {
                    Report(context, DiagnosticDescriptors.ReservedOpCodeRange, methodSymbol, methodSymbol.Name, opcode.Value);
                }

                string opcodeKey = $"{ns}.{opcode.Value}";
                (IMethodSymbol Method, INamedTypeSymbol Controller) = globalOpcodes.GetOrAdd(opcodeKey, (methodSymbol, typeSymbol));
                if (!IsSymbol(Controller, typeSymbol))
                {
                    Report(context, DiagnosticDescriptors.GlobalDuplicateOpcode, methodSymbol, methodSymbol.Name, opcode.Value, Method.Name, Controller.Name);
                }

                if (!HasSupportedParameterSignature(methodSymbol, symbols))
                {
                    Report(context, DiagnosticDescriptors.InvalidControllerHandlerSignature, methodSymbol, methodSymbol.Name);
                }
                else
                {
                    ReportPacketContextMismatchIfAny(context, methodSymbol, symbols);
                }

                if (!HasSupportedReturnType(methodSymbol.ReturnType, symbols))
                {
                    Report(
                        context,
                        DiagnosticDescriptors.UnsupportedControllerHandlerReturnType,
                        methodSymbol,
                        methodSymbol.Name,
                        methodSymbol.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
                }
            }
            else if (isCandidate)
            {
                Report(context, DiagnosticDescriptors.ControllerMethodRequiresOpcode, methodSymbol, methodSymbol.Name);
            }
        }
    }

    // Canonical handler shape: first parameter is IPacketContext<T>, optional trailing parameters must be annotated with [FromScope]
    private static bool HasSupportedParameterSignature(IMethodSymbol methodSymbol, SymbolSet symbols)
    {
        ImmutableArray<IParameterSymbol> parameters = methodSymbol.Parameters;
        if (parameters.Length == 0 || !IsPacketContext(parameters[0].Type, symbols))
        {
            return false;
        }

        for (int i = 1; i < parameters.Length; i++)
        {
            IParameterSymbol param = parameters[i];
            if (!param.Type.IsReferenceType)
            {
                return false;
            }

            if (symbols.FromScopeAttribute is not null && HasAttribute(param, symbols.FromScopeAttribute))
            {
                continue;
            }

            if (param.GetAttributes().Any(a =>
                a.AttributeClass?.ToDisplayString() == "Nalix.Abstractions.Injection.FromScopeAttribute"
                || (symbols.FromScopeAttribute is null && a.AttributeClass?.Name is "FromScope" or "FromScopeAttribute")))
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private static bool HasSupportedReturnType(ITypeSymbol returnType, SymbolSet symbols)
    {
        if (returnType.SpecialType == SpecialType.System_Void)
        {
            return true;
        }

        if (IsSymbol(returnType, symbols.TaskType)
            || IsSymbol(returnType, symbols.ValueTaskType))
        {
            return true;
        }

        if (Implements(returnType, symbols.PacketInterface))
        {
            return true;
        }

        if (returnType is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Byte })
        {
            return true;
        }

        if (IsSymbol(returnType, symbols.MemoryByteType)
            || IsSymbol(returnType, symbols.ReadOnlyMemoryByteType))
        {
            return true;
        }

        if (returnType is INamedTypeSymbol namedReturnType && namedReturnType.IsGenericType)
        {
            if (IsSymbol(namedReturnType.ConstructedFrom, symbols.GenericTaskType)
                || IsSymbol(namedReturnType.ConstructedFrom, symbols.GenericValueTaskType))
            {
                return namedReturnType.TypeArguments.Length == 1
                    && HasSupportedReturnType(namedReturnType.TypeArguments[0], symbols);
            }
        }

        return false;
    }

    private static void AnalyzeFixedSizeSerializableDynamics(SymbolAnalysisContext context, INamedTypeSymbol typeSymbol, SymbolSet symbols)
    {
        if (!Implements(typeSymbol, symbols.FixedSizeSerializableType))
        {
            return;
        }

        foreach (ISymbol member in typeSymbol.GetMembers())
        {
            if (member is not IPropertySymbol and not IFieldSymbol
                || member.IsStatic
                || member.IsImplicitlyDeclared
                || HasAttribute(member, symbols.SerializeIgnoreAttribute))
            {
                continue;
            }

            ITypeSymbol memberType = member is IPropertySymbol p ? p.Type : ((IFieldSymbol)member).Type;
            if (IsDynamicSizeType(memberType, symbols))
            {
                Report(
                    context,
                    DiagnosticDescriptors.FixedSizeSerializableHasDynamicMember,
                    member,
                    typeSymbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    member.Name,
                    memberType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
            }
        }
    }

    private static void AnalyzeDeserializeOverloads(SymbolAnalysisContext context, INamedTypeSymbol typeSymbol, SymbolSet symbols)
    {
        // NALIX052 is a packet-shape rule. Skip utility/static helper types that
        // happen to expose Deserialize overloads (for example LiteSerializer).
        bool isPacketLikeType =
            InheritsPacketBase(typeSymbol, symbols)
            || Implements(typeSymbol, symbols.PacketInterface)
            || typeSymbol.AllInterfaces
                .OfType<INamedTypeSymbol>()
                .Any(interfaceSymbol =>
                    interfaceSymbol.IsGenericType
                    && IsSymbol(interfaceSymbol.ConstructedFrom, symbols.PacketDeserializerType));

        if (!isPacketLikeType)
        {
            return;
        }

        bool hasAnyDeserialize = false;
        bool hasReadOnlySpanDeserialize = false;

        foreach (IMethodSymbol method in EnumerateDeserializeMethods(typeSymbol))
        {
            if (!method.IsStatic
                || method.DeclaredAccessibility != Accessibility.Public)
            {
                continue;
            }

            hasAnyDeserialize = true;

            if (IsValidPacketDeserializeSignature(method, typeSymbol))
            {
                hasReadOnlySpanDeserialize = true;
                break;
            }
        }

        if (hasAnyDeserialize && !hasReadOnlySpanDeserialize)
        {
            Report(context, DiagnosticDescriptors.PacketDeserializeSpanOverloadMissing, typeSymbol, typeSymbol.Name);
        }
    }

    private static void AnalyzePacketBaseDeserializeContract(SymbolAnalysisContext context, INamedTypeSymbol typeSymbol)
    {
        ImmutableArray<IMethodSymbol> declaredSpanDeserializeMethods = [.. typeSymbol.GetMembers("Deserialize")
            .OfType<IMethodSymbol>()
            .Where(static method =>
                method.MethodKind == MethodKind.Ordinary
                && IsReadOnlySpanByteType(method.Parameters.FirstOrDefault()?.Type))];

        if (declaredSpanDeserializeMethods.Length > 0)
        {
            if (declaredSpanDeserializeMethods.Any(method => IsValidPacketDeserializeSignature(method, typeSymbol)))
            {
                return;
            }

            Report(context, DiagnosticDescriptors.PacketDeserializeSignatureInvalid, typeSymbol, typeSymbol.Name);
            return;
        }

        foreach (IMethodSymbol method in EnumerateDeserializeMethods(typeSymbol))
        {
            if (IsValidPacketDeserializeSignature(method, typeSymbol))
            {
                return;
            }
        }

        Report(context, DiagnosticDescriptors.PacketBaseMissingDeserializeMethod, typeSymbol, typeSymbol.Name);
    }

    private static bool IsValidPacketDeserializeSignature(IMethodSymbol method, INamedTypeSymbol containingType)
        => method.IsStatic
           && method.DeclaredAccessibility == Accessibility.Public
           && method.Parameters.Length == 1
           && IsReadOnlySpanByteType(method.Parameters[0].Type)
           && IsSymbol(method.ReturnType, containingType);

    private static IEnumerable<IMethodSymbol> EnumerateDeserializeMethods(INamedTypeSymbol typeSymbol)
    {
        for (INamedTypeSymbol? current = typeSymbol; current is not null; current = current.BaseType)
        {
            foreach (IMethodSymbol method in current.GetMembers("Deserialize")
                .OfType<IMethodSymbol>()
                .Where(static method => method.MethodKind == MethodKind.Ordinary))
            {
                yield return method;
            }
        }
    }

    private static bool IsReadOnlySpanByteType(ITypeSymbol? type)
        => type is INamedTypeSymbol parameterType
           && parameterType.OriginalDefinition.MetadataName == "ReadOnlySpan`1"
           && parameterType.OriginalDefinition.ContainingNamespace.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == "global::System"
           && parameterType.TypeArguments.Length == 1
           && parameterType.TypeArguments[0].SpecialType == SpecialType.System_Byte;

    private static bool IsDynamicSizeType(ITypeSymbol typeSymbol, SymbolSet symbols)
    {
        if (typeSymbol.SpecialType == SpecialType.System_String
            || typeSymbol is IArrayTypeSymbol
            || Implements(typeSymbol, symbols.PacketInterface))
        {
            return true;
        }

        return false;
    }

    private static string? GetPacketHandlerName(INamedTypeSymbol typeSymbol, INamedTypeSymbol? controllerAttributeSymbol)
    {
        AttributeData? attribute = typeSymbol.GetAttributes()
            .FirstOrDefault(a => IsSymbol(a.AttributeClass, controllerAttributeSymbol));
        if (attribute is null)
        {
            return null;
        }

        if (attribute.ConstructorArguments.Length > 0 && attribute.ConstructorArguments[0].Value is string ctorName && !string.IsNullOrWhiteSpace(ctorName))
        {
            return ctorName;
        }

        return attribute.NamedArguments
            .Where(namedArg => namedArg.Key == "Name" && namedArg.Value.Value is string namedName && !string.IsNullOrWhiteSpace(namedName))
            .Select(namedArg => (string)namedArg.Value.Value!)
            .FirstOrDefault();
    }

    private static bool IsNalixHandlerCandidate(IMethodSymbol methodSymbol, SymbolSet symbols)
    {
        if (methodSymbol.Parameters.Length is < 1 or > 3)
        {
            return false;
        }

        ITypeSymbol firstParameterType = methodSymbol.Parameters[0].Type;
        return Implements(firstParameterType, symbols.PacketInterface) || IsPacketContext(firstParameterType, symbols);
    }

    private static bool TryGetPacketContextType(IMethodSymbol methodSymbol, INamedTypeSymbol? packetContextSymbol, out ITypeSymbol? packetType)
    {
        packetType = null;

        if (methodSymbol.Parameters.Length == 0)
        {
            return false;
        }

        ITypeSymbol firstParameterType = methodSymbol.Parameters[0].Type;
        if (firstParameterType is not INamedTypeSymbol namedType
            || !namedType.IsGenericType
            || !IsSymbol(namedType.ConstructedFrom, packetContextSymbol))
        {
            return false;
        }

        packetType = namedType.TypeArguments[0];
        return true;
    }

    private static ITypeSymbol? GetLegacyPacketType(IMethodSymbol methodSymbol, SymbolSet symbols)
    {
        if (methodSymbol.Parameters.Length == 0)
        {
            return null;
        }

        ITypeSymbol firstParameterType = methodSymbol.Parameters[0].Type;
        if (IsPacketContext(firstParameterType, symbols))
        {
            return null;
        }

        return Implements(firstParameterType, symbols.PacketInterface) ? firstParameterType : null;
    }

    private static ushort? GetOpcode(IMethodSymbol methodSymbol, INamedTypeSymbol? packetAttributeSymbol)
    {
        AttributeData? attribute = methodSymbol.GetAttributes()
            .FirstOrDefault(a => IsSymbol(a.AttributeClass, packetAttributeSymbol));

        if (attribute?.ConstructorArguments.Length != 1)
        {
            return null;
        }

        object? value = attribute.ConstructorArguments[0].Value;
        return value switch
        {
            ushort u => u,
            int i => (ushort)i,
            _ => null
        };
    }

    private static bool IsSymbol(ISymbol? symbol, ISymbol? target)
    {
        if (symbol is null || target is null)
        {
            return false;
        }

        if (SymbolEqualityComparer.Default.Equals(symbol, target))
        {
            return true;
        }

        string s1 = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        string s2 = target.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        if (s1 == s2)
        {
            return true;
        }

        return symbol.ToDisplayString() == target.ToDisplayString();
    }

    private static bool HasAttribute(ISymbol symbol, INamedTypeSymbol? attributeSymbol)
        => attributeSymbol is not null && symbol.GetAttributes().Any(a => IsSymbol(a.AttributeClass, attributeSymbol));

    private static bool IsPacketContext(ITypeSymbol? type, SymbolSet symbols)
    {
        if (type is not INamedTypeSymbol namedType)
        {
            return false;
        }

        if (!namedType.IsGenericType)
        {
            return false;
        }

        INamedTypeSymbol genericDef = namedType.ConstructedFrom;
        return IsSymbol(genericDef, symbols.PacketContextType)
               || IsSymbol(genericDef, symbols.PacketContextInterface);
    }

    private static bool HasInternalByPass(IMethodSymbol methodSymbol, INamedTypeSymbol typeSymbol, SymbolSet symbols)
    {
        return HasAttribute(methodSymbol, symbols.ReservedOpcodePermittedAttribute)
               || HasAttribute(typeSymbol, symbols.ReservedOpcodePermittedAttribute);
    }

    private static bool Implements(ITypeSymbol typeSymbol, INamedTypeSymbol? interfaceSymbol)
        => interfaceSymbol is not null && (IsSymbol(typeSymbol, interfaceSymbol)
           || typeSymbol.AllInterfaces.Any(i => IsSymbol(i, interfaceSymbol)));

    private static bool ImplementsOpenGeneric(ITypeSymbol typeSymbol, INamedTypeSymbol? openGenericInterfaceSymbol)
        => openGenericInterfaceSymbol is not null && typeSymbol.AllInterfaces
            .OfType<INamedTypeSymbol>()
            .Any(i => i.IsGenericType && IsSymbol(i.ConstructedFrom, openGenericInterfaceSymbol));

    private static bool IsAssignable(ITypeSymbol from, ITypeSymbol? to)
        => to is not null && (IsSymbol(from, to)
           || from.AllInterfaces.Any(i => IsSymbol(i, to))
           || HasBaseType(from, to));

    private static bool HasBaseType(ITypeSymbol typeSymbol, ITypeSymbol? expectedBase)
    {
        if (expectedBase is null)
        {
            return false;
        }

        ITypeSymbol? current = typeSymbol.BaseType;
        while (current is not null)
        {
            if (IsSymbol(current, expectedBase))
            {
                return true;
            }

            current = current.BaseType;
        }

        return false;
    }

    private static bool InheritsPacketBase(INamedTypeSymbol typeSymbol, SymbolSet symbols)
    {
        INamedTypeSymbol? current = typeSymbol.BaseType;
        while (current is not null)
        {
            if (current.IsGenericType && IsSymbol(current.ConstructedFrom, symbols.PacketBaseType))
            {
                return true;
            }

            current = current.BaseType;
        }

        return false;
    }

    private static bool IsExplicitSerializeLayout(AttributeData serializePackable, INamedTypeSymbol? serializeLayoutType)
    {
        if (serializePackable.ConstructorArguments.Length != 1)
        {
            return false;
        }

        TypedConstant arg = serializePackable.ConstructorArguments[0];
        if (arg.Type is null
            || serializeLayoutType is null
            || !IsSymbol(arg.Type, serializeLayoutType)
            || arg.Value is null)
        {
            return false;
        }

        // Resolve by enum member value instead of hardcoded numeric constants.
        object? explicitValue = serializeLayoutType.GetMembers()
            .OfType<IFieldSymbol>()
            .FirstOrDefault(static field => field.Name == "Explicit")
            ?.ConstantValue;

        return explicitValue is not null
               && Equals(Convert.ToInt32(arg.Value, CultureInfo.InvariantCulture), Convert.ToInt32(explicitValue, CultureInfo.InvariantCulture));
    }

    private static int? GetSerializeOrder(ISymbol symbol, INamedTypeSymbol? serializeOrderAttribute)
    {
        AttributeData? attribute = symbol.GetAttributes()
            .FirstOrDefault(a => IsSymbol(a.AttributeClass, serializeOrderAttribute));

        if (attribute?.ConstructorArguments.Length != 1)
        {
            return null;
        }

        object? value = attribute.ConstructorArguments[0].Value;
        return value switch
        {
            int intValue => intValue,
            byte byteValue => byteValue,
            short shortValue => shortValue,
            _ => null
        };
    }

    private static int? GetMiddlewareOrder(ITypeSymbol typeSymbol, INamedTypeSymbol? middlewareOrderAttribute)
    {
        AttributeData? attribute = typeSymbol.GetAttributes()
            .FirstOrDefault(a => IsSymbol(a.AttributeClass, middlewareOrderAttribute));

        if (attribute?.ConstructorArguments.Length != 1)
        {
            return null;
        }

        object? value = attribute.ConstructorArguments[0].Value;
        return value switch
        {
            int intValue => intValue,
            short shortValue => shortValue,
            byte byteValue => byteValue,
            sbyte sbyteValue => sbyteValue,
            _ => null
        };
    }

    private static bool IsFixedSizeSerializationType(ITypeSymbol typeSymbol)
    {
        if (typeSymbol.SpecialType == SpecialType.System_String)
        {
            return false;
        }

        if (typeSymbol is IArrayTypeSymbol)
        {
            return false;
        }

        return typeSymbol.IsValueType || typeSymbol.SpecialType is not SpecialType.None;
    }

    private static bool IsSupportedConfigurationType(ITypeSymbol typeSymbol)
    {
        if (typeSymbol.TypeKind == TypeKind.Enum)
        {
            return true;
        }

        if (typeSymbol.SpecialType is SpecialType.System_Char
            or SpecialType.System_String
            or SpecialType.System_Boolean
            or SpecialType.System_SByte
            or SpecialType.System_Byte
            or SpecialType.System_Int16
            or SpecialType.System_UInt16
            or SpecialType.System_Int32
            or SpecialType.System_UInt32
            or SpecialType.System_Int64
            or SpecialType.System_UInt64
            or SpecialType.System_Single
            or SpecialType.System_Double
            or SpecialType.System_Decimal
            or SpecialType.System_DateTime)
        {
            return true;
        }

        return typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) is "global::System.Guid" or "global::System.TimeSpan";
    }

    private static bool TryGetConstantInt(IOperation operation, out int value)
    {
        object? constantValue = operation.ConstantValue.HasValue ? operation.ConstantValue.Value : null;
        switch (constantValue)
        {
            case int intValue:
                value = intValue;
                return true;
            case short shortValue:
                value = shortValue;
                return true;
            case sbyte sbyteValue:
                value = sbyteValue;
                return true;
            case byte byteValue:
                value = byteValue;
                return true;
            default:
                value = default;
                return false;
        }
    }

    private static bool IsNullLiteral(IOperation operation)
        => operation.ConstantValue.HasValue && operation.ConstantValue.Value is null;

    private static ITypeSymbol? GetUnderlyingType(IOperation operation)
    {
        IOperation current = operation;
        while (current is IConversionOperation conversion)
        {
            current = conversion.Operand;
        }

        return current.Type ?? operation.Type;
    }



    private static bool IsBuilderOpcodeAccess(ExpressionSyntax expression, string builderParameterName)
        => expression is MemberAccessExpressionSyntax
        {
            Expression: IdentifierNameSyntax { Identifier.ValueText: var identifier },
            Name.Identifier.ValueText: "Opcode"
        } && identifier == builderParameterName;

    private static bool IsInboundAlwaysExecute(AttributeData stageAttribute, INamedTypeSymbol? middlewareStageType)
    {
        if (stageAttribute.ConstructorArguments.Length != 1
            || stageAttribute.ConstructorArguments[0].Type is null
            || !IsSymbol(stageAttribute.ConstructorArguments[0].Type, middlewareStageType)
            || stageAttribute.ConstructorArguments[0].Value is not byte stageValue
            || stageValue != 0)
        {
            return false;
        }

        foreach (KeyValuePair<string, TypedConstant> namedArgument in stageAttribute.NamedArguments)
        {
            if (namedArgument.Key == "AlwaysExecute"
                && namedArgument.Value.Value is bool alwaysExecute
                && alwaysExecute)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsOpcodeGuard(SyntaxNode node, string builderParameterName)
    {
        if (node is BinaryExpressionSyntax binary)
        {
            return (binary.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.EqualsExpression)
                    || binary.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.NotEqualsExpression))
                   && (IsBuilderOpcodeAccess(binary.Left, builderParameterName) || IsBuilderOpcodeAccess(binary.Right, builderParameterName));
        }

        if (node is IsPatternExpressionSyntax pattern)
        {
            return IsBuilderOpcodeAccess(pattern.Expression, builderParameterName);
        }

        return false;
    }

    private static void Report(SymbolAnalysisContext context, DiagnosticDescriptor descriptor, IMethodSymbol methodSymbol, params object[] messageArgs)
    {
        Location? location = methodSymbol.Locations.FirstOrDefault(static l => l.IsInSource);
        if (location is not null)
        {
            context.ReportDiagnostic(Diagnostic.Create(descriptor, location, messageArgs));
        }
    }

    private static void Report(SymbolAnalysisContext context, DiagnosticDescriptor descriptor, ISymbol symbol, params object[] messageArgs)
    {
        Location? location = symbol.Locations.FirstOrDefault(static l => l.IsInSource);
        if (location is not null)
        {
            context.ReportDiagnostic(Diagnostic.Create(descriptor, location, messageArgs));
        }
    }

    private static void Report(OperationAnalysisContext context, DiagnosticDescriptor descriptor, Location location, params object[] messageArgs)
        => context.ReportDiagnostic(Diagnostic.Create(descriptor, location, messageArgs));

    private static void Report(OperationAnalysisContext context, DiagnosticDescriptor descriptor, SyntaxNode syntax, params object[] messageArgs)
    {
        Location location = syntax.GetLocation();
        context.ReportDiagnostic(Diagnostic.Create(descriptor, location, messageArgs));
    }

    private static void AnalyzeMethod(SymbolAnalysisContext context, SymbolSet symbols)
    {
        IMethodSymbol method = (IMethodSymbol)context.Symbol;
        AnalyzePacketOpcodeOnNonController(context, method, symbols);
        AnalyzeOpCodeDocumentation(context, method, symbols);
        AnalyzeBufferLeaseLeak(context, method, symbols);
    }

    private static void AnalyzePacketOpcodeOnNonController(SymbolAnalysisContext context, IMethodSymbol method, SymbolSet symbols)
    {
        if (!HasAttribute(method, symbols.PacketOpcodeAttribute))
        {
            return;
        }

        if (method.ContainingType is null || HasAttribute(method.ContainingType, symbols.ControllerAttribute))
        {
            return;
        }

        Report(
            context,
            DiagnosticDescriptors.PacketOpcodeOnNonControllerType,
            method,
            method.Name,
            method.ContainingType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
    }

    private static void AnalyzeObjectCreation(OperationAnalysisContext context, SymbolSet symbols)
    {
        IObjectCreationOperation creation = (IObjectCreationOperation)context.Operation;
        if (creation.IsImplicit)
        {
            return;
        }

        if (context.ContainingSymbol is not IMethodSymbol method)
        {
            return;
        }

        if (IsNalixHotPath(method, symbols))
        {
            ITypeSymbol? type = creation.Type;
            if (type is null)
            {
                return;
            }

            // Warn for reference types that are not explicitly allowed in hot paths
            if (type.IsReferenceType && !IsAllowedInHotPath(type))
            {
                Report(context, DiagnosticDescriptors.AllocationInHotPath, creation.Syntax, method.Name, type.Name);
            }
        }
    }

    private static bool IsAllowedInHotPath(ITypeSymbol type)
    {
        if (type.IsValueType)
        {
            return true;
        }

        if (type.SpecialType == SpecialType.System_String)
        {
            return true;
        }

        string name = type.Name;
        return name.EndsWith("Exception", StringComparison.Ordinal) ||
               string.Equals(name, "StringBuilder", StringComparison.Ordinal) ||
               string.Equals(name, "CancellationTokenSource", StringComparison.Ordinal);
    }

    private static bool IsNalixHotPath(IMethodSymbol method, SymbolSet symbols)
    {
        if (HasAttribute(method, symbols.PacketOpcodeAttribute))
        {
            return true;
        }

        if (method.Name == "InvokeAsync" && ImplementsOpenGeneric(method.ContainingType, symbols.PacketMiddlewareType))
        {
            return true;
        }

        return false;
    }

    private static bool ContainsInvocation(IOperation? operation, string methodName)
    {
        IOperation? current = operation;
        while (current is not null)
        {
            if (current is IInvocationOperation invocation)
            {
                if (invocation.TargetMethod.Name == methodName)
                {
                    return true;
                }

                current = invocation.Instance;
                continue;
            }

            if (current is IConversionOperation conversion)
            {
                current = conversion.Operand;
                continue;
            }

            if (current is ILocalReferenceOperation localRef)
            {
                return CheckSymbolUsage(localRef, localRef.Local, methodName);
            }

            if (current is IParameterReferenceOperation paramRef)
            {
                return CheckSymbolUsage(paramRef, paramRef.Parameter, methodName);
            }

            break;
        }

        return false;
    }

    private static bool CheckSymbolUsage(IOperation reference, ISymbol symbol, string methodName)
    {
        IOperation? root = reference.Parent;
        while (root is not null && root.Kind != OperationKind.MethodBody && root.Kind != OperationKind.AnonymousFunction)
        {
            root = root.Parent;
        }

        if (root is null)
        {
            return false;
        }

        foreach (IOperation desc in root.Descendants())
        {
            if (desc is IInvocationOperation invocation && invocation.TargetMethod.Name == methodName)
            {
                if (IsMatchingSymbol(invocation.Instance, symbol))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsMatchingSymbol(IOperation? operation, ISymbol symbol)
    {
        while (operation is IInvocationOperation invocation)
        {
            operation = invocation.Instance;
        }

        while (operation is IConversionOperation conversion)
        {
            operation = conversion.Operand;
        }

        if (operation is ILocalReferenceOperation localRef)
        {
            return SymbolEqualityComparer.Default.Equals(localRef.Local, symbol);
        }

        if (operation is IParameterReferenceOperation paramRef)
        {
            return SymbolEqualityComparer.Default.Equals(paramRef.Parameter, symbol);
        }

        return false;
    }

    private static void AnalyzeOpCodeDocumentation(SymbolAnalysisContext context, IMethodSymbol method, SymbolSet symbols)
    {
        ushort? opcode = GetOpcode(method, symbols.PacketOpcodeAttribute);
        if (!opcode.HasValue)
        {
            return;
        }

        string? xml = method.GetDocumentationCommentXml();
        if (string.IsNullOrEmpty(xml))
        {
            return;
        }

        // Look for 0xXXXX patterns
        Match match = Regex.Match(xml, @"0x([0-9A-Fa-f]{1,4})");
        if (match.Success)
        {
            if (ushort.TryParse(match.Groups[1].Value, NumberStyles.HexNumber, null, out ushort docOpcode))
            {
                if (docOpcode != opcode.Value)
                {
                    Report(context, DiagnosticDescriptors.OpCodeDocMismatch, method, method.Name, docOpcode, opcode.Value);
                }
            }
        }
    }

    private static void AnalyzeBufferLeaseLeak(SymbolAnalysisContext context, IMethodSymbol method, SymbolSet symbols)
    {
        if (method.MethodKind != MethodKind.Ordinary || (method.ContainingType.TypeKind == TypeKind.Interface && method.IsAbstract))
        {
            return;
        }

        List<IParameterSymbol> leaseParams = [.. method.Parameters.Where(p => IsSymbol(p.Type, symbols.BufferLeaseType))];
        if (leaseParams.Count == 0)
        {
            return;
        }

        // Skip if the method itself is marked as [Pure]
        if (method.GetAttributes().Any(a => a.AttributeClass?.Name == "PureAttribute"))
        {
            return;
        }

        foreach (SyntaxReference reference in method.DeclaringSyntaxReferences)
        {
            SyntaxNode syntax = reference.GetSyntax(context.CancellationToken);
            string code = syntax.ToString();

            // Look for disposal or return via next delegate
            // This is still a heuristic but we'll make it slightly more specific
            foreach (IParameterSymbol p in leaseParams)
            {
                // Skip parameters explicitly marked as borrowed/no-dispose, or 'out' parameters 
                // where ownership is transferred to the caller.
                if (p.GetAttributes().Any(a => a.AttributeClass?.Name is "BorrowedAttribute" or "NoDisposeAttribute") || p.RefKind == RefKind.Out)
                {
                    continue;
                }

                string name = p.Name;
                // Heuristic: check if the lease is disposed, used in a using block, 
                // passed to a next delegate, or returned in any form.
                // Also consider passing to a constructor as 'potentially owned' by the new object.
                bool isDisposed = code.Contains($"{name}.Dispose()")
                                || code.Contains($"using (var {name}")
                                || code.Contains($"using var {name}")
                                || (code.Contains($"new ") && code.Contains(name)) // Passed to constructor
                                || Regex.IsMatch(code, $@"\b(next|nextHandler|ExecuteAsync|InvokeAsync|SendAsync|ReceiveAsync|WriteAsync|ReadAsync)\s*\([^)]*\b{name}\b")
                                || Regex.IsMatch(code, $@"return\s+[^;]*\b{name}\b")
                                || Regex.IsMatch(code, $@"=> \s*[^;]*\b{name}\b");

                if (!isDisposed)
                {
                    Report(context, DiagnosticDescriptors.PotentialBufferLeaseLeak, p, name);
                }
            }
        }
    }

    // ─── NALIX073: Unguarded catch(Exception) in Nalix Core ──────────────────

    private static void AnalyzeCatchClause(OperationAnalysisContext context, SymbolSet symbols)
    {
        ICatchClauseOperation catchClause = (ICatchClauseOperation)context.Operation;

        if (catchClause.ExceptionType is null
            || !IsSymbol(catchClause.ExceptionType, symbols.CaughtExceptionType))
        {
            return;
        }

        if (!IsNalixCoreAssembly(context.Operation.SemanticModel?.Compilation?.Assembly.Name))
        {
            return;
        }

        if (catchClause.Filter is not null && ContainsExceptionClassifierInvocation(catchClause.Filter, symbols))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            DiagnosticDescriptors.UnguardedCatchException,
            catchClause.Syntax.GetLocation()));
    }

    private static bool ContainsExceptionClassifierInvocation(IOperation filter, SymbolSet symbols)
    {
        // The filter itself may be the invocation (e.g. when (ExceptionClassifier.IsNonFatal(ex)))
        if (filter is IInvocationOperation directInvocation
            && IsSymbol(directInvocation.TargetMethod.ContainingType, symbols.ExceptionClassifierType)
            && directInvocation.TargetMethod.Name == "IsNonFatal")
        {
            return true;
        }

        foreach (IOperation child in filter.ChildOperations)
        {
            if (ContainsExceptionClassifierInvocationRecursive(child, symbols))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsExceptionClassifierInvocationRecursive(IOperation operation, SymbolSet symbols)
    {
        if (operation is IInvocationOperation invocation
            && IsSymbol(invocation.TargetMethod.ContainingType, symbols.ExceptionClassifierType)
            && invocation.TargetMethod.Name == "IsNonFatal")
        {
            return true;
        }

        foreach (IOperation child in operation.ChildOperations)
        {
            if (ContainsExceptionClassifierInvocationRecursive(child, symbols))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsNalixCoreAssembly(string? assemblyName)
    {
        if (string.IsNullOrEmpty(assemblyName))
        {
            return false;
        }

        // Exact match against known Nalix Core assembly names.
        // Excludes test, benchmark, sample, and consumer assemblies.
        return assemblyName is
            "Nalix.Abstractions" or
            "Nalix.Codec" or
            "Nalix.Environment" or
            "Nalix.Framework" or
            "Nalix.Network" or
            "Nalix.Runtime" or
            "Nalix.SDK" or
            "Nalix.Observability" or
            "Nalix.Observability.Extensions" or
            "Nalix.Hosting";
    }

    // ─── NALIX071: Disallowed System.Security.Cryptography usage in Core ────

    private static void AnalyzeCryptographyUsage(OperationAnalysisContext context)
    {
        string? assemblyName = context.Operation.SemanticModel?.Compilation?.Assembly.Name;
        if (!IsNalixCoreAssembly(assemblyName))
        {
            return;
        }

        ITypeSymbol? targetType = context.Operation switch
        {
            IInvocationOperation invocation => invocation.TargetMethod.ContainingType,
            IObjectCreationOperation creation => creation.Type,
            _ => null
        };

        if (targetType is null || !IsInCryptoNamespace(targetType))
        {
            return;
        }

        string usedMember = context.Operation switch
        {
            IInvocationOperation inv => $"{inv.TargetMethod.ContainingType.Name}.{inv.TargetMethod.Name}",
            IObjectCreationOperation cr => cr.Type?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) ?? "unknown",
            _ => "unknown"
        };

        if (IsAllowedCryptographyUsage(context.Operation, targetType))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            DiagnosticDescriptors.DisallowedCryptographyUsage,
            context.Operation.Syntax.GetLocation(),
            usedMember));
    }

    private static bool IsInCryptoNamespace(ITypeSymbol typeSymbol)
    {
        INamespaceSymbol? ns = typeSymbol.ContainingNamespace;
        while (ns is not null)
        {
            if (ns.Name == "Cryptography"
                && ns.ContainingNamespace?.Name == "Security"
                && ns.ContainingNamespace?.ContainingNamespace?.Name == "System"
                && ns.ContainingNamespace?.ContainingNamespace?.ContainingNamespace?.IsGlobalNamespace == true)
            {
                return true;
            }

            ns = ns.ContainingNamespace;
        }

        return false;
    }

    private static bool IsAllowedCryptographyUsage(IOperation operation, ITypeSymbol targetType)
    {
        // Allowlist 1: CryptographicOperations.FixedTimeEquals
        // Nalix has BitwiseOperations.FixedTimeEquals but this BCL call is a
        // known-compat shim until all call sites are migrated.
        if (operation is IInvocationOperation invocation
            && invocation.TargetMethod.Name == "FixedTimeEquals"
            && targetType.Name == "CryptographicOperations")
        {
            return true;
        }

        // Allowlist 2: Platform fallback shims in OsCsprng-like files.
        // Check if the containing method or type is in a known CSPRNG shim.
        ISymbol? containingSymbol = operation.SemanticModel?.GetEnclosingSymbol(
            operation.Syntax.Span.Start, System.Threading.CancellationToken.None);

        if (containingSymbol is not null)
        {
            INamedTypeSymbol? containingType = containingSymbol switch
            {
                IMethodSymbol method => method.ContainingType,
                IFieldSymbol field => field.ContainingType,
                IPropertySymbol prop => prop.ContainingType,
                _ => null
            };

            if (containingType is not null && IsApprovedCryptoShimType(containingType))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsApprovedCryptoShimType(INamedTypeSymbol typeSymbol)
    {
        // Allow types named OsCsprng, OsRandom, or similar platform CSPRNG shims.
        string name = typeSymbol.Name;
        return name is "OsCsprng" or "OsRandom" or "PlatformCsprng";
    }

    // ─── NALIX072: Allocating endpoint formatting in hot paths ──────────────

    private static void AnalyzeEndpointFormatting(OperationAnalysisContext context)
    {
        string? assemblyName = context.Operation.SemanticModel?.Compilation?.Assembly.Name;
        if (!IsNalixCoreAssembly(assemblyName))
        {
            return;
        }

        switch (context.Operation)
        {
            case IInvocationOperation invocation:
                AnalyzeEndpointInvocation(context, invocation);
                break;

            case IPropertyReferenceOperation propertyRef:
                AnalyzeEndpointPropertyReference(context, propertyRef);
                break;

            default:
                break;
        }
    }

    private static void AnalyzeEndpointInvocation(OperationAnalysisContext context, IInvocationOperation invocation)
    {
        IMethodSymbol targetMethod = invocation.TargetMethod;

        // Report IPAddress.ToString()
        if (targetMethod.Name == "ToString"
            && targetMethod.Parameters.Length == 0
            && invocation.Instance is not null
            && IsIPAddressType(invocation.Instance.Type))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.AllocatingEndpointFormatting,
                invocation.Syntax.GetLocation(),
                "IPAddress.ToString()"));
        }
    }

    private static void AnalyzeEndpointPropertyReference(OperationAnalysisContext context, IPropertyReferenceOperation propertyRef)
    {
        // Skip if inside TryFormatAddress (the zero-allocation alternative)
        if (IsInsideTryFormatAddress(context.Operation))
        {
            return;
        }

        string? propertyName = propertyRef.Property.Name;
        ITypeSymbol? containingType = propertyRef.Property.ContainingType;

        if (containingType is null)
        {
            return;
        }

        // Report INetworkEndpoint.Address (allocates in SocketEndpoint implementation)
        if (propertyName == "Address" && IsNetworkEndpointType(containingType))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.AllocatingEndpointFormatting,
                propertyRef.Syntax.GetLocation(),
                $"{containingType.Name}.Address"));
        }
    }

    private static bool IsIPAddressType(ITypeSymbol? type)
    {
        return type is not null
            && type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == "global::System.Net.IPAddress";
    }

    private static bool IsNetworkEndpointType(ITypeSymbol type)
    {
        string fullName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return fullName is
            "global::Nalix.Abstractions.Networking.INetworkEndpoint"
            or "global::Nalix.Network.Internal.Transport.SocketEndpoint";
    }

    private static bool IsInsideTryFormatAddress(IOperation operation)
    {
        IOperation? current = operation.Parent;
        while (current is not null)
        {
            if (current is IInvocationOperation invocation
                && invocation.TargetMethod.Name == "TryFormatAddress")
            {
                return true;
            }

            current = current.Parent;
        }

        return false;
    }

    // ─── NALIX078: Unbounded reflection in AOT-sensitive Core code ──────────

    private static void AnalyzeReflectionUsage(OperationAnalysisContext context)
    {
        if (context.Operation is not IInvocationOperation invocation)
        {
            return;
        }

        string? assemblyName = context.Operation.SemanticModel?.Compilation?.Assembly.Name;
        if (!IsNalixCoreAssembly(assemblyName))
        {
            return;
        }

        IMethodSymbol targetMethod = invocation.TargetMethod;
        ITypeSymbol containingType = targetMethod.ContainingType;
        string containingTypeFullName = containingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        // Check for dangerous assembly scanning
        if (targetMethod.Name is "GetTypes" or "GetExportedTypes"
            && containingTypeFullName is "global::System.Reflection.Assembly")
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.UnboundedReflectionInAotCode,
                invocation.Syntax.GetLocation(),
                $"{containingType.Name}.{targetMethod.Name}"));
            return;
        }

        if (targetMethod.Name == "GetAssemblies"
            && containingTypeFullName is "global::System.AppDomain")
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.UnboundedReflectionInAotCode,
                invocation.Syntax.GetLocation(),
                $"{containingType.Name}.{targetMethod.Name}"));
            return;
        }

        // Check for dynamic code generation
        if (targetMethod.Name == "Compile"
            && (containingTypeFullName is "global::System.Linq.Expressions.LambdaExpression"
                || TypeInheritsFrom(containingType, "global::System.Linq.Expressions.LambdaExpression")))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.UnboundedReflectionInAotCode,
                invocation.Syntax.GetLocation(),
                $"{containingType.Name}.Compile"));
            return;
        }

        // Check for dynamic generic construction
        if (targetMethod.Name is "MakeGenericType" or "MakeGenericMethod"
            && containingTypeFullName is "global::System.Type" or "global::System.Reflection.MethodInfo")
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.UnboundedReflectionInAotCode,
                invocation.Syntax.GetLocation(),
                $"{containingType.Name}.{targetMethod.Name}"));
            return;
        }

        // Check for string-based type lookup
        if (targetMethod.Name == "GetType"
            && targetMethod.Parameters.Length >= 1
            && targetMethod.Parameters[0].Type?.SpecialType == SpecialType.System_String
            && containingTypeFullName is "global::System.Type" or "global::System.Reflection.Assembly")
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.UnboundedReflectionInAotCode,
                invocation.Syntax.GetLocation(),
                $"{containingType.Name}.{targetMethod.Name}"));
            return;
        }

        // NOTE: Activator.CreateInstance(Type) detection is deferred.
        // It requires reliable differentiation between:
        //   - Activator.CreateInstance<T>() (allowed)
        //   - Activator.CreateInstance(typeof(KnownType)) (allowed)
        //   - Activator.CreateInstance(type) with unannotated runtime Type (report)
        //   - Activator.CreateInstance(type) with [DynamicallyAccessedMembers] (allowed)
        // This will be addressed in a future phase with more robust annotation detection.

        // Check for reflection invocations on MemberInfo subtypes
        if (IsReflectionInvokeOrAccessor(targetMethod, containingTypeFullName))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.UnboundedReflectionInAotCode,
                invocation.Syntax.GetLocation(),
                $"{containingType.Name}.{targetMethod.Name}"));
        }
    }

    private static bool IsReflectionInvokeOrAccessor(IMethodSymbol method, string containingTypeFullName)
    {
        // MethodInfo.Invoke, ConstructorInfo.Invoke
        if (method.Name == "Invoke"
            && containingTypeFullName is
                "global::System.Reflection.MethodInfo"
                or "global::System.Reflection.ConstructorInfo"
                or "global::System.Reflection.MethodBase")
        {
            return true;
        }

        // PropertyInfo.GetValue, PropertyInfo.SetValue
        if (method.Name is "GetValue" or "SetValue"
            && containingTypeFullName is "global::System.Reflection.PropertyInfo")
        {
            return true;
        }

        // FieldInfo.GetValue, FieldInfo.SetValue
        if (method.Name is "GetValue" or "SetValue"
            && containingTypeFullName is "global::System.Reflection.FieldInfo")
        {
            return true;
        }

        return false;
    }

    private static bool TypeInheritsFrom(ITypeSymbol type, string baseTypeFullName)
    {
        ITypeSymbol? current = type.BaseType;
        while (current is not null)
        {
            if (current.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == baseTypeFullName)
            {
                return true;
            }

            current = current.BaseType;
        }

        return false;
    }

    // NOTE: HasDynamicallyAccessedConstructorsAnnotation is deferred until
    // Activator.CreateInstance detection is implemented (see NALIX078 report).

    // ─── NALIX074: Eager string formatting in DiagnosticLog ─────────────────

    private static void AnalyzeDiagnosticLogCreation(OperationAnalysisContext context)
    {
        if (context.Operation is not IObjectCreationOperation creation)
        {
            return;
        }

        string? assemblyName = context.Operation.SemanticModel?.Compilation?.Assembly.Name;
        if (!IsNalixCoreAssembly(assemblyName))
        {
            return;
        }

        if (creation.Type is null
            || creation.Type.MetadataName != "DiagnosticLog"
            || creation.Type.ContainingNamespace?.ToDisplayString() != "Nalix.Abstractions.Diagnostics")
        {
            return;
        }

        // Check the Message argument (index 1) for eager string formatting.
        // DiagnosticLog(string Tag, string Message, Exception? Exception = null)
        if (creation.Arguments.Length < 2)
        {
            return;
        }

        IArgumentOperation messageArg = creation.Arguments[1];

        if (ContainsEagerStringFormatting(messageArg.Value)
            && !IsInsideIsEnabledGuard(creation))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.EagerStringFormattingInDiagnosticLog,
                creation.Syntax.GetLocation()));
        }
    }

    private static bool IsInsideIsEnabledGuard(IOperation operation)
    {
        IOperation? current = operation.Parent;
        while (current is not null)
        {
            // Check if we're inside an if statement whose condition calls IsEnabled
            if (current is IConditionalOperation conditional
                && ContainsIsEnabledInvocation(conditional.Condition))
            {
                return true;
            }

            current = current.Parent;
        }

        return false;
    }

    private static bool ContainsIsEnabledInvocation(IOperation operation)
    {
        if (operation is IInvocationOperation invocation
            && invocation.TargetMethod.Name == "IsEnabled")
        {
            return true;
        }

        foreach (IOperation child in operation.ChildOperations)
        {
            if (ContainsIsEnabledInvocation(child))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsEagerStringFormatting(IOperation operation)
    {
        return operation switch
        {
            IInterpolatedStringOperation => true,
            IBinaryOperation binary => binary.OperatorKind == BinaryOperatorKind.Add && IsStringType(binary.Type),
            IInvocationOperation invocation => IsStringFormattingMethod(invocation.TargetMethod),
            IConversionOperation conversion => ContainsEagerStringFormatting(conversion.Operand),
            _ => false
        };
    }

    private static bool IsStringFormattingMethod(IMethodSymbol method)
    {
        if (method.ContainingType?.SpecialType != SpecialType.System_String)
        {
            return false;
        }

        return method.Name is "Format" or "Concat" or "Join";
    }

    private static bool IsStringType(ITypeSymbol? type)
        => type?.SpecialType == SpecialType.System_String;

    // ─── NALIX075: PacketScope must be disposed ──────────────────────────────

    private static void AnalyzeLocalDeclaration(SyntaxNodeAnalysisContext context, SymbolSet symbols)
    {
        if (context.Node is not LocalDeclarationStatementSyntax localDecl)
        {
            return;
        }

        if (localDecl.UsingKeyword.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.UsingKeyword))
        {
            return;
        }

        VariableDeclarationSyntax declaration = localDecl.Declaration;
        ITypeSymbol? declaredType = context.SemanticModel.GetTypeInfo(declaration.Type, context.CancellationToken).Type;

        if (declaredType is null)
        {
            return;
        }

        bool isPacketScope = false;
        if (declaredType is INamedTypeSymbol namedType
            && namedType.IsGenericType
            && IsSymbol(namedType.OriginalDefinition, symbols.PacketScopeType))
        {
            isPacketScope = true;
        }

        if (!isPacketScope)
        {
            return;
        }

        foreach (VariableDeclaratorSyntax variable in declaration.Variables)
        {
            if (IsPassedToDisposeOnCompletionAsync(context, variable))
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.PacketScopeNotDisposed,
                variable.GetLocation(),
                variable.Identifier.Text,
                declaredType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
        }
    }

    private static bool IsPassedToDisposeOnCompletionAsync(SyntaxNodeAnalysisContext context, VariableDeclaratorSyntax variable)
    {
        ISymbol? localSymbol = context.SemanticModel.GetDeclaredSymbol(variable, context.CancellationToken);
        if (localSymbol is null)
        {
            return false;
        }

        Microsoft.CodeAnalysis.CSharp.Syntax.BlockSyntax? enclosingBlock = variable.FirstAncestorOrSelf<Microsoft.CodeAnalysis.CSharp.Syntax.BlockSyntax>();
        if (enclosingBlock is null)
        {
            return false;
        }

        foreach (Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax invocation in enclosingBlock.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>())
        {
            if (invocation.Expression is Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax memberAccess
                && memberAccess.Name.Identifier.Text == "DisposeOnCompletionAsync")
            {
                foreach (Microsoft.CodeAnalysis.CSharp.Syntax.ArgumentSyntax arg in invocation.ArgumentList.Arguments)
                {
                    if (arg.Expression is Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax identifier)
                    {
                        SymbolInfo symbolInfo = context.SemanticModel.GetSymbolInfo(identifier, context.CancellationToken);
                        if (SymbolEqualityComparer.Default.Equals(symbolInfo.Symbol, localSymbol))
                        {
                            return true;
                        }
                    }
                }
            }
        }

        return false;
    }

    // ─── NALIX076: Packet context escape detection ────────────────────────────

    private static void AnalyzeContextEscape(OperationAnalysisContext context, SymbolSet symbols)
    {
        string? assemblyName = context.Operation.SemanticModel?.Compilation?.Assembly.Name;
        if (!IsNalixCoreAssembly(assemblyName))
        {
            return;
        }

        switch (context.Operation)
        {
            case ISimpleAssignmentOperation assignment:
                AnalyzeFieldPropertyAssignment(context, assignment, symbols);
                break;

            case IInvocationOperation invocation:
                AnalyzeContextEscapeInvocation(context, invocation, symbols);
                break;

            case IEventAssignmentOperation eventAssignment:
                AnalyzeEventAssignment(context, eventAssignment, symbols);
                break;

            default:
                break;
        }
    }

    private static void AnalyzeFieldPropertyAssignment(
        OperationAnalysisContext context,
        ISimpleAssignmentOperation assignment,
        SymbolSet symbols)
    {
        if (assignment.Target is not IFieldReferenceOperation and not IPropertyReferenceOperation)
        {
            return;
        }

        // Skip null assignments (e.g., _context = null in ResetForPool)
        if (assignment.Value.ConstantValue is { HasValue: true, Value: null })
        {
            return;
        }

        ITypeSymbol? valueType = assignment.Value.Type;
        if (valueType is null)
        {
            return;
        }

        if (IsPacketContextType(valueType, symbols))
        {
            string targetDesc = assignment.Target switch
            {
                IFieldReferenceOperation f => $"field '{f.Field.Name}'",
                IPropertyReferenceOperation p => $"property '{p.Property.Name}'",
                _ => "member"
            };
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.PacketContextEscapesHandlerScope,
                assignment.Syntax.GetLocation(),
                "PacketContext",
                $"Assigning to {targetDesc} extends the pooled context lifetime beyond the handler."));
        }
        else if (IsPacketType(valueType, symbols))
        {
            string targetDesc = assignment.Target switch
            {
                IFieldReferenceOperation f => $"field '{f.Field.Name}'",
                IPropertyReferenceOperation p => $"property '{p.Property.Name}'",
                _ => "member"
            };
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.PacketContextEscapesHandlerScope,
                assignment.Syntax.GetLocation(),
                "Packet",
                $"Assigning Packet to {targetDesc} extends the pooled packet lifetime beyond the handler."));
        }
    }

    private static void AnalyzeContextEscapeInvocation(
        OperationAnalysisContext context,
        IInvocationOperation invocation,
        SymbolSet symbols)
    {
        IMethodSymbol targetMethod = invocation.TargetMethod;

        // Pattern: Task.Run(() => context/packet)
        if (IsOffloadMethod(targetMethod) && invocation.Arguments.Length >= 1)
        {
            IArgumentOperation firstArg = invocation.Arguments[0];
            if (ArgumentCapturesContextOrPacket(firstArg, symbols))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.PacketContextEscapesHandlerScope,
                    invocation.Syntax.GetLocation(),
                    "PacketContext",
                    $"Offloading to '{targetMethod.ContainingType.Name}.{targetMethod.Name}' with a delegate that captures a pooled context or packet."));
            }
        }

        // Pattern: collection.Add(context) / queue.Enqueue(context.Packet)
        if (IsCollectionAddMethod(targetMethod) && invocation.Arguments.Length >= 1)
        {
            IArgumentOperation firstArg = invocation.Arguments[0];
            if (IsPacketContextType(firstArg.Type, symbols) || IsPacketType(firstArg.Type, symbols))
            {
                string typeName = IsPacketContextType(firstArg.Type, symbols) ? "PacketContext" : "Packet";
                context.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.PacketContextEscapesHandlerScope,
                    invocation.Syntax.GetLocation(),
                    typeName,
                    $"Adding to a collection via '{targetMethod.Name}' extends the pooled lifetime beyond the handler."));
            }
        }
    }

    private static void AnalyzeEventAssignment(
        OperationAnalysisContext context,
        IEventAssignmentOperation eventAssignment,
        SymbolSet symbols)
    {
        // IEventAssignmentOperation doesn't expose a Value property directly;
        // walk child operations to find the delegate/value being assigned.
        foreach (IOperation child in eventAssignment.ChildOperations)
        {
            if (child is IDelegateCreationOperation delegateCreation
                && delegateCreation.Target is IAnonymousFunctionOperation lambda)
            {
                if (LambdaCapturesContextOrPacket(lambda, symbols))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        DiagnosticDescriptors.PacketContextEscapesHandlerScope,
                        eventAssignment.Syntax.GetLocation(),
                        "PacketContext",
                        "Event subscription with a delegate that captures a pooled context or packet."));
                }

                return;
            }
        }
    }

    private static bool IsOffloadMethod(IMethodSymbol method)
    {
        string name = method.Name;
        string containingType = method.ContainingType?.ToDisplayString() ?? "";

        // Task.Run
        if (name == "Run" && containingType.StartsWith("System.Threading.Tasks.Task", StringComparison.Ordinal))
        {
            return true;
        }

        // Task.Factory.StartNew
        if (name == "StartNew" && containingType.Contains("TaskFactory", StringComparison.Ordinal))
        {
            return true;
        }

        // ThreadPool.QueueUserWorkItem
        if (name == "QueueUserWorkItem" && containingType.Contains("ThreadPool", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private static bool IsCollectionAddMethod(IMethodSymbol method)
    {
        string name = method.Name;
        return name is "Add" or "Enqueue" or "TryWrite" or "Push" or "Insert";
    }

    private static bool ArgumentCapturesContextOrPacket(IArgumentOperation argument, SymbolSet symbols)
    {
        // Check if the argument type itself is context/packet
        if (IsPacketContextType(argument.Type, symbols) || IsPacketType(argument.Type, symbols))
        {
            return true;
        }

        // Check if it's a delegate/lambda that captures context/packet
        if (argument.Value is IDelegateCreationOperation delegateCreation
            && delegateCreation.Target is IAnonymousFunctionOperation lambda)
        {
            return LambdaCapturesContextOrPacket(lambda, symbols);
        }

        // Check if it's a method group
        if (argument.Value is IMethodReferenceOperation methodRef)
        {
            // If the method takes a context/packet parameter, the delegate likely captures it
            foreach (IParameterSymbol param in methodRef.Method.Parameters)
            {
                if (IsPacketContextType(param.Type, symbols) || IsPacketType(param.Type, symbols))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool LambdaCapturesContextOrPacket(IAnonymousFunctionOperation lambda, SymbolSet symbols)
    {
        // Walk the lambda body looking for references to context/packet types
        foreach (IOperation descendant in lambda.Descendants())
        {
            switch (descendant)
            {
                case IParameterReferenceOperation paramRef:
                    if (IsPacketContextType(paramRef.Type, symbols) || IsPacketType(paramRef.Type, symbols))
                    {
                        return true;
                    }
                    break;

                case ILocalReferenceOperation localRef:
                    if (IsPacketContextType(localRef.Type, symbols) || IsPacketType(localRef.Type, symbols))
                    {
                        return true;
                    }
                    break;

                case IFieldReferenceOperation fieldRef:
                    if (IsPacketContextType(fieldRef.Type, symbols) || IsPacketType(fieldRef.Type, symbols))
                    {
                        return true;
                    }
                    break;

                case IPropertyReferenceOperation propRef:
                    if (IsPacketContextType(propRef.Type, symbols) || IsPacketType(propRef.Type, symbols))
                    {
                        return true;
                    }
                    break;

                default:
                    break;
            }
        }

        return false;
    }

    private static bool IsPacketContextType(ITypeSymbol? type, SymbolSet symbols)
    {
        if (type is null)
        {
            return false;
        }

        if (type is INamedTypeSymbol namedType && namedType.IsGenericType)
        {
            INamedTypeSymbol originalDef = namedType.OriginalDefinition;
            if (IsSymbol(originalDef, symbols.PacketContextType) || IsSymbol(originalDef, symbols.PacketContextInterface))
            {
                return true;
            }
        }

        // Check if the type implements IPacketContext<T>
        foreach (INamedTypeSymbol iface in type.AllInterfaces)
        {
            if (iface.IsGenericType && IsSymbol(iface.OriginalDefinition, symbols.PacketContextInterface))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPacketType(ITypeSymbol? type, SymbolSet symbols)
    {
        if (type is null)
        {
            return false;
        }

        // Check if it's the packet interface
        if (IsSymbol(type, symbols.PacketInterface))
        {
            return true;
        }

        // Check if it implements IPacket
        foreach (INamedTypeSymbol iface in type.AllInterfaces)
        {
            if (IsSymbol(iface, symbols.PacketInterface))
            {
                return true;
            }
        }

        // Check if it inherits from PacketBase<T>
        ITypeSymbol? current = type.BaseType;
        while (current is not null)
        {
            if (current is INamedTypeSymbol namedBase
                && namedBase.IsGenericType
                && IsSymbol(namedBase.OriginalDefinition, symbols.PacketBaseType))
            {
                return true;
            }

            current = current.BaseType;
        }

        return false;
    }
}
