// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Nalix.Analyzers.Diagnostics;

namespace Nalix.Analyzers.Analyzers;

public sealed partial class NalixUsageAnalyzer
{
    private static void AnalyzeInvocation(OperationAnalysisContext context, SymbolSet symbols)
    {
        IInvocationOperation invocation = (IInvocationOperation)context.Operation;
        IMethodSymbol targetMethod = invocation.TargetMethod;

        AnalyzeRequestOptionsInvocation(context, invocation, symbols);

        if (IsSymbol(targetMethod.ContainingType, symbols.PacketRegistryFactoryType))
        {
            if (targetMethod.Name == "RegisterPacket")
            {
                AnalyzeRegisterPacketInvocation(context, invocation, symbols);
            }

            return;
        }

        if (IsSymbol(targetMethod.ContainingType, symbols.NetworkApplicationBuilderType))
        {
            if (targetMethod.Name == "Build")
            {
                AnalyzeNetworkApplicationBuildInvocation(context, invocation);
            }
            else if (targetMethod.Name == "MapHandlers" && targetMethod.TypeArguments.Length == 1)
            {
                AnalyzeNetworkApplicationTypeRegistration(context, invocation, targetMethod.TypeArguments[0], DiagnosticDescriptors.NetworkHostingHandlerTypeInvalid);
            }
            else if (targetMethod.Name == "AddMetadataProvider" && targetMethod.TypeArguments.Length == 1)
            {
                AnalyzeNetworkApplicationTypeRegistration(context, invocation, targetMethod.TypeArguments[0], DiagnosticDescriptors.NetworkHostingMetadataProviderTypeInvalid);
            }

            return;
        }

        if (!IsSymbol(targetMethod.ContainingType.OriginalDefinition, symbols.PacketDispatchOptionsType))
        {
            return;
        }

        string methodName = targetMethod.Name;
        if (methodName == "WithHandler")
        {
            AnalyzeWithHandlerInvocation(context, invocation, symbols);
        }
        else if (methodName == "WithMiddleware")
        {
            AnalyzeWithMiddlewareInvocation(context, invocation, symbols);
            AnalyzeMiddlewareRegistrationDuplicateOrder(context, invocation, symbols);
        }
        else if (methodName == "RegisterPacket")
        {
            AnalyzeRegisterPacketInvocation(context, invocation, symbols);
        }
        else if (methodName == "WithDispatchLoopCount")
        {
            AnalyzeDispatchLoopCountInvocation(context, invocation);
        }
    }

