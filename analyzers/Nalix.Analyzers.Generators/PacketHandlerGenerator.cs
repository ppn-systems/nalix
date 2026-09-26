// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Nalix.Analyzers.Generators.Internal;

namespace Nalix.Analyzers.Generators;

/// <summary>
/// Generates zero-allocation dispatch compilers for packet controllers.
/// </summary>
[Generator]
public sealed class PacketHandlerGenerator : IIncrementalGenerator
{
    public readonly struct ScopeParameterModel : IEquatable<ScopeParameterModel>
    {
        public string Name { get; }
        public string TypeFullyQualifiedStr { get; }
        public bool IsNullable { get; }
        public bool IsFromScope { get; }

        public ScopeParameterModel(string name, string typeFullyQualifiedStr, bool isNullable, bool isFromScope)
        {
            this.Name = name;
            this.TypeFullyQualifiedStr = typeFullyQualifiedStr;
            this.IsNullable = isNullable;
            this.IsFromScope = isFromScope;
        }

        public bool Equals(ScopeParameterModel other) =>
            this.Name == other.Name &&
            this.TypeFullyQualifiedStr == other.TypeFullyQualifiedStr &&
            this.IsNullable == other.IsNullable &&
            this.IsFromScope == other.IsFromScope;

        public override bool Equals(object obj) => obj is ScopeParameterModel other && this.Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = (hash * 23) + (this.Name?.GetHashCode() ?? 0);
                hash = (hash * 23) + (this.TypeFullyQualifiedStr?.GetHashCode() ?? 0);
                hash = (hash * 23) + this.IsNullable.GetHashCode();
                hash = (hash * 23) + this.IsFromScope.GetHashCode();
                return hash;
            }
        }
    }

    public readonly struct HandlerMethodModel : IEquatable<HandlerMethodModel>
    {
        public string MethodName { get; }
        public bool IsStatic { get; }
        public ushort OpcodeVal { get; }
        public string ReturnTypeStr { get; }
        public string ExpectedPacketTypeStr { get; }
        public bool IsGenericContext { get; }
        public string? PacketTypeStr { get; }
        public string MetadataExpr { get; }
        public bool ReturnsTaskOrValueTask { get; }
        public bool ReturnsVoid { get; }
        public bool ReturnsAsyncEnumerable { get; }
        public ImmutableArray<ScopeParameterModel> ScopeParameters { get; }

        public HandlerMethodModel(
            string methodName, bool isStatic, ushort opcodeVal, string returnTypeStr,
            string expectedPacketTypeStr, bool isGenericContext, string? packetTypeStr,
            string metadataExpr, bool returnsTaskOrValueTask, bool returnsVoid,
            bool returnsAsyncEnumerable,
            ImmutableArray<ScopeParameterModel> scopeParameters)
        {
            this.MethodName = methodName;
            this.IsStatic = isStatic;
            this.OpcodeVal = opcodeVal;
            this.ReturnTypeStr = returnTypeStr;
            this.ExpectedPacketTypeStr = expectedPacketTypeStr;
            this.IsGenericContext = isGenericContext;
            this.PacketTypeStr = packetTypeStr;
            this.MetadataExpr = metadataExpr;
            this.ReturnsTaskOrValueTask = returnsTaskOrValueTask;
            this.ReturnsVoid = returnsVoid;
            this.ReturnsAsyncEnumerable = returnsAsyncEnumerable;
            this.ScopeParameters = scopeParameters;
        }

        public bool Equals(HandlerMethodModel other) =>
            this.MethodName == other.MethodName &&
            this.IsStatic == other.IsStatic &&
            this.OpcodeVal == other.OpcodeVal &&
            this.ReturnTypeStr == other.ReturnTypeStr &&
            this.ExpectedPacketTypeStr == other.ExpectedPacketTypeStr &&
            this.IsGenericContext == other.IsGenericContext &&
            this.PacketTypeStr == other.PacketTypeStr &&
            this.MetadataExpr == other.MetadataExpr &&
            this.ReturnsTaskOrValueTask == other.ReturnsTaskOrValueTask &&
            this.ReturnsVoid == other.ReturnsVoid &&
            this.ReturnsAsyncEnumerable == other.ReturnsAsyncEnumerable &&
            Internal.ModelEquality.SequenceEqual(this.ScopeParameters, other.ScopeParameters);

        public override bool Equals(object obj) => obj is HandlerMethodModel other && this.Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = (hash * 23) + (this.MethodName?.GetHashCode() ?? 0);
                hash = (hash * 23) + this.IsStatic.GetHashCode();
                hash = (hash * 23) + this.OpcodeVal.GetHashCode();
                hash = (hash * 23) + (this.ReturnTypeStr?.GetHashCode() ?? 0);
                hash = (hash * 23) + (this.ExpectedPacketTypeStr?.GetHashCode() ?? 0);
                hash = (hash * 23) + this.IsGenericContext.GetHashCode();
                hash = (hash * 23) + (this.PacketTypeStr?.GetHashCode() ?? 0);
                hash = (hash * 23) + (this.MetadataExpr?.GetHashCode() ?? 0);
                hash = (hash * 23) + this.ReturnsTaskOrValueTask.GetHashCode();
                hash = (hash * 23) + this.ReturnsVoid.GetHashCode();
                hash = (hash * 23) + this.ReturnsAsyncEnumerable.GetHashCode();
                if (!this.ScopeParameters.IsDefaultOrEmpty)
                {
                    foreach (ScopeParameterModel p in this.ScopeParameters)
                    {
                        hash = (hash * 23) + p.GetHashCode();
                    }
                }
                return hash;
            }
        }
    }

    public readonly struct ControllerModel : IEquatable<ControllerModel>
    {
        public string FullyQualifiedName { get; }
        public string Name { get; }
        public bool IsStatic { get; }
        public string NamespaceName { get; }
        public string GeneratedNamespace { get; }
        public bool HasInjectedFields { get; }
        public ImmutableArray<string> InjectedMembersAssignments { get; }
        public ImmutableArray<HandlerMethodModel> Methods { get; }
        public ImmutableArray<Diagnostic> Diagnostics { get; }

        public ControllerModel(
            string fullyQualifiedName, string name, bool isStatic, string namespaceName,
            string generatedNamespace, bool hasInjectedFields,
            ImmutableArray<string> injectedMembersAssignments,
            ImmutableArray<HandlerMethodModel> methods,
            ImmutableArray<Diagnostic> diagnostics)
        {
            this.FullyQualifiedName = fullyQualifiedName;
            this.Name = name;
            this.IsStatic = isStatic;
            this.NamespaceName = namespaceName;
            this.GeneratedNamespace = generatedNamespace;
            this.HasInjectedFields = hasInjectedFields;
            this.InjectedMembersAssignments = injectedMembersAssignments;
            this.Methods = methods;
            this.Diagnostics = diagnostics;
        }

        public bool Equals(ControllerModel other)
        {
            if (this.FullyQualifiedName != other.FullyQualifiedName ||
                this.Name != other.Name ||
                this.IsStatic != other.IsStatic ||
                this.NamespaceName != other.NamespaceName ||
                this.GeneratedNamespace != other.GeneratedNamespace ||
                this.HasInjectedFields != other.HasInjectedFields)
            {
                return false;
            }

            return Internal.ModelEquality.SequenceEqual(this.InjectedMembersAssignments, other.InjectedMembersAssignments)
                && Internal.ModelEquality.SequenceEqual(this.Methods, other.Methods)
                && Internal.ModelEquality.SequenceEqual(this.Diagnostics, other.Diagnostics);
        }

        public override bool Equals(object obj) => obj is ControllerModel other && this.Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = (hash * 23) + (this.FullyQualifiedName?.GetHashCode() ?? 0);
                hash = (hash * 23) + (this.Name?.GetHashCode() ?? 0);
                hash = (hash * 23) + this.IsStatic.GetHashCode();
                if (!this.InjectedMembersAssignments.IsDefaultOrEmpty)
                {
                    foreach (string a in this.InjectedMembersAssignments)
                    {
                        hash = (hash * 23) + (a?.GetHashCode() ?? 0);
                    }
                }

                if (!this.Methods.IsDefaultOrEmpty)
                {
                    foreach (HandlerMethodModel m in this.Methods)
                    {
                        hash = (hash * 23) + m.GetHashCode();
                    }
                }

                if (!this.Diagnostics.IsDefaultOrEmpty)
                {
                    foreach (Diagnostic d in this.Diagnostics)
                    {
                        hash = (hash * 23) + (d?.GetHashCode() ?? 0);
                    }
                }

                return hash;
            }
        }
    }

    /// <inheritdoc/>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<ControllerModel> controllers = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                KnownNames.PacketHandlerAttributeMetadataName,
                predicate: static (node, _) => node is TypeDeclarationSyntax,
                transform: static (ctx, _) => GET_CONTROLLER_MODEL(ctx))
            .Where(static m => m.FullyQualifiedName != null);

        IncrementalValueProvider<ImmutableArray<ControllerModel>> collected = controllers.Collect();

        context.RegisterSourceOutput(collected, static (spc, source) => Execute(source, spc));
    }

    private static ControllerModel GET_CONTROLLER_MODEL(GeneratorAttributeSyntaxContext context)
    {
        if (context.TargetSymbol is not INamedTypeSymbol controller)
        {
            return default;
        }

        if (controller.IsAbstract || controller.TypeKind == TypeKind.Interface || controller.IsGenericType)
        {
            return default;
        }

        string classFullName = controller.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        string name = controller.Name;
        bool isStatic = controller.IsStatic;
        string namespaceName = controller.ContainingNamespace.ToDisplayString();
        string generatedNamespace = SourceGenNamespaces.Get(controller);

        List<ISymbol> injectedMembers = [.. controller.GetMembers().Where(m => (m is IFieldSymbol || m is IPropertySymbol) && m.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == KnownNames.InjectAttributeMetadataName))];
        bool hasInjectedFields = injectedMembers.Count > 0;

        ImmutableArray<string>.Builder injectedAssignments = ImmutableArray.CreateBuilder<string>();
        foreach (ISymbol member in injectedMembers)
        {
            string typeName = member switch
            {
                IFieldSymbol f => f.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                IPropertySymbol p => p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                _ => ""
            };
            string prefix = member.IsStatic ? "" : "Instance.";
            injectedAssignments.Add($"            {prefix}{member.Name} = global::Nalix.Framework.Injection.InstanceManager.Instance.GetExistingInstance<{typeName}>()!;");
        }

        ImmutableArray<HandlerMethodModel>.Builder methods = ImmutableArray.CreateBuilder<HandlerMethodModel>();
        ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        foreach (IMethodSymbol method in controller.GetMembers().OfType<IMethodSymbol>())
        {
            if (method.DeclaredAccessibility != Accessibility.Public && method.DeclaredAccessibility != Accessibility.Internal)
            {
                continue;
            }

            AttributeData? opcodeAttr = method.GetAttributes().FirstOrDefault(a =>
                a.AttributeClass?.ToDisplayString() == KnownNames.PacketOpcodeAttributeMetadataName);

            if (opcodeAttr == null || opcodeAttr.ConstructorArguments.Length == 0)
            {
                continue;
            }

            object? val = opcodeAttr.ConstructorArguments[0].Value;
            if (val == null)
            {
                continue;
            }

            ushort opcodeVal = Convert.ToUInt16(val);

            if (method.Parameters.Length == 0)
            {
                continue;
            }

            IParameterSymbol firstParam = method.Parameters[0];
            INamedTypeSymbol? packetType = null;
            bool isGenericContext = false;

            if (firstParam.Type is INamedTypeSymbol namedParam && namedParam.IsGenericType && namedParam.TypeArguments.Length == 1)
            {
                string originalDef = namedParam.OriginalDefinition.ToDisplayString();
                if (originalDef is "Nalix.Abstractions.Networking.Packets.IPacketContext<TPacket>"
                    or "Nalix.Runtime.Dispatching.PacketContext<TPacket>")
                {
                    packetType = namedParam.TypeArguments[0] as INamedTypeSymbol;
                    isGenericContext = true;
                }
                else
                {
                    continue;
                }
            }
            else
            {
                continue;
            }

            bool allScopeParamsValid = true;
            ImmutableArray<ScopeParameterModel>.Builder scopeParams = ImmutableArray.CreateBuilder<ScopeParameterModel>();
            for (int i = 1; i < method.Parameters.Length; i++)
            {
                IParameterSymbol sp = method.Parameters[i];
                bool hasFromScope = sp.GetAttributes().Any(a =>
                    a.AttributeClass?.ToDisplayString() == KnownNames.FromScopeAttributeMetadataName
                    || a.AttributeClass?.Name is "FromScope" or "FromScopeAttribute");

                // Trailing parameters MUST have [FromScope] AND must be reference types (class constraint)
                if (!hasFromScope || !sp.Type.IsReferenceType)
                {
                    allScopeParamsValid = false;
                    diagnostics.Add(Diagnostic.Create(
                        GeneratorDiagnosticDescriptors.InvalidScopeParameter,
                        method.Locations.FirstOrDefault() ?? Location.None,
                        method.Name, sp.Name, controller.Name));
                    break;
                }

                string spTypeStr = sp.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                bool isNullable = sp.NullableAnnotation == NullableAnnotation.Annotated;

                scopeParams.Add(new ScopeParameterModel(sp.Name, spTypeStr, isNullable, hasFromScope));
            }

            if (!allScopeParamsValid)
            {
                continue;
            }

            string? pTypeStr = packetType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            string expectedPacketTypeStr = packetType != null
                ? $"typeof({pTypeStr})"
                : "null";

            string returnTypeStr = method.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            bool returnsVoid = returnTypeStr == "void";
            bool returnsTaskOrValueTask = false;

            if (method.ReturnType.Name is "ValueTask" or "Task" && method.ReturnType is INamedTypeSymbol namedRet && !namedRet.IsGenericType)
            {
                returnsTaskOrValueTask = true;
            }
            else if (method.ReturnType.Name is "ValueTask" or "Task")
            {
                // Returns Task<T> or ValueTask<T>, we must await and return result, handled dynamically later but NeedsAwait is true
                // We'll just rely on the fact it isn't void.
            }

            // #391: a handler that returns IAsyncEnumerable<T> runs its body lazily — nothing in it
            // executes until the first MoveNextAsync — so a bridge (concrete-typed) context must stay
            // rented until the whole stream is consumed or disposed, not just until this method call
            // returns the (not-yet-started) enumerable object. See PacketContextBridge.WrapStream.
            bool returnsAsyncEnumerable =
                method.ReturnType is INamedTypeSymbol namedEnumerable &&
                namedEnumerable.IsGenericType &&
                namedEnumerable.Name == "IAsyncEnumerable";

            string metadataExpr = EmitMetadataExpression(method, opcodeVal);

            methods.Add(new HandlerMethodModel(
                methodName: method.Name,
                isStatic: method.IsStatic,
                opcodeVal: opcodeVal,
                returnTypeStr: returnTypeStr,
                expectedPacketTypeStr: expectedPacketTypeStr,
                isGenericContext: isGenericContext,
                packetTypeStr: pTypeStr,
                metadataExpr: metadataExpr,
                returnsTaskOrValueTask: returnsTaskOrValueTask,
                returnsVoid: returnsVoid,
                returnsAsyncEnumerable: returnsAsyncEnumerable,
                scopeParameters: scopeParams.ToImmutable()
            ));
        }

        return new ControllerModel(
            classFullName, name, isStatic, namespaceName, generatedNamespace,
            hasInjectedFields, injectedAssignments.ToImmutable(), methods.ToImmutable(),
            diagnostics.ToImmutable());
    }

    private static void Execute(ImmutableArray<ControllerModel> targets, SourceProductionContext context)
    {
        HashSet<string> seen = new();

        List<ControllerModel> distinctControllers = [.. targets
            .Where(static p => p.FullyQualifiedName != null)
            .Where(p => seen.Add(p.FullyQualifiedName))
            .OrderBy(static p => p.FullyQualifiedName)];

        foreach (ControllerModel controller in distinctControllers)
        {
            if (controller.Diagnostics.IsDefaultOrEmpty)
            {
                continue;
            }

            foreach (Diagnostic diagnostic in controller.Diagnostics)
            {
                context.ReportDiagnostic(diagnostic);
            }
        }

        if (distinctControllers.Count == 0)
        {
            return;
        }

        string generatedNamespace = distinctControllers[0].GeneratedNamespace;

        StringBuilder sb = new();
        _ = sb.AppendLine("// <auto-generated/>");
        _ = sb.AppendLine("// Copyright (c) 2026 PPN Corporation. All rights reserved.");
        _ = sb.AppendLine("// Licensed under the Apache License, Version 2.0.");
        _ = sb.AppendLine();
        _ = sb.AppendLine("#pragma warning disable CS1591");
        _ = sb.AppendLine("#nullable enable");
        _ = sb.AppendLine();
        _ = sb.AppendLine("using System;");
        _ = sb.AppendLine("using System.Threading.Tasks;");
        _ = sb.AppendLine("using System.Runtime.CompilerServices;");
        _ = sb.AppendLine("using Nalix.Runtime.Dispatching;");
        _ = sb.AppendLine("using Nalix.Abstractions.Networking.Packets;");
        _ = sb.AppendLine();

        _ = sb.AppendLine($"namespace {generatedNamespace}.Handlers");
        _ = sb.AppendLine("{");
        _ = sb.AppendLine();

        Dictionary<string, string> compilerNames = new();
        foreach (IGrouping<string, ControllerModel>? group in distinctControllers.GroupBy(c => c.Name))
        {
            bool isCollision = group.Count() > 1;
            foreach (ControllerModel controller in group)
            {
                string suffix = isCollision ? $"_{GetDeterministicHash(controller.FullyQualifiedName)}" : "";
                compilerNames[controller.FullyQualifiedName] = $"__Compiler_{controller.Name}{suffix}";
            }
        }

        foreach (ControllerModel controller in distinctControllers)
        {
            EMIT_COMPILER(sb, controller, compilerNames[controller.FullyQualifiedName]);
            EMIT_PARTIAL_CLASS(context, controller);
        }

        _ = sb.AppendLine("}"); // End generatedNamespace
        _ = sb.AppendLine();
        _ = sb.AppendLine($"namespace {generatedNamespace}");
        _ = sb.AppendLine("{");
        _ = sb.AppendLine($"    internal static class PacketHandlerGenerated");
        _ = sb.AppendLine("    {");
        _ = sb.AppendLine("        [ModuleInitializer]");
        _ = sb.AppendLine("        internal static void Initialize()");
        _ = sb.AppendLine("        {");

        foreach (ControllerModel controller in distinctControllers)
        {
            string compilerName = compilerNames[controller.FullyQualifiedName];
            _ = sb.AppendLine($"            global::Nalix.Runtime.Dispatching.PacketHandlerRegistry.Register(typeof({controller.FullyQualifiedName}), new global::{generatedNamespace}.Handlers.{compilerName}());");
        }

        _ = sb.AppendLine("        }");
        _ = sb.AppendLine("    }");
        _ = sb.AppendLine("}");

        context.AddSource($"PacketHandlerGenerated.cs", sb.ToString());
    }

    private static void EMIT_COMPILER(StringBuilder sb, ControllerModel controller, string compilerName)
    {
        _ = sb.AppendLine($"    internal sealed class {compilerName} : global::Nalix.Runtime.Dispatching.IPacketHandlerCompiler");
        _ = sb.AppendLine("    {");
        _ = sb.AppendLine("        public void InitializeDependencies()");
        _ = sb.AppendLine("        {");
        if (controller.HasInjectedFields)
        {
            _ = sb.AppendLine($"            {controller.FullyQualifiedName}.__InitializeDependencies();");
        }
        _ = sb.AppendLine("        }");
        _ = sb.AppendLine();
        _ = sb.AppendLine("        public void Build<TPacket>(global::Nalix.Runtime.Dispatching.IPacketHandlerBuilder<TPacket> builder, global::System.Func<object> factory) where TPacket : global::Nalix.Abstractions.Networking.Packets.IPacket");
        _ = sb.AppendLine("        {");

        foreach (HandlerMethodModel method in controller.Methods)
        {
            _ = sb.AppendLine($"        builder.RegisterHandler(");
            _ = sb.AppendLine($"            opCode: {method.OpcodeVal},");
            _ = sb.AppendLine($"            metadata: {method.MetadataExpr},");
            _ = sb.AppendLine($"            methodName: \"{method.MethodName}\",");

            string instanceArg = method.IsStatic ? "null" : (controller.HasInjectedFields ? $"{controller.FullyQualifiedName}.Instance" : "factory()");
            _ = sb.AppendLine($"            instance: {instanceArg},");
            _ = sb.AppendLine($"            returnType: typeof({method.ReturnTypeStr}),");
            _ = sb.AppendLine($"            expectedPacketType: {method.ExpectedPacketTypeStr},");

            _ = sb.AppendLine($"            invoker: static async (instance, context) =>");
            _ = sb.AppendLine($"            {{");

            string instanceCast = method.IsStatic ? "" : $"(({controller.FullyQualifiedName})instance!).";
            string typeCall = method.IsStatic ? $"{controller.FullyQualifiedName}." : instanceCast;
            string contextCast;
            bool needsBridgeCleanup = false;

            // #391: an IAsyncEnumerable<T>-returning handler runs its body lazily, so a synchronous
            // try/finally around the call (the branch below) would return the bridge context to the
            // pool before the handler has read anything from it — see PacketContextBridge.WrapStream
            // for the full failure mode. Such handlers keep the context alive via WrapStream instead,
            // so they get no try/finally here at all.
            bool isBridgedStream = method.IsGenericContext && method.PacketTypeStr != null && method.ReturnsAsyncEnumerable;

            if (method.IsGenericContext && method.PacketTypeStr != null)
            {
                _ = sb.AppendLine($"                var concretePacket = ({method.PacketTypeStr})(object)context.Packet;");
                _ = sb.AppendLine($"                var concreteContext = global::Nalix.Runtime.Dispatching.PacketContextBridge.Create<{method.PacketTypeStr}, TPacket>(");
                _ = sb.AppendLine($"                    (global::Nalix.Runtime.Dispatching.PacketContext<TPacket>)context, concretePacket);");
                contextCast = "concreteContext";

                if (!isBridgedStream)
                {
                    _ = sb.AppendLine($"                try");
                    _ = sb.AppendLine($"                {{");
                    needsBridgeCleanup = true;
                }
            }
            else
            {
                contextCast = "(global::Nalix.Abstractions.Networking.Packets.IPacketContext<TPacket>)context";
            }

            List<string> callArgs = [contextCast];
            if (!method.ScopeParameters.IsDefaultOrEmpty)
            {
                for (int i = 0; i < method.ScopeParameters.Length; i++)
                {
                    ScopeParameterModel sp = method.ScopeParameters[i];
                    string argName = $"__scoped_{sp.Name}_{i}";
                    if (sp.IsNullable)
                    {
                        _ = sb.AppendLine($"                var {argName} = context.Scope.GetService<{sp.TypeFullyQualifiedStr}>();");
                    }
                    else
                    {
                        _ = sb.AppendLine($"                var {argName} = context.Scope.GetRequiredService<{sp.TypeFullyQualifiedStr}>();");
                    }
                    callArgs.Add(argName);
                }
            }

            string invocationArgs = string.Join(", ", callArgs);

            if (method.ReturnsVoid)
            {
                _ = sb.AppendLine($"                    {typeCall}{method.MethodName}({invocationArgs});");
                _ = sb.AppendLine($"                    return null;");
            }
            else if (method.ReturnsTaskOrValueTask)
            {
                _ = sb.AppendLine($"                    await {typeCall}{method.MethodName}({invocationArgs});");
                _ = sb.AppendLine($"                    return null;");
            }
            else if (isBridgedStream)
            {
                // #391: keep concreteContext rented for the whole enumeration instead of returning it
                // the instant this (lazy, not-yet-started) enumerable object is constructed.
                _ = sb.AppendLine($"                    return global::Nalix.Runtime.Dispatching.PacketContextBridge.WrapStream(");
                _ = sb.AppendLine($"                        {typeCall}{method.MethodName}({invocationArgs}), concreteContext);");
            }
            else
            {
                if (method.ReturnTypeStr.Contains("System.Threading.Tasks.Task") || method.ReturnTypeStr.Contains("System.Threading.Tasks.ValueTask"))
                {
                    _ = sb.AppendLine($"                    return await {typeCall}{method.MethodName}({invocationArgs});");
                }
                else
                {
                    _ = sb.AppendLine($"                    return {typeCall}{method.MethodName}({invocationArgs});");
                }
            }

            if (needsBridgeCleanup)
            {
                _ = sb.AppendLine($"                }}");
                _ = sb.AppendLine($"                finally");
                _ = sb.AppendLine($"                {{");
                _ = sb.AppendLine($"                    global::Nalix.Runtime.Dispatching.PacketContextBridge.Return(concreteContext);");
                _ = sb.AppendLine($"                }}");
            }

            _ = sb.AppendLine($"            }});");
        }

        _ = sb.AppendLine("    }");
        _ = sb.AppendLine("}");
        _ = sb.AppendLine();
    }

    private static void EMIT_PARTIAL_CLASS(SourceProductionContext context, ControllerModel controller)
    {
        if (!controller.HasInjectedFields)
        {
            return;
        }

        string safeName = GetSafeName(controller.FullyQualifiedName);

        StringBuilder sb = new();
        _ = sb.AppendLine("// <auto-generated/>");
        _ = sb.AppendLine("#nullable enable");
        _ = sb.AppendLine();
        _ = sb.AppendLine($"namespace {controller.NamespaceName}");
        _ = sb.AppendLine("{");
        _ = sb.AppendLine($"    partial class {controller.Name}");
        _ = sb.AppendLine("    {");
        _ = sb.AppendLine("        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]");
        _ = sb.AppendLine("        internal static void __InitializeDependencies()");
        _ = sb.AppendLine("        {");

        foreach (string assignment in controller.InjectedMembersAssignments)
        {
            _ = sb.AppendLine(assignment);
        }

        _ = sb.AppendLine("        }");

        if (!controller.IsStatic)
        {
            _ = sb.AppendLine();
            _ = sb.AppendLine($"        private static {controller.FullyQualifiedName}? __s_instance;");
            _ = sb.AppendLine($"        internal static {controller.FullyQualifiedName} Instance => __s_instance ??= new {controller.FullyQualifiedName}();");
        }

        _ = sb.AppendLine("    }");
        _ = sb.AppendLine("}");

        context.AddSource($"{safeName}_Inject.g.cs", sb.ToString());
    }

    private static string EmitMetadataExpression(IMethodSymbol method, ushort opcode)
    {
        ImmutableArray<AttributeData> attrs = method.GetAttributes();

        string tTimeout = "null";
        string tPermission = "null";
        string tEncryption = "null";
        string tRate = "null";
        string tTransport = "null";

        List<string> customAttributesList = new();

        foreach (AttributeData a in attrs)
        {
            string? name = a.AttributeClass?.ToDisplayString();
            if (name == null)
            {
                continue;
            }

            if (name == KnownNames.PacketOpcodeAttributeMetadataName ||
                name.StartsWith("System.Runtime.CompilerServices") ||
                name.StartsWith("System.Diagnostics"))
            {
                continue;
            }

            if (name == KnownNames.PacketTimeoutAttributeMetadataName && a.ConstructorArguments.Length > 0)
            {
                tTimeout = $"new global::Nalix.Abstractions.Networking.Packets.PacketTimeoutAttribute((int){a.ConstructorArguments[0].Value})";
            }
            else if (name == KnownNames.PacketPermissionAttributeMetadataName && a.ConstructorArguments.Length > 0)
            {
                tPermission = $"new global::Nalix.Abstractions.Networking.Packets.PacketPermissionAttribute((global::Nalix.Abstractions.Security.PermissionLevel){a.ConstructorArguments[0].Value})";
            }
            else if (name == KnownNames.PacketEncryptionAttributeMetadataName && a.ConstructorArguments.Length > 0)
            {
                tEncryption = $"new global::Nalix.Abstractions.Networking.Packets.PacketEncryptionAttribute({a.ConstructorArguments[0].Value?.ToString()?.ToLowerInvariant()})";
            }
            else if (name == KnownNames.PacketRateLimitAttributeMetadataName && a.ConstructorArguments.Length > 0)
            {
                int rps = (int)a.ConstructorArguments[0].Value!;
                double burst = a.ConstructorArguments.Length > 1 ? (double)a.ConstructorArguments[1].Value! : 1.0;
                tRate = $"new global::Nalix.Abstractions.Networking.Packets.PacketRateLimitAttribute({rps}, {burst})";
            }
            else if (name == KnownNames.PacketTransportAttributeMetadataName && a.ConstructorArguments.Length > 0)
            {
                tTransport = $"new global::Nalix.Abstractions.Networking.Packets.PacketTransportAttribute((global::Nalix.Abstractions.Networking.NetworkTransport){a.ConstructorArguments[0].Value})";
            }
            else
            {
                // Parse and render custom attribute
                string initCode = RenderCustomAttribute(a);
                if (!string.IsNullOrEmpty(initCode))
                {
                    customAttributesList.Add($"{{ typeof(global::{name}), {initCode} }}");
                }
            }
        }

        string customAttributesExpr = "null";
        if (customAttributesList.Count > 0)
        {
            customAttributesExpr = $@"new global::System.Collections.Generic.Dictionary<global::System.Type, global::System.Attribute>()
                {{
                    {string.Join(",\n                    ", customAttributesList)}
                }}";
        }

        return $@"new global::Nalix.Abstractions.Networking.Packets.PacketMetadata(
                opCode: new global::Nalix.Abstractions.Networking.Packets.PacketOpcodeAttribute({opcode}),
                timeout: {tTimeout},
                permission: {tPermission},
                encryption: {tEncryption},
                rateLimit: {tRate},
                transport: {tTransport},
                customAttributes: {customAttributesExpr})";
    }

    private static string RenderCustomAttribute(AttributeData attr)
    {
        if (attr.AttributeClass == null)
        {
            return "";
        }

        string typeName = attr.AttributeClass.ToDisplayString();

        List<string> ctorArgs = new();
        foreach (TypedConstant arg in attr.ConstructorArguments)
        {
            ctorArgs.Add(RenderTypedConstant(arg));
        }

        List<string> namedArgs = new();
        foreach (System.Collections.Generic.KeyValuePair<string, TypedConstant> namedArg in attr.NamedArguments)
        {
            namedArgs.Add($"{namedArg.Key} = {RenderTypedConstant(namedArg.Value)}");
        }

        string ctorString = string.Join(", ", ctorArgs);
        string initString = namedArgs.Count > 0 ? $" {{ {string.Join(", ", namedArgs)} }}" : "";

        return $"new global::{typeName}({ctorString}){initString}";
    }

    private static string RenderTypedConstant(TypedConstant constant)
    {
        if (constant.IsNull)
        {
            return "null";
        }

        if (constant.Kind == TypedConstantKind.Array)
        {
            string elementType = ((IArrayTypeSymbol)constant.Type!).ElementType.ToDisplayString();
            IEnumerable<string> elements = constant.Values.Select(RenderTypedConstant);
            return $"new global::{elementType}[] {{ {string.Join(", ", elements)} }}";
        }

        if (constant.Kind == TypedConstantKind.Enum)
        {
            return $"(global::{constant.Type!.ToDisplayString()}){constant.Value}";
        }

        if (constant.Kind == TypedConstantKind.Type)
        {
            return $"typeof(global::{((ITypeSymbol)constant.Value!).ToDisplayString()})";
        }

        if (constant.Value is string s)
        {
            return Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(s, true);
        }

        if (constant.Value is bool b)
        {
            return b ? "true" : "false";
        }

        return constant.Value?.ToString() ?? "null";
    }

    private static string GetSafeName(string typeName) => new([.. typeName.Select(c => char.IsLetterOrDigit(c) ? c : '_')]);

    private static string GetDeterministicHash(string text)
    {
        uint hash = 2166136261;
        foreach (char c in text)
        {
            hash = (hash ^ c) * 16777619;
        }
        return hash.ToString("X8");
    }
}
