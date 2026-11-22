// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Nalix.Analyzers.Diagnostics;

namespace Nalix.Analyzers.Analyzers;

// NALIX111–NALIX114: handler policy attributes ([PacketPermission], [PacketRateLimit], [PacketTimeout]).
//
// How the runtime treats them (verified in Nalix.Runtime):
//   * [PacketPermission] is enforced inline by PacketHandler<T>.CanExecute and is fail-closed: a handler
//     without it is rejected with UNAUTHORIZED.
//   * [PacketRateLimit] / [PacketTimeout] are enforced only by RateLimitMiddleware / TimeoutMiddleware,
//     which NetworkApplicationBuilder does not register on its own.
public sealed partial class NalixUsageAnalyzer
{
    private sealed class PolicySymbols
    {
        private PolicySymbols(Compilation compilation)
        {
            this.PacketPermissionAttribute = compilation.GetTypeByMetadataName("Nalix.Abstractions.Networking.Packets.PacketPermissionAttribute");
            this.PacketRateLimitAttribute = compilation.GetTypeByMetadataName("Nalix.Abstractions.Networking.Packets.PacketRateLimitAttribute");
            this.PacketTimeoutAttribute = compilation.GetTypeByMetadataName("Nalix.Abstractions.Networking.Packets.PacketTimeoutAttribute");
            this.PacketIdempotentAttribute = compilation.GetTypeByMetadataName("Nalix.Abstractions.Networking.Packets.PacketIdempotentAttribute");
            this.RateLimitMiddleware = compilation.GetTypeByMetadataName("Nalix.Runtime.Middleware.Standard.RateLimitMiddleware");
            this.TimeoutMiddleware = compilation.GetTypeByMetadataName("Nalix.Runtime.Middleware.Standard.TimeoutMiddleware");
            this.TransportSessionSubscriptions = compilation.GetTypeByMetadataName("Nalix.SDK.Transport.Extensions.TransportSessionSubscriptions");
            this.SubscriptionExtensions = compilation.GetTypeByMetadataName("Nalix.SDK.Extensions.SubscriptionExtensions");
            this.MemoryExtensions = compilation.GetTypeByMetadataName("System.MemoryExtensions");
            this.Enumerable = compilation.GetTypeByMetadataName("System.Linq.Enumerable");
        }

        public INamedTypeSymbol? PacketPermissionAttribute { get; }
        public INamedTypeSymbol? PacketRateLimitAttribute { get; }
        public INamedTypeSymbol? PacketTimeoutAttribute { get; }
        public INamedTypeSymbol? PacketIdempotentAttribute { get; }
        public INamedTypeSymbol? RateLimitMiddleware { get; }
        public INamedTypeSymbol? TimeoutMiddleware { get; }
        public INamedTypeSymbol? TransportSessionSubscriptions { get; }
        public INamedTypeSymbol? SubscriptionExtensions { get; }
        public INamedTypeSymbol? MemoryExtensions { get; }
        public INamedTypeSymbol? Enumerable { get; }

        public static PolicySymbols Create(Compilation compilation) => new(compilation);
    }

    /// <summary>Per-compilation facts gathered concurrently for NALIX114 and evaluated at compilation end.</summary>
    private sealed class PolicyUsageState
    {
        private int _hostSeen;
        private int _rateLimitMiddlewareCreated;
        private int _timeoutMiddlewareCreated;

        public ConcurrentBag<PolicyUsage> Usages { get; } = new();

        public bool HostSeen => Volatile.Read(ref _hostSeen) != 0;

        public bool RateLimitMiddlewareCreated => Volatile.Read(ref _rateLimitMiddlewareCreated) != 0;

        public bool TimeoutMiddlewareCreated => Volatile.Read(ref _timeoutMiddlewareCreated) != 0;

        public void MarkHost() => Volatile.Write(ref _hostSeen, 1);

        public void MarkRateLimitMiddleware() => Volatile.Write(ref _rateLimitMiddlewareCreated, 1);

        public void MarkTimeoutMiddleware() => Volatile.Write(ref _timeoutMiddlewareCreated, 1);
    }

    private readonly struct PolicyUsage(Location location, string attributeName, string methodName, bool isRateLimit)
    {
        public Location Location { get; } = location;
        public string AttributeName { get; } = attributeName;
        public string MethodName { get; } = methodName;
        public bool IsRateLimit { get; } = isRateLimit;
    }

    private static void RegisterPolicyAnalysis(CompilationStartAnalysisContext startContext, SymbolSet symbols)
    {
        PolicySymbols policy = PolicySymbols.Create(startContext.Compilation);

        RegisterHandlerPolicyAnalysis(startContext, symbols, policy);

        startContext.RegisterOperationAction(
            operationContext => AnalyzePolicyInvocation(operationContext, symbols, policy),
            OperationKind.Invocation);
    }

