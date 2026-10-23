// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Microsoft.CodeAnalysis;

namespace Nalix.Analyzers.Diagnostics;

internal static class DiagnosticDescriptors
{
    public static readonly DiagnosticDescriptor DuplicateControllerOpcode = new(
        id: "NALIX001",
        title: "Packet controller contains duplicate PacketOpcode",
        messageFormat: "Handler method '{0}' uses PacketOpcode 0x{1:X4}, which is already used by another handler in the same [PacketHandler]",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Nalix controller dispatch relies on unique opcodes per controller.");

    public static readonly DiagnosticDescriptor ControllerMethodRequiresOpcode = new(
        id: "NALIX002",
        title: "Packet controller handler should declare PacketOpcode",
        messageFormat: "Handler method '{0}' in a [PacketHandler] matches Nalix handler patterns and should be annotated with [PacketOpcode(...)]",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Nalix packet controller methods are only discovered when PacketOpcode is present.");

    public static readonly DiagnosticDescriptor InvalidControllerHandlerSignature = new(
        id: "NALIX003",
        title: "Packet controller handler has unsupported signature",
        messageFormat: "Handler method '{0}' has unsupported Nalix signature. Supported signatures require the first parameter to be IPacketContext<TPacket>, and any additional parameters must be annotated with [FromScope].",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Nalix packet handlers must use IPacketContext<T> as the first parameter and [FromScope] for additional parameters supported by PacketHandlerGenerator.");

    public static readonly DiagnosticDescriptor PacketContextTypeMismatch = new(
        id: "NALIX004",
        title: "PacketContext<T> does not match dispatch packet type",
        messageFormat: "Handler method '{0}' uses PacketContext<{1}>, but the dispatcher packet type is '{2}'",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Nalix dispatch expects PacketContext<T> to use the same TPacket as PacketDispatchOptions<TPacket>.");

    public static readonly DiagnosticDescriptor HandlerPacketTypeMismatch = new(
        id: "NALIX005",
        title: "Registered controller handler packet type does not match dispatcher type",
        messageFormat: "Controller '{0}' contains handler '{1}' that expects packet type '{2}', which is not assignable to dispatcher packet type '{3}'",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Nalix dispatch registration should use controllers whose handler packet types are compatible with PacketDispatchOptions<TPacket>.");

    public static readonly DiagnosticDescriptor MiddlewareTypeMismatch = new(
        id: "NALIX006",
        title: "Registered middleware type does not match dispatcher packet type",
        messageFormat: "Middleware type '{0}' is not assignable to IPacketMiddleware<{1}> for this PacketDispatchOptions<{1}>",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Nalix packet middleware should be registered against a compatible PacketDispatchOptions<TPacket>.");

    // NALIX007 — removed. NetworkBufferMiddlewareType was intentionally dropped from Nalix.

    public static readonly DiagnosticDescriptor ControllerMissingPacketHandlerAttribute = new(
        id: "NALIX008",
        title: "Registered handler controller is missing PacketHandler",
        messageFormat: "Controller type '{0}' is registered with WithHandler, but it is missing [PacketHandler]",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Nalix requires PacketHandlerAttribute on controller types registered through PacketDispatchOptions.WithHandler.");

    public static readonly DiagnosticDescriptor PacketRegistryPacketMissingDeserializer = new(
        id: "NALIX009",
        title: "Registered packet type is missing static Deserialize",
        messageFormat: "Packet type '{0}' is registered with PacketRegistryFactory.RegisterPacket, but it does not implement IPacketDeserializer<{0}>",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Nalix packet registry only binds packet types that expose the required static Deserialize(ReadOnlySpan<byte>) member.");

    public static readonly DiagnosticDescriptor PacketBaseSelfTypeMismatch = new(
        id: "NALIX010",
        title: "PacketBase<TSelf> should use the containing packet type",
        messageFormat: "Packet type '{0}' inherits from PacketBase<{1}> but should inherit from PacketBase<{0}>",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Nalix packet types should use themselves as the TSelf argument when inheriting from PacketBase<TSelf>.");

    public static readonly DiagnosticDescriptor PacketDeserializerSelfTypeMismatch = new(
        id: "NALIX011",
        title: "IPacketDeserializer<T> should use the containing packet type",
        messageFormat: "Packet type '{0}' implements IPacketDeserializer<{1}> but should implement IPacketDeserializer<{0}>",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Nalix packet deserializer contracts should target the containing packet type for consistent registry binding.");

    public static readonly DiagnosticDescriptor PacketBaseMissingDeserializeMethod = new(
        id: "NALIX012",
        title: "PacketBase packet should expose static Deserialize",
        messageFormat: "Packet type '{0}' inherits from PacketBase<{0}> but no accessible 'public static {0} Deserialize(ReadOnlySpan<byte>)' was found in its inheritance chain",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Nalix packet types built on PacketBase<TSelf> should have an accessible static Deserialize(ReadOnlySpan<byte>) entry point, either declared on the type or inherited from its packet base hierarchy.");

    public static readonly DiagnosticDescriptor ExplicitSerializationMemberMissingOrder = new(
        id: "NALIX013",
        title: "Explicit serialization member should declare SerializeOrder",
        messageFormat: "Member '{0}' belongs to a [SerializePackable(SerializeLayout.Explicit)] type and should declare [SerializeOrder(...)] or [SerializeIgnore]",
        category: "Serialization",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Explicit serialization layouts should assign a stable SerializeOrder to every serialized member.");

    public static readonly DiagnosticDescriptor DuplicateSerializeOrder = new(
        id: "NALIX014",
        title: "SerializeOrder value is duplicated",
        messageFormat: "Member '{0}' uses SerializeOrder {1}, which is already used by another member in type '{2}'",
        category: "Serialization",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "SerializeOrder values should be unique within a serializable type.");

    public static readonly DiagnosticDescriptor SerializeIgnoreConflictsWithOrder = new(
        id: "NALIX015",
        title: "SerializeIgnore conflicts with SerializeOrder",
        messageFormat: "Member '{0}' declares both [SerializeIgnore] and [SerializeOrder(...)]",
        category: "Serialization",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A member cannot be both ignored and explicitly ordered for serialization.");

    public static readonly DiagnosticDescriptor SerializeDynamicSizeOnFixedMember = new(
        id: "NALIX016",
        title: "SerializeDynamicSize is unnecessary on fixed-size member",
        messageFormat: "Member '{0}' uses [SerializeDynamicSize] but type '{1}' is fixed-size",
        category: "Serialization",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "SerializeDynamicSize should be used only for variable-size serialization members such as strings, arrays, or variable-size collections.");

    public static readonly DiagnosticDescriptor PacketDeserializeSignatureInvalid = new(
        id: "NALIX017",
        title: "Packet Deserialize signature is invalid",
        messageFormat: "Packet type '{0}' exposes Deserialize, but it should be 'public static new {0} Deserialize(ReadOnlySpan<byte>)'",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Nalix packet types should expose a public static Deserialize(ReadOnlySpan<byte>) helper with the packet type as return value.");

    public static readonly DiagnosticDescriptor PacketRegistryPacketMustBeConcrete = new(
        id: "NALIX018",
        title: "Registered packet type must be concrete",
        messageFormat: "Packet type '{0}' is registered with PacketRegistryFactory.RegisterPacket, but it is not a concrete non-generic type",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Nalix packet registry should register only concrete, non-abstract, non-generic packet types.");

    // NALIX019 — removed. NetworkBufferMiddlewareType was intentionally dropped from Nalix.

    public static readonly DiagnosticDescriptor ResetForPoolShouldCallBase = new(
        id: "NALIX020",
        title: "Packet ResetForPool should call base.ResetForPool",
        messageFormat: "Packet type '{0}' overrides ResetForPool but does not call base.ResetForPool()",
        category: "Lifecycle",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Nalix packets derived from PacketBase<TSelf> should call base.ResetForPool() to restore header fields and default metadata.");

    public static readonly DiagnosticDescriptor NegativeSerializeOrder = new(
        id: "NALIX021",
        title: "SerializeOrder should not be negative",
        messageFormat: "Member '{0}' uses negative SerializeOrder {1}",
        category: "Serialization",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Nalix explicit serialization layout should not use negative SerializeOrder values.");

    public static readonly DiagnosticDescriptor ReservedPacketHeaderSlot = new(
        id: "NALIX022",
        title: "SerializeHeader(0) is reserved on PacketBase types",
        messageFormat: "Member '{0}' uses [SerializeHeader(0)] on a PacketBase-derived type, but header slot 0 is reserved by Nalix packet internals",
        category: "Serialization",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "On PacketBase<TSelf>-derived types, SerializeHeader(0) is reserved by the Nalix packet header. Use a non-zero header order or SerializeOrder for user-defined members.");

    public static readonly DiagnosticDescriptor UnsupportedConfigurationPropertyType = new(
        id: "NALIX023",
        title: "Configuration property type is not supported",
        messageFormat: "Property '{0}' on ConfigurationLoader type '{1}' uses unsupported configuration type '{2}'. Supported types: primitives, string, DateTime, TimeSpan, Guid, and enums. Add [ConfiguredIgnore] to skip it.",
        category: "Configuration",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Nalix ConfigurationLoader binds only supported scalar, Guid, TimeSpan, DateTime, and enum property types. Unsupported properties should be marked with ConfiguredIgnoreAttribute.");

    public static readonly DiagnosticDescriptor ConfigurationPropertyNotBindable = new(
        id: "NALIX024",
        title: "Configuration property is not bindable",
        messageFormat: "Property '{0}' on ConfigurationLoader type '{1}' will not be bound because it {2}. Make the setter public or add [ConfiguredIgnore] to document the intent.",
        category: "Configuration",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Nalix ConfigurationLoader binds only public instance properties with public setters. Non-bindable properties should either expose a public setter or be marked with ConfiguredIgnoreAttribute.");

    public static readonly DiagnosticDescriptor MetadataProviderClearsOpcode = new(
        id: "NALIX025",
        title: "Packet metadata provider clears Opcode",
        messageFormat: "Metadata provider '{0}' assigns builder.Opcode = null inside Populate(...), which will make PacketMetadataBuilder.Build() fail",
        category: "Routing",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Nalix packet metadata providers should not clear builder.Opcode because packet metadata requires a non-null opcode.");

    public static readonly DiagnosticDescriptor MetadataProviderOverwritesOpcodeWithoutGuard = new(
        id: "NALIX026",
        title: "Packet metadata provider overwrites Opcode without guard",
        messageFormat: "Metadata provider '{0}' assigns builder.Opcode in Populate(...) without checking the current value first",
        category: "Routing",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Nalix packet metadata providers usually should augment metadata instead of unconditionally replacing an existing PacketOpcodeAttribute.");

    public static readonly DiagnosticDescriptor RequestOptionsRetryCountNegative = new(
        id: "NALIX027",
        title: "RequestOptions RetryCount should not be negative",
        messageFormat: "RequestOptions RetryCount is set to negative value {0}",
        category: "SDK",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Nalix RequestOptions retry count must be zero or greater.");

    public static readonly DiagnosticDescriptor RequestOptionsTimeoutNegative = new(
        id: "NALIX028",
        title: "RequestOptions TimeoutMs should not be negative",
        messageFormat: "RequestOptions TimeoutMs is set to negative value {0}",
        category: "SDK",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Nalix RequestOptions timeout must be zero or greater. Use 0 to wait indefinitely.");

    public static readonly DiagnosticDescriptor PacketMiddlewareMissingOrder = new(
        id: "NALIX030",
        title: "Packet middleware should declare MiddlewareOrder",
        messageFormat: "Packet middleware type '{0}' does not declare [MiddlewareOrder(...)]",
        category: "Middleware",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Nalix packet middleware ordering is more predictable when each middleware declares MiddlewareOrderAttribute explicitly.");

    // NALIX031 — removed. NetworkBufferMiddlewareType was intentionally dropped from Nalix.

    public static readonly DiagnosticDescriptor InboundMiddlewareAlwaysExecuteIgnored = new(
        id: "NALIX032",
        title: "Inbound middleware ignores AlwaysExecute",
        messageFormat: "Middleware type '{0}' sets MiddlewareStage(Inbound, AlwaysExecute = true), but AlwaysExecute only affects outbound execution",
        category: "Middleware",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Nalix AlwaysExecute only changes outbound middleware behavior. It has no effect on inbound-only middleware.");

    public static readonly DiagnosticDescriptor MiddlewareRegistrationDuplicateOrder = new(
        id: "NALIX033",
        title: "Registered middleware shares MiddlewareOrder with another middleware in the same chain",
        messageFormat: "Middleware type '{0}' uses MiddlewareOrder {1}, which is already used by '{2}' in the same registration chain",
        category: "Middleware",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Nalix middleware registration chains are easier to reason about when MiddlewareOrder values are unique within the same builder chain.");

    public static readonly DiagnosticDescriptor SerializeHeaderConflictsWithOrder = new(
        id: "NALIX034",
        title: "SerializeHeader conflicts with SerializeOrder",
        messageFormat: "Member '{0}' declares both [SerializeHeader(...)] and [SerializeOrder(...)]",
        category: "Serialization",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Nalix serialization members should not declare both SerializeHeader and SerializeOrder. SerializeHeader is a specialized form of ordering for header fields.");

    public static readonly DiagnosticDescriptor ReservedOpCodeRange = new(
        id: "NALIX035",
        title: "PacketOpcode is in reserved range",
        messageFormat: "Handler method '{0}' uses OpCode 0x{1:X4}, which is in the reserved system range (0x0000 - 0x00FF)",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "OpCodes between 0x0000 and 0x00FF are reserved for internal Nalix system packets and should not be used for application handlers.");

    public static readonly DiagnosticDescriptor GlobalDuplicateOpcode = new(
        id: "NALIX036",
        title: "PacketOpcode is already used in a different controller",
        messageFormat: "Handler method '{0}' uses OpCode 0x{1:X4}, which is already defined by '{2}' in controller '{3}'",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Nalix dispatch requires unique opcodes across all registered controllers in a compilation unit.");

    public static readonly DiagnosticDescriptor AllocationInHotPath = new(
        id: "NALIX037",
        title: "Potential allocation in hot path",
        messageFormat: "Method '{0}' is a high-frequency Nalix hot path. Allocating '{1}' via 'new' may impact performance; consider using ObjectPoolManager or recycling instances.",
        category: "Performance",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Nalix middleware and handlers are executed frequently. Avoiding allocations in these paths is critical for low-latency networking.");

    public static readonly DiagnosticDescriptor OpCodeDocMismatch = new(
        id: "NALIX038",
        title: "OpCode in documentation does not match attribute",
        messageFormat: "Documentation for '{0}' mentions OpCode 0x{1:X4}, but the attribute value is 0x{2:X4}",
        category: "Documentation",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "XML documentation summaries should be synchronized with PacketOpcode values for clarity and automated protocol documentation.");

    public static readonly DiagnosticDescriptor PotentialBufferLeaseLeak = new(
        id: "NALIX039",
        title: "Potential IBufferLease leak",
        messageFormat: "Local variable or parameter '{0}' of type IBufferLease might be leaked; ensure it is disposed of exactly once on all code paths",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "IBufferLease represents a pooled resource that must be returned to the pool exactly once via Dispose() or an explicit return call.");

    public static readonly DiagnosticDescriptor NetworkHostingMissingBufferPoolManager = new(
        id: "NALIX040",
        title: "NetworkApplicationBuilder should configure BufferPoolManager",
        messageFormat: "NetworkApplicationBuilder.Build() is called without UseBufferPoolManager(...); configuring a BufferPoolManager can improve network allocation performance",
        category: "Performance",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Nalix network hosting can reduce allocation pressure by registering an explicit BufferPoolManager before building the application.");

    public static readonly DiagnosticDescriptor NetworkHostingMissingConnectionHub = new(
        id: "NALIX041",
        title: "NetworkApplicationBuilder should configure ConnectionHub",
        messageFormat: "NetworkApplicationBuilder.Build() is called without UseConnectionHub(...); configuring a ConnectionHub makes host wiring explicit",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Nalix network hosting can use an explicitly configured ConnectionHub instead of relying on the default fallback instance.");

    public static readonly DiagnosticDescriptor NetworkHostingHandlerTypeInvalid = new(
        id: "NALIX042",
        title: "NetworkApplicationBuilder handler type is not constructible",
        messageFormat: "MapHandlers<{0}>() expects a concrete, non-abstract, non-open-generic class type",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Nalix handler registration creates instances at runtime, so the handler type should be a concrete class.");

    public static readonly DiagnosticDescriptor NetworkHostingMetadataProviderTypeInvalid = new(
        id: "NALIX043",
        title: "NetworkApplicationBuilder metadata provider type is not constructible",
        messageFormat: "AddMetadataProvider<{0}>() expects a concrete, non-abstract, non-open-generic class type",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Nalix metadata provider registration creates instances at runtime, so the provider type should be a concrete class.");

    public static readonly DiagnosticDescriptor NetworkHostingMissingTcpBinding = new(
        id: "NALIX044",
        title: "NetworkApplicationBuilder should configure a TCP binding",
        messageFormat: "NetworkApplicationBuilder.Build() is called without ListenTcp(...); a host without TCP bindings may be incomplete",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Nalix network hosting usually needs at least one TCP binding to serve clients.");

    public static readonly DiagnosticDescriptor NetworkHostingUdpWithoutTcpBinding = new(
        id: "NALIX045",
        title: "NetworkApplicationBuilder should configure TCP before UDP",
        messageFormat: "NetworkApplicationBuilder.Build() configures UDP bindings without any ListenTcp(...); UDP bindings are expected to run alongside TCP in this host setup",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Nalix network hosting in this package expects UDP bindings to be paired with TCP bindings.");

    public static readonly DiagnosticDescriptor UnusuallyLargeSerializeOrderGap = new(
        id: "NALIX046",
        title: "SerializeOrder gap is unusually large",
        messageFormat: "Member '{0}' uses SerializeOrder {1}, which leaves a large gap from previous SerializeOrder {2}",
        category: "Serialization",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "SerializeOrder defines member ordering, not byte offsets. Large jumps are allowed, but may indicate accidental numbering.");

    public static readonly DiagnosticDescriptor DispatchLoopCountOutOfRange = new(
        id: "NALIX047",
        title: "Dispatch loop count is out of supported range",
        messageFormat: "WithDispatchLoopCount({0}) is outside supported range [1..64]",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Nalix PacketDispatchOptions.WithDispatchLoopCount expects values between 1 and 64.");

    public static readonly DiagnosticDescriptor UnsupportedControllerHandlerReturnType = new(
        id: "NALIX048",
        title: "Packet controller handler return type is unsupported",
        messageFormat: "Handler method '{0}' returns '{1}', which is not supported by Nalix return handlers",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Nalix packet handlers should return void, Task, ValueTask, IPacket, byte[], Memory<byte>, ReadOnlyMemory<byte>, or Task/ValueTask of supported result types.");

    public static readonly DiagnosticDescriptor PacketOpcodeOnNonControllerType = new(
        id: "NALIX050",
        title: "PacketOpcode is declared on a non-controller type",
        messageFormat: "Method '{0}' declares [PacketOpcode], but containing type '{1}' is missing [PacketHandler]",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "PacketOpcode handlers are expected to be declared inside PacketHandler types.");

    public static readonly DiagnosticDescriptor FixedSizeSerializableHasDynamicMember = new(
        id: "NALIX051",
        title: "IFixedSizeSerializable type contains dynamic serialization member",
        messageFormat: "Type '{0}' implements IFixedSizeSerializable but member '{1}' has dynamic size type '{2}'",
        category: "Serialization",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Fixed-size serializable types should avoid variable-sized members such as string, arrays, or nested packets.");

    public static readonly DiagnosticDescriptor PacketDeserializeSpanOverloadMissing = new(
        id: "NALIX052",
        title: "Packet Deserialize overload should include ReadOnlySpan<byte>",
        messageFormat: "Packet type '{0}' declares Deserialize overloads but is missing 'public static {0} Deserialize(ReadOnlySpan<byte>)'",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Nalix packet discovery and registry binding expect a ReadOnlySpan<byte> Deserialize entry point.");

    public static readonly DiagnosticDescriptor DuplicatePacketHandlerName = new(
        id: "NALIX054",
        title: "PacketHandler name is duplicated",
        messageFormat: "PacketHandler name '{0}' is used by both '{1}' and '{2}'",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Duplicate PacketHandler names reduce routing and diagnostics clarity.");

    public static readonly DiagnosticDescriptor RedundantPacketContextPacketCast = new(
        id: "NALIX055",
        title: "Redundant cast on PacketContext<T>.Packet",
        messageFormat: "Handler method '{0}' already uses PacketContext<{1}>; casting '{2}.Packet' to '{1}' is redundant",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "PacketContext<T> already provides a strongly-typed Packet value; additional casts to T are unnecessary.");

    public static readonly DiagnosticDescriptor MiddlewareRegistrationNullLiteral = new(
        id: "NALIX056",
        title: "Middleware registration uses null",
        messageFormat: "Call to '{0}' passes null middleware and will throw at runtime",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "PacketDispatchOptions middleware registration requires non-null middleware instances.");

    public static readonly DiagnosticDescriptor RequestOptionsInfiniteTimeoutWithRetry = new(
        id: "NALIX057",
        title: "RequestOptions uses infinite timeout with retries",
        messageFormat: "RequestOptions sets TimeoutMs=0 with RetryCount={0}; retries are typically ineffective with infinite timeout",
        category: "SDK",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "With TimeoutMs=0 each attempt can wait indefinitely, so RetryCount usually has no practical effect.");

    public static readonly DiagnosticDescriptor GenericPacketHandlerMethod = new(
        id: "NALIX058",
        title: "Packet handler method should not be generic",
        messageFormat: "Handler method '{0}' declares generic type parameters, which is not recommended for PacketOpcode dispatch",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Nalix packet dispatch expects concrete handler signatures; generic handler methods can lead to ambiguous or unsupported binding.");

    public static readonly DiagnosticDescriptor UnguardedCatchException = new(
        id: "NALIX073",
        title: "catch(Exception) should filter through ExceptionClassifier.IsNonFatal()",
        messageFormat: "Catch clause catches System.Exception without an ExceptionClassifier.IsNonFatal() filter; fatal exceptions may be swallowed",
        category: "Correctness",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Nalix core code should use 'catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))' to avoid swallowing fatal runtime exceptions like OutOfMemoryException or StackOverflowException.");

    public static readonly DiagnosticDescriptor DisallowedCryptographyUsage = new(
        id: "NALIX071",
        title: "Use Nalix crypto abstractions instead of System.Security.Cryptography",
        messageFormat: "Usage of '{0}' from System.Security.Cryptography is not allowed in Nalix Core assemblies. Use Nalix internal crypto abstractions instead.",
        category: "Security",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Nalix Core assemblies should centralize cryptographic behavior behind Nalix-owned abstractions. Direct BCL crypto usage can bypass security review, create platform inconsistencies, and complicate Native AOT compatibility. Approved platform fallback shims are allowlisted.");

    public static readonly DiagnosticDescriptor AllocatingEndpointFormatting = new(
        id: "NALIX072",
        title: "Use zero-allocation endpoint formatting in hot paths",
        messageFormat: "Usage of '{0}' allocates in networking hot paths. Use TryFormatAddress or other zero-allocation formatting APIs instead.",
        category: "Performance",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "IPAddress.ToString(), INetworkEndpoint.Address, and SocketEndpoint.Address allocate strings on every call. In networking hot paths, use SocketEndpoint.TryFormatAddress or Span-based formatting to avoid GC pressure.");

    public static readonly DiagnosticDescriptor PacketScopeNotDisposed = new(
        id: "NALIX075",
        title: "PacketScope<T> must be disposed",
        messageFormat: "Local variable '{0}' of type PacketScope<{1}> is not declared with 'using'; the pooled packet will not be returned",
        category: "Pooling",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "PacketScope<T> wraps a pooled packet and must be disposed via 'using' to return it to the pool. Forgetting 'using' causes pool leaks.");

    public static readonly DiagnosticDescriptor UnboundedReflectionInAotCode = new(
        id: "NALIX078",
        title: "Avoid unbounded reflection in AOT-sensitive Nalix Core code",
        messageFormat: "Usage of '{0}' introduces unbounded reflection that may not be compatible with Native AOT trimming. Use source-generated or compile-time alternatives instead.",
        category: "AOT",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Nalix Core assemblies targeting Native AOT should avoid runtime assembly scanning, dynamic code generation, and unannotated reflection. Use source-generated activators, compile-time registration, or DynamicallyAccessedMembers annotations.");

    public static readonly DiagnosticDescriptor EagerStringFormattingInDiagnosticLog = new(
        id: "NALIX074",
        title: "Avoid eager string formatting in diagnostic logging",
        messageFormat: "Eager string formatting in DiagnosticLog construction allocates even when diagnostics are disabled. Use constant strings or move formatting behind an IsEnabled check.",
        category: "Performance",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "DiagnosticLog message arguments are eagerly evaluated. Interpolated strings, string.Format, or concatenation inside new DiagnosticLog(...) allocate even when the diagnostic event is disabled. In hot paths, prefer constant strings or guard formatting behind DiagnosticsEvents.Source.IsEnabled(...).");

    public static readonly DiagnosticDescriptor PacketContextEscapesHandlerScope = new(
        id: "NALIX076",
        title: "Packet context must not escape handler scope",
        messageFormat: "Pooled {0} must not escape the handler scope: {1}",
        category: "Correctness",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "IPacketContext<T> and its Packet are pooled and returned after the handler completes. Capturing them in a field, long-lived delegate, or offloaded task causes use-after-return bugs, including cross-packet/tenant data leaks. Extract needed data into locals before offloading work.");

    public static readonly DiagnosticDescriptor RpcServiceInvalidReturnType = new(
        id: "NALIX100",
        title: "RpcService method has invalid return type",
        messageFormat: "RPC method '{0}' must return an awaitable type (ValueTask, RpcCall<T>, or RpcStream<T>)",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Nalix RpcService methods must return one of the supported Typed-RPC struct types to ensure zero-allocation execution.");

    public static readonly DiagnosticDescriptor RpcServiceInvalidParameters = new(
        id: "NALIX101",
        title: "RpcService method has invalid parameters",
        messageFormat: "Method '{0}' has invalid parameters. The first parameter must be an IPacket. Subsequent parameters can only be RequestOptions, Func<TResponse, bool>, bool?, or CancellationToken.",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Nalix RpcService methods have strict parameter requirements to match the underlying transport session methods.");

    public static readonly DiagnosticDescriptor RpcServiceContainsInvalidMembers = new(
        id: "NALIX102",
        title: "RpcService interface contains non-method members",
        messageFormat: "Interface '{0}' is marked with [RpcService] but contains member '{1}' which is not a method",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Nalix RpcService interfaces must only contain methods.");

    public static readonly DiagnosticDescriptor RpcServiceMissingAttribute = new(
        id: "NALIX103",
        title: "RpcService interface is missing attribute",
        messageFormat: "Type '{0}' is missing the [RpcService] attribute",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Nalix RpcService interfaces must be decorated with [RpcService] to be discovered by the Source Generator.");

    public static readonly DiagnosticDescriptor InjectFieldCannotBeReadOnly = new(
        id: "NALIX104",
        title: "[Inject] field cannot be readonly",
        messageFormat: "Field '{0}' is marked with [Inject] and cannot be readonly, because the dependency injection container must be able to assign it",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Fields annotated with [Inject] cannot be readonly since they are assigned via reflection or source generation after object instantiation.");

    public static readonly DiagnosticDescriptor RequiredSymbolUnresolved = new(
        id: "NALIX105",
        title: "Nalix analyzer disabled: required symbol could not be resolved",
        messageFormat: "The Nalix analyzer found no diagnostics for this compilation because the required symbol '{0}' could not be resolved. Verify the project references the Nalix package that declares it.",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "When a mandatory Nalix symbol cannot be resolved (e.g. a missing package reference), the entire analyzer silently reports nothing. This diagnostic surfaces that condition instead of leaving it indistinguishable from 'no issues found'.",
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    public static readonly DiagnosticDescriptor DuplicatePacketStaticOpCode = new(
        id: "NALIX106",
        title: "Packet StaticOpCode is already used by another packet",
        messageFormat: "Packet type '{0}' declares StaticOpCode 0x{1:X4} ({1}), which is already registered by packet '{2}'",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Each registered packet type in a compilation must declare a unique ushort StaticOpCode to prevent opcode collisions in PacketRegistry.");

    public static readonly DiagnosticDescriptor PacketResetForPoolMissingMemberReset = new(
        id: "NALIX107",
        title: "Packet ResetForPool does not reset mutable member",
        messageFormat: "Packet type '{0}' overrides ResetForPool() but does not reset mutable member '{1}', which may cause cross-request state bleed",
        category: "Lifecycle",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Manual implementations of ResetForPool() on packet types must reset all mutable instance fields and auto-properties (unless annotated with [SkipClean]) to prevent cross-request data leaks when instances are reused from the object pool.");
}