    private static void AnalyzeMiddlewareRegistrationDuplicateOrder(
        OperationAnalysisContext context,
        IInvocationOperation invocation,
        SymbolSet symbols)
    {
        if (invocation.Arguments.Length != 1)
        {
            return;
        }

        ITypeSymbol? currentType = GetUnderlyingType(invocation.Arguments[0].Value);
        if (currentType is null)
        {
            return;
        }

        int? currentOrder = GetMiddlewareOrder(currentType, symbols.MiddlewareOrderAttribute);
        if (currentOrder is null)
        {
            return;
        }

        IOperation? current = invocation.Instance;
        while (current is not null)
        {
            if (current is IInvocationOperation prevInvocation)
            {
                if (prevInvocation.TargetMethod.Name == "WithMiddleware" && prevInvocation.Arguments.Length == 1)
                {
                    ITypeSymbol? prevType = GetUnderlyingType(prevInvocation.Arguments[0].Value);
                    if (prevType is not null)
                    {
                        int? prevOrder = GetMiddlewareOrder(prevType, symbols.MiddlewareOrderAttribute);
                        if (prevOrder.HasValue)
                        {
                            if (prevOrder.Value == currentOrder.Value)
                            {
                                Report(
                                    context,
                                    DiagnosticDescriptors.MiddlewareRegistrationDuplicateOrder,
                                    invocation.Syntax.GetLocation(),
                                    currentType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                                    currentOrder.Value,
                                    prevType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
                                return;
                            }
                        }
                    }
                }
                current = prevInvocation.Instance;
            }
            else if (current is IConversionOperation conversion)
            {
                current = conversion.Operand;
            }
            else
            {
                break;
            }
        }
    }

    private static void AnalyzeRequestOptionsInvocation(OperationAnalysisContext context, IInvocationOperation invocation, SymbolSet symbols)
    {
        IMethodSymbol targetMethod = invocation.TargetMethod;

        if (IsSymbol(targetMethod.ContainingType, symbols.RequestOptionsType))
        {
            if (targetMethod.Name == "WithRetry"
                && invocation.Arguments.Length == 1
                && TryGetConstantInt(invocation.Arguments[0].Value, out int retryCount)
                && retryCount < 0)
            {
                Report(context, DiagnosticDescriptors.RequestOptionsRetryCountNegative, invocation.Syntax.GetLocation(), retryCount);
            }

            if (targetMethod.Name == "WithTimeout"
                && invocation.Arguments.Length == 1
                && TryGetConstantInt(invocation.Arguments[0].Value, out int timeoutMs)
                && timeoutMs < 0)
            {
                Report(context, DiagnosticDescriptors.RequestOptionsTimeoutNegative, invocation.Syntax.GetLocation(), timeoutMs);
            }

            return;
        }

        if (!IsSymbol(targetMethod.ContainingType, symbols.RequestExtensionsType)
            || targetMethod.Name != "RequestAsync")
        {
            return;
        }

        if (invocation.Arguments.Length < 3)
        {
            return;
        }

        IArgumentOperation optionsArgument = invocation.Arguments[2];
        if (optionsArgument.IsImplicit || IsNullLiteral(optionsArgument.Value))
        {
            return;
        }

        if (TryGetTimeoutAndRetryValues(optionsArgument.Value, symbols, out int? timeoutMsValue, out int? retryCountValue)
            && timeoutMsValue == 0
            && retryCountValue.HasValue
            && retryCountValue.Value > 0)
        {
            Report(
                context,
                DiagnosticDescriptors.RequestOptionsInfiniteTimeoutWithRetry,
                invocation.Syntax.GetLocation(),
                retryCountValue.Value);
        }

    }

    private static bool TryGetTimeoutAndRetryValues(
        IOperation operation,
        SymbolSet symbols,
        out int? timeoutMs,
        out int? retryCount)
    {
        timeoutMs = null;
        retryCount = null;

        if (operation is IPropertyReferenceOperation propertyReference
            && propertyReference.Property.Name == "Default"
            && IsSymbol(propertyReference.Member.ContainingType, symbols.RequestOptionsType))
        {
            timeoutMs = 5000;
            retryCount = 0;
            return true;
        }

        if (operation is IObjectCreationOperation creation
            && IsSymbol(creation.Type, symbols.RequestOptionsType))
        {
            timeoutMs = 5000;
            retryCount = 0;

            IEnumerable<ISimpleAssignmentOperation> assignments = creation.Initializer?.Initializers
                .OfType<ISimpleAssignmentOperation>() ?? [];

            foreach (ISimpleAssignmentOperation assignment in assignments)
            {
                if (assignment.Target is not IPropertyReferenceOperation targetProperty)
                {
                    continue;
                }

                if (targetProperty.Property.Name == "TimeoutMs"
                    && TryGetConstantInt(assignment.Value, out int timeout))
                {
                    timeoutMs = timeout;
                }
                else if (targetProperty.Property.Name == "RetryCount"
                    && TryGetConstantInt(assignment.Value, out int retry))
                {
                    retryCount = retry;
                }
            }

            return true;
        }

        if (operation is IInvocationOperation invocation
            && IsSymbol(invocation.TargetMethod.ContainingType, symbols.RequestOptionsType))
        {
            bool hasInstanceValues = invocation.Instance is not null
                && TryGetTimeoutAndRetryValues(invocation.Instance, symbols, out timeoutMs, out retryCount);

            if (!hasInstanceValues)
            {
                timeoutMs = 5000;
                retryCount = 0;
            }

            if (invocation.TargetMethod.Name == "WithTimeout"
                && invocation.Arguments.Length == 1
                && TryGetConstantInt(invocation.Arguments[0].Value, out int timeout))
            {
                timeoutMs = timeout;
            }
            else if (invocation.TargetMethod.Name == "WithRetry"
                     && invocation.Arguments.Length == 1
                     && TryGetConstantInt(invocation.Arguments[0].Value, out int retry))
            {
                retryCount = retry;
            }

            return true;
        }

        return false;
    }



    private static void AnalyzeRegisterPacketInvocation(OperationAnalysisContext context, IInvocationOperation invocation, SymbolSet symbols)
    {
        if (invocation.TargetMethod.TypeArguments.Length != 1 || invocation.TargetMethod.TypeArguments[0] is not INamedTypeSymbol packetType)
        {
            return;
        }

        AnalyzeRegisterPacketDuplicateStaticOpCode(context, invocation, packetType);

        if (!IsSymbol(invocation.TargetMethod.ContainingType, symbols.PacketRegistryFactoryType))
        {
            return;
        }

        if (packetType.IsAbstract || packetType.IsGenericType || packetType.TypeKind is not (TypeKind.Class or TypeKind.Struct))
        {
            Report(
                context,
                DiagnosticDescriptors.PacketRegistryPacketMustBeConcrete,
                invocation.Syntax.GetLocation(),
                packetType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
            return;
        }

        INamedTypeSymbol? expectedDeserializer = symbols.PacketDeserializerType?.Construct(packetType);
        bool hasDeserializer = packetType.AllInterfaces.Any(i => IsSymbol(i, expectedDeserializer));
        if (!hasDeserializer)
        {
            Report(
                context,
                DiagnosticDescriptors.PacketRegistryPacketMissingDeserializer,
                invocation.Syntax.GetLocation(),
                packetType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
        }
    }

    private static void AnalyzeRegisterPacketDuplicateStaticOpCode(
        OperationAnalysisContext context,
        IInvocationOperation invocation,
        INamedTypeSymbol currentPacketType)
    {
        if (!TryGetPacketStaticOpCode(currentPacketType, context.CancellationToken, out ushort currentOpCode, out _))
        {
            return;
        }

        IOperation? current = invocation.Instance;
        while (current is not null)
        {
            if (current is IInvocationOperation prevInvocation)
            {
                if (prevInvocation.TargetMethod.Name == "RegisterPacket"
                    && prevInvocation.TargetMethod.TypeArguments.Length == 1
                    && prevInvocation.TargetMethod.TypeArguments[0] is INamedTypeSymbol prevPacketType
                    && !IsSymbol(prevPacketType, currentPacketType)
                    && TryGetPacketStaticOpCode(prevPacketType, context.CancellationToken, out ushort prevOpCode, out _)
                    && prevOpCode == currentOpCode)
                {
                    Report(
                        context,
                        DiagnosticDescriptors.DuplicatePacketStaticOpCode,
                        invocation.Syntax.GetLocation(),
                        currentPacketType.Name,
                        currentOpCode,
                        prevPacketType.Name);
                    return;
                }

                current = prevInvocation.Instance;
            }
            else if (current is IConversionOperation conv)
            {
                current = conv.Operand;
            }
            else
            {
                break;
            }
        }
    }

    private static void AnalyzeNetworkApplicationBuildInvocation(OperationAnalysisContext context, IInvocationOperation invocation)
    {
        if (invocation.Instance is null)
        {
            return;
        }

        bool hasConnectionHub = ContainsInvocation(invocation.Instance, "UseConnectionHub");
        bool hasBufferPoolManager = ContainsInvocation(invocation.Instance, "UseBufferPoolManager");
        bool hasTcpBinding = ContainsInvocation(invocation.Instance, "ListenTcp");
        bool hasUdpBinding = ContainsInvocation(invocation.Instance, "ListenUdp");

        if (!hasBufferPoolManager)
        {
            Report(context, DiagnosticDescriptors.NetworkHostingMissingBufferPoolManager, invocation.Syntax.GetLocation());
        }

        if (!hasConnectionHub)
        {
            Report(context, DiagnosticDescriptors.NetworkHostingMissingConnectionHub, invocation.Syntax.GetLocation());
        }

        if (!hasTcpBinding)
        {
            Report(context, DiagnosticDescriptors.NetworkHostingMissingTcpBinding, invocation.Syntax.GetLocation());
        }

        if (hasUdpBinding && !hasTcpBinding)
        {
            Report(context, DiagnosticDescriptors.NetworkHostingUdpWithoutTcpBinding, invocation.Syntax.GetLocation());
        }
    }

    private static void AnalyzeNetworkApplicationTypeRegistration(
        OperationAnalysisContext context,
        IInvocationOperation invocation,
        ITypeSymbol typeArgument,
        DiagnosticDescriptor descriptor)
    {
        if (typeArgument.TypeKind != TypeKind.Class
            || typeArgument.IsAbstract
            || typeArgument is ITypeParameterSymbol)
        {
            Report(
                context,
                descriptor,
                invocation.Syntax.GetLocation(),
                typeArgument.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
        }
    }

    private static void AnalyzeDispatchLoopCountInvocation(OperationAnalysisContext context, IInvocationOperation invocation)
    {
        if (invocation.Arguments.Length != 1)
        {
            return;
        }

        if (!TryGetConstantInt(invocation.Arguments[0].Value, out int loopCount))
        {
            return;
        }

        if (loopCount < 1 || loopCount > 64)
        {
            Report(context, DiagnosticDescriptors.DispatchLoopCountOutOfRange, invocation.Syntax.GetLocation(), loopCount);
        }
    }

    private static void ReportPacketContextMismatchIfAny(SymbolAnalysisContext context, IMethodSymbol methodSymbol, SymbolSet symbols)
    {
        if (!TryGetPacketContextType(methodSymbol, symbols.PacketContextType, out ITypeSymbol? packetContextType)
            || packetContextType is null)
        {
            return;
        }

        if (methodSymbol.ContainingType is not INamedTypeSymbol controllerType)
        {
            return;
        }

        IEnumerable<ITypeSymbol> legacyPacketTypes = controllerType.GetMembers()
            .OfType<IMethodSymbol>()
            .Where(method => !IsSymbol(method, methodSymbol))
            .Where(method => HasAttribute(method, symbols.PacketOpcodeAttribute))
            .Select(method => GetLegacyPacketType(method, symbols))
            .Where(static type => type is not null)!;

        foreach (ITypeSymbol legacyPacketType in legacyPacketTypes)
        {
            if (!IsSymbol(legacyPacketType, packetContextType))
            {
                Report(
                    context,
                    DiagnosticDescriptors.PacketContextTypeMismatch,
                    methodSymbol,
                    methodSymbol.Name,
                    packetContextType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    legacyPacketType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
                break;
            }
        }
    }

    private static void AnalyzeWithMiddlewareInvocation(OperationAnalysisContext context, IInvocationOperation invocation, SymbolSet symbols)
    {
        if (invocation.Instance?.Type is not INamedTypeSymbol dispatchOptionsType || dispatchOptionsType.TypeArguments.Length != 1)
        {
            return;
        }

        ITypeSymbol dispatcherPacketType = dispatchOptionsType.TypeArguments[0];
        IArgumentOperation? middlewareArgument = invocation.Arguments.FirstOrDefault();
        if (middlewareArgument is not null && IsNullLiteral(middlewareArgument.Value))
        {
            Report(context, DiagnosticDescriptors.MiddlewareRegistrationNullLiteral, invocation.Syntax.GetLocation(), invocation.TargetMethod.Name);
            return;
        }

        ITypeSymbol? middlewareType = middlewareArgument is null ? null : GetUnderlyingType(middlewareArgument.Value);
        if (middlewareType is null)
        {
            return;
        }

        IEnumerable<INamedTypeSymbol> middlewareInterfaces = middlewareType.AllInterfaces
            .OfType<INamedTypeSymbol>()
            .Where(i => i.IsGenericType && IsSymbol(i.OriginalDefinition, symbols.PacketMiddlewareType));

        bool isCompatible = middlewareInterfaces.Any(i => IsAssignable(dispatcherPacketType, i.TypeArguments[0]));
        if (!isCompatible)
        {
            Report(
                context,
                DiagnosticDescriptors.MiddlewareTypeMismatch,
                invocation.Syntax.GetLocation(),
                middlewareType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                dispatcherPacketType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
        }
    }

    private static void AnalyzeWithHandlerInvocation(OperationAnalysisContext context, IInvocationOperation invocation, SymbolSet symbols)
    {
        if (invocation.Instance?.Type is not INamedTypeSymbol dispatchOptionsType || dispatchOptionsType.TypeArguments.Length != 1)
        {
            return;
        }

        ITypeSymbol dispatcherPacketType = dispatchOptionsType.TypeArguments[0];
        if (invocation.TargetMethod.TypeArguments.Length != 1)
        {
            return;
        }

        if (invocation.TargetMethod.TypeArguments[0] is not INamedTypeSymbol controllerType)
        {
            return;
        }

        if (!HasAttribute(controllerType, symbols.ControllerAttribute))
        {
            Report(context, DiagnosticDescriptors.ControllerMissingPacketHandlerAttribute, invocation.Syntax.GetLocation(), controllerType.Name);
            return;
        }

        foreach (IMethodSymbol handlerMethod in controllerType.GetMembers().OfType<IMethodSymbol>())
        {
            if (handlerMethod.MethodKind != MethodKind.Ordinary
                || handlerMethod.DeclaredAccessibility != Accessibility.Public
                || !HasAttribute(handlerMethod, symbols.PacketOpcodeAttribute))
            {
                continue;
            }

            ITypeSymbol? packetType = GetLegacyPacketType(handlerMethod, symbols);
            if (packetType is null)
            {
                if (TryGetPacketContextType(handlerMethod, symbols.PacketContextType, out ITypeSymbol? contextPacketType)
                    && contextPacketType is not null
                    && !IsSymbol(contextPacketType, dispatcherPacketType))
                {
                    Report(
                        context,
                        DiagnosticDescriptors.PacketContextTypeMismatch,
                        invocation.Syntax.GetLocation(),
                        handlerMethod.Name,
                        contextPacketType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                        dispatcherPacketType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
                }

                continue;
            }

            if (!IsAssignable(packetType, dispatcherPacketType))
            {
                Report(
                    context,
                    DiagnosticDescriptors.HandlerPacketTypeMismatch,
                    invocation.Syntax.GetLocation(),
                    controllerType.Name,
                    handlerMethod.Name,
                    packetType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    dispatcherPacketType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
            }
        }
    }

}