    private static void RegisterHandlerPolicyAnalysis(CompilationStartAnalysisContext startContext, SymbolSet symbols, PolicySymbols policy)
    {
        if (policy.PacketPermissionAttribute is null
            && policy.PacketRateLimitAttribute is null
            && policy.PacketTimeoutAttribute is null)
        {
            return;
        }

        PolicyUsageState state = new();

        startContext.RegisterSymbolAction(
            symbolContext => AnalyzeHandlerPolicyMethod(symbolContext, symbols, policy, state),
            SymbolKind.Method);

        // NALIX114 needs the middleware symbols; without them there is nothing to look for.
        if (policy.RateLimitMiddleware is null && policy.TimeoutMiddleware is null)
        {
            return;
        }

        startContext.RegisterOperationAction(
            operationContext => AnalyzePolicyMiddlewareCreation(operationContext, policy, state),
            OperationKind.ObjectCreation);

        startContext.RegisterOperationAction(
            operationContext => AnalyzeNetworkApplicationHost(operationContext, symbols, state),
            OperationKind.Invocation);

        startContext.RegisterCompilationEndAction(endContext => ReportMissingPolicyMiddleware(endContext, state));
    }

    // ─── NALIX111 / NALIX112 / NALIX113 ─────────────────────────────────────

    private static void AnalyzeHandlerPolicyMethod(
        SymbolAnalysisContext context,
        SymbolSet symbols,
        PolicySymbols policy,
        PolicyUsageState state)
    {
        IMethodSymbol method = (IMethodSymbol)context.Symbol;
        ImmutableArray<AttributeData> attributes = method.GetAttributes();
        if (attributes.IsEmpty)
        {
            return;
        }

        bool hasPermission = false;
        foreach (AttributeData attribute in attributes)
        {
            INamedTypeSymbol? attributeClass = attribute.AttributeClass;
            if (attributeClass is null)
            {
                continue;
            }

            if (SymbolEqualityComparer.Default.Equals(attributeClass, policy.PacketPermissionAttribute))
            {
                hasPermission = true;
            }
            else if (SymbolEqualityComparer.Default.Equals(attributeClass, policy.PacketRateLimitAttribute))
            {
                AnalyzePacketRateLimitAttribute(context, method, attribute, policy, state);
            }
            else if (SymbolEqualityComparer.Default.Equals(attributeClass, policy.PacketTimeoutAttribute))
            {
                AnalyzePacketTimeoutAttribute(context, method, attribute, policy, state);
            }
        }

        if (!hasPermission
            && policy.PacketPermissionAttribute is not null
            && IsGeneratedHandlerMethod(method, symbols))
        {
            Report(context, DiagnosticDescriptors.HandlerMissingPermission, method, method.Name);
        }
    }

    /// <summary>
    /// Mirrors the handler discovery in <c>PacketHandlerGenerator</c>: a public or internal method that carries
    /// <c>[PacketOpcode]</c> on a concrete, non-generic <c>[PacketHandler]</c> type. Handlers whose signature is
    /// already reported (NALIX003, NALIX048, NALIX058) are skipped: the generator drops or rejects them, so a
    /// missing permission is not what stops them from running.
    /// </summary>
    private static bool IsGeneratedHandlerMethod(IMethodSymbol method, SymbolSet symbols)
    {
        if (method.MethodKind != MethodKind.Ordinary
            || method.IsGenericMethod
            || method.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal)
            || !HasSupportedParameterSignature(method, symbols)
            || !HasSupportedReturnType(method.ReturnType, symbols))
        {
            return false;
        }

        INamedTypeSymbol? type = method.ContainingType;
        if (type is null || type.IsAbstract || type.IsGenericType || type.TypeKind == TypeKind.Interface)
        {
            return false;
        }

        return HasAttributeExact(method, symbols.PacketOpcodeAttribute)
               && HasAttributeExact(type, symbols.ControllerAttribute);
    }

    private static void AnalyzePacketRateLimitAttribute(
        SymbolAnalysisContext context,
        IMethodSymbol method,
        AttributeData attribute,
        PolicySymbols policy,
        PolicyUsageState state)
    {
        Location location = GetAttributeLocation(attribute, method, context.CancellationToken);
        ImmutableArray<TypedConstant> arguments = attribute.ConstructorArguments;

        if (arguments.Length > 0 && arguments[0].Value is int requestsPerSecond && requestsPerSecond <= 0)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.PacketRateLimitInvalidValue,
                location,
                method.Name,
                "RequestsPerSecond",
                requestsPerSecond.ToString(CultureInfo.InvariantCulture),
                "the limit is skipped, so this handler is not rate limited"));
        }

        if (arguments.Length > 1 && arguments[1].Value is double burst && burst <= 0)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.PacketRateLimitInvalidValue,
                location,
                method.Name,
                "Burst",
                burst.ToString(CultureInfo.InvariantCulture),
                "the limiter denies every request to this handler"));
        }

        if (policy.RateLimitMiddleware is not null)
        {
            state.Usages.Add(new PolicyUsage(location, "PacketRateLimit", method.Name, isRateLimit: true));
        }
    }

    private static void AnalyzePacketTimeoutAttribute(
        SymbolAnalysisContext context,
        IMethodSymbol method,
        AttributeData attribute,
        PolicySymbols policy,
        PolicyUsageState state)
    {
        Location location = GetAttributeLocation(attribute, method, context.CancellationToken);
        ImmutableArray<TypedConstant> arguments = attribute.ConstructorArguments;

        if (arguments.Length > 0 && arguments[0].Value is int timeoutMs && timeoutMs <= 0)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.PacketTimeoutNonPositive,
                location,
                timeoutMs.ToString(CultureInfo.InvariantCulture),
                method.Name));
        }

        if (policy.TimeoutMiddleware is not null)
        {
            state.Usages.Add(new PolicyUsage(location, "PacketTimeout", method.Name, isRateLimit: false));
        }
    }

    // ─── NALIX114 ────────────────────────────────────────────────────────────

    private static void AnalyzePolicyMiddlewareCreation(OperationAnalysisContext context, PolicySymbols policy, PolicyUsageState state)
    {
        if (context.Operation is not IObjectCreationOperation { Type: INamedTypeSymbol createdType })
        {
            return;
        }

        if (!state.RateLimitMiddlewareCreated && DerivesFromOrEquals(createdType, policy.RateLimitMiddleware))
        {
            state.MarkRateLimitMiddleware();
        }

        if (!state.TimeoutMiddlewareCreated && DerivesFromOrEquals(createdType, policy.TimeoutMiddleware))
        {
            state.MarkTimeoutMiddleware();
        }
    }

    private static void AnalyzeNetworkApplicationHost(OperationAnalysisContext context, SymbolSet symbols, PolicyUsageState state)
    {
        if (state.HostSeen)
        {
            return;
        }

        IMethodSymbol target = ((IInvocationOperation)context.Operation).TargetMethod;
        if (target.Name == "Build"
            && symbols.NetworkApplicationBuilderType is not null
            && SymbolEqualityComparer.Default.Equals(target.ContainingType, symbols.NetworkApplicationBuilderType))
        {
            state.MarkHost();
        }
    }

    private static void ReportMissingPolicyMiddleware(CompilationAnalysisContext context, PolicyUsageState state)
    {
        // Only a project that builds the application can be expected to register the middleware. A handler
        // library registers it from its host, which this compilation cannot see.
        if (!state.HostSeen)
        {
            return;
        }

        foreach (PolicyUsage usage in state.Usages)
        {
            bool registered = usage.IsRateLimit ? state.RateLimitMiddlewareCreated : state.TimeoutMiddlewareCreated;
            if (registered)
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.PolicyAttributeWithoutMiddleware,
                usage.Location,
                usage.AttributeName,
                usage.MethodName,
                usage.IsRateLimit ? "RateLimitMiddleware" : "TimeoutMiddleware"));
        }
    }

    // ─── helpers ─────────────────────────────────────────────────────────────

    private static Location GetAttributeLocation(AttributeData attribute, IMethodSymbol fallback, CancellationToken cancellationToken)
    {
        Location? location = attribute.ApplicationSyntaxReference?.GetSyntax(cancellationToken).GetLocation();
        return location ?? fallback.Locations.FirstOrDefault(static l => l.IsInSource) ?? Location.None;
    }

    private static bool HasAttributeExact(ISymbol symbol, INamedTypeSymbol? attributeSymbol)
    {
        if (attributeSymbol is null)
        {
            return false;
        }

        foreach (AttributeData attribute in symbol.GetAttributes())
        {
            if (SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, attributeSymbol))
            {
                return true;
            }
        }

        return false;
    }

    private static bool DerivesFromOrEquals(INamedTypeSymbol type, INamedTypeSymbol? baseType)
    {
        if (baseType is null)
        {
            return false;
        }

        for (INamedTypeSymbol? current = type; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, baseType))
            {
                return true;
            }
        }

        return false;
    }
}
