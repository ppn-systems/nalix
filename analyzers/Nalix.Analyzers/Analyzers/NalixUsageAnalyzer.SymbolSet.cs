// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;

namespace Nalix.Analyzers.Analyzers;

public sealed partial class NalixUsageAnalyzer
{
    private sealed class SymbolSet
    {
        private SymbolSet(
            INamedTypeSymbol? packetOpcodeAttribute,
            INamedTypeSymbol? controllerAttribute,
            INamedTypeSymbol? packetInterface,
            INamedTypeSymbol? packetBaseType,
            INamedTypeSymbol? serializePackableAttribute,
            INamedTypeSymbol? serializeHeaderAttribute,
            INamedTypeSymbol? serializeOrderAttribute,
            INamedTypeSymbol? serializeIgnoreAttribute,
            INamedTypeSymbol? serializeDynamicSizeAttribute,
            INamedTypeSymbol? serializeLayoutType,
            INamedTypeSymbol? packetContextType,
            INamedTypeSymbol? packetContextInterface,
            INamedTypeSymbol? connectionType,
            INamedTypeSymbol? packetDispatchOptionsType,
            INamedTypeSymbol? packetRegistryFactoryType,
            INamedTypeSymbol? packetDeserializerType,
            INamedTypeSymbol? packetMiddlewareType,
            INamedTypeSymbol? networkApplicationBuilderType,
            INamedTypeSymbol? middlewareOrderAttribute,
            INamedTypeSymbol? middlewareStageAttribute,
            INamedTypeSymbol? middlewareStageType,
            INamedTypeSymbol? configurationLoaderType,
            INamedTypeSymbol? configurationIgnoreAttribute,
            INamedTypeSymbol? reservedOpcodePermittedAttribute,
            INamedTypeSymbol? packetMetadataProviderType,
            INamedTypeSymbol? packetMetadataBuilderType,
            INamedTypeSymbol? methodInfoType,
            INamedTypeSymbol? requestOptionsType,
            INamedTypeSymbol? requestExtensionsType,
            INamedTypeSymbol? taskType,
            INamedTypeSymbol? genericTaskType,
            INamedTypeSymbol? valueTaskType,
            INamedTypeSymbol? genericValueTaskType,
            INamedTypeSymbol? cancellationTokenType,
            INamedTypeSymbol? readOnlyMemoryByteType,
            INamedTypeSymbol? memoryByteType,
            INamedTypeSymbol? fixedSizeSerializableType,
            INamedTypeSymbol? packetDispatchType,
            INamedTypeSymbol? bufferLeaseType,
            INamedTypeSymbol? exceptionClassifierType,
            INamedTypeSymbol? packetScopeType,
            INamedTypeSymbol? caughtExceptionType,
            INamedTypeSymbol? fromScopeAttribute,
            INamedTypeSymbol? rpcServiceAttribute,
            INamedTypeSymbol? rpcCallType,
            INamedTypeSymbol? rpcStreamType,
            INamedTypeSymbol? packetAttribute,
            INamedTypeSymbol? skipCleanAttribute,
            INamedTypeSymbol? injectAttribute,
            INamedTypeSymbol? poolableInterface,
            int packetHeaderRegionOffset)
        {
            this.PacketOpcodeAttribute = packetOpcodeAttribute;
            this.ControllerAttribute = controllerAttribute;
            this.PacketInterface = packetInterface;
            this.PacketBaseType = packetBaseType;
            this.SerializePackableAttribute = serializePackableAttribute;
            this.SerializeHeaderAttribute = serializeHeaderAttribute;
            this.SerializeOrderAttribute = serializeOrderAttribute;
            this.SerializeIgnoreAttribute = serializeIgnoreAttribute;
            this.SerializeDynamicSizeAttribute = serializeDynamicSizeAttribute;
            this.SerializeLayoutType = serializeLayoutType;
            this.PacketContextType = packetContextType;
            this.PacketContextInterface = packetContextInterface;
            this.ConnectionType = connectionType;
            this.PacketDispatchOptionsType = packetDispatchOptionsType;
            this.PacketRegistryFactoryType = packetRegistryFactoryType;
            this.PacketDeserializerType = packetDeserializerType;
            this.PacketMiddlewareType = packetMiddlewareType;
            this.NetworkApplicationBuilderType = networkApplicationBuilderType;
            this.MiddlewareOrderAttribute = middlewareOrderAttribute;
            this.MiddlewareStageAttribute = middlewareStageAttribute;
            this.MiddlewareStageType = middlewareStageType;
            this.ConfigurationLoaderType = configurationLoaderType;
            this.ConfigurationIgnoreAttribute = configurationIgnoreAttribute;
            this.ReservedOpcodePermittedAttribute = reservedOpcodePermittedAttribute;
            this.PacketMetadataProviderType = packetMetadataProviderType;
            this.PacketMetadataBuilderType = packetMetadataBuilderType;
            this.MethodInfoType = methodInfoType;
            this.RequestOptionsType = requestOptionsType;
            this.RequestExtensionsType = requestExtensionsType;
            this.TaskType = taskType;
            this.GenericTaskType = genericTaskType;
            this.ValueTaskType = valueTaskType;
            this.GenericValueTaskType = genericValueTaskType;
            this.CancellationTokenType = cancellationTokenType;
            this.ReadOnlyMemoryByteType = readOnlyMemoryByteType;
            this.MemoryByteType = memoryByteType;
            this.FixedSizeSerializableType = fixedSizeSerializableType;
            this.PacketDispatchType = packetDispatchType;
            this.BufferLeaseType = bufferLeaseType;
            this.ExceptionClassifierType = exceptionClassifierType;
            this.PacketScopeType = packetScopeType;
            this.CaughtExceptionType = caughtExceptionType;
            this.FromScopeAttribute = fromScopeAttribute;
            this.RpcServiceAttribute = rpcServiceAttribute;
            this.RpcCallType = rpcCallType;
            this.RpcStreamType = rpcStreamType;
            this.PacketAttribute = packetAttribute;
            this.SkipCleanAttribute = skipCleanAttribute;
            this.InjectAttribute = injectAttribute;
            this.PoolableInterface = poolableInterface;
            this.PacketHeaderRegionOffset = packetHeaderRegionOffset;
        }

        public INamedTypeSymbol? PacketOpcodeAttribute { get; }
        public INamedTypeSymbol? ControllerAttribute { get; }
        public INamedTypeSymbol? PacketInterface { get; }
        public INamedTypeSymbol? PacketBaseType { get; }
        public INamedTypeSymbol? SerializePackableAttribute { get; }
        public INamedTypeSymbol? SerializeHeaderAttribute { get; }
        public INamedTypeSymbol? SerializeOrderAttribute { get; }
        public INamedTypeSymbol? SerializeIgnoreAttribute { get; }
        public INamedTypeSymbol? SerializeDynamicSizeAttribute { get; }
        public INamedTypeSymbol? SerializeLayoutType { get; }
        public INamedTypeSymbol? PacketContextType { get; }
        public INamedTypeSymbol? PacketContextInterface { get; }
        public INamedTypeSymbol? ConnectionType { get; }
        public INamedTypeSymbol? PacketDispatchOptionsType { get; }
        public INamedTypeSymbol? PacketRegistryFactoryType { get; }
        public INamedTypeSymbol? PacketDeserializerType { get; }
        public INamedTypeSymbol? PacketMiddlewareType { get; }
        public INamedTypeSymbol? NetworkApplicationBuilderType { get; }
        public INamedTypeSymbol? MiddlewareOrderAttribute { get; }
        public INamedTypeSymbol? MiddlewareStageAttribute { get; }
        public INamedTypeSymbol? MiddlewareStageType { get; }
        public INamedTypeSymbol? ConfigurationLoaderType { get; }
        public INamedTypeSymbol? ConfigurationIgnoreAttribute { get; }
        public INamedTypeSymbol? ReservedOpcodePermittedAttribute { get; }
        public INamedTypeSymbol? PacketMetadataProviderType { get; }
        public INamedTypeSymbol? PacketMetadataBuilderType { get; }
        public INamedTypeSymbol? MethodInfoType { get; }
        public INamedTypeSymbol? RequestOptionsType { get; }
        public INamedTypeSymbol? RequestExtensionsType { get; }
        public INamedTypeSymbol? TaskType { get; }
        public INamedTypeSymbol? GenericTaskType { get; }
        public INamedTypeSymbol? ValueTaskType { get; }
        public INamedTypeSymbol? GenericValueTaskType { get; }
        public INamedTypeSymbol? CancellationTokenType { get; }
        public INamedTypeSymbol? ReadOnlyMemoryByteType { get; }
        public INamedTypeSymbol? MemoryByteType { get; }
        public INamedTypeSymbol? FixedSizeSerializableType { get; }
        public INamedTypeSymbol? PacketDispatchType { get; }
        public INamedTypeSymbol? BufferLeaseType { get; }
        public INamedTypeSymbol? ExceptionClassifierType { get; }
        public INamedTypeSymbol? PacketScopeType { get; }
        public INamedTypeSymbol? CaughtExceptionType { get; }
        public INamedTypeSymbol? FromScopeAttribute { get; }
        public INamedTypeSymbol? RpcServiceAttribute { get; }
        public INamedTypeSymbol? RpcCallType { get; }
        public INamedTypeSymbol? RpcStreamType { get; }
        public INamedTypeSymbol? PacketAttribute { get; }
        public INamedTypeSymbol? SkipCleanAttribute { get; }
        public INamedTypeSymbol? InjectAttribute { get; }
        public INamedTypeSymbol? PoolableInterface { get; }
        public int PacketHeaderRegionOffset { get; }

        public static SymbolSet? Create(Compilation compilation, out string? missingSymbol)
        {
            INamedTypeSymbol? packetOpcodeAttribute = compilation.GetTypeByMetadataName("Nalix.Abstractions.Networking.Packets.PacketOpcodeAttribute");
            INamedTypeSymbol? controllerAttribute = compilation.GetTypeByMetadataName("Nalix.Abstractions.Networking.Packets.PacketHandlerAttribute");
            INamedTypeSymbol? packetInterface = compilation.GetTypeByMetadataName("Nalix.Abstractions.Networking.Packets.IPacket");
            INamedTypeSymbol? packetBaseType = compilation.GetTypeByMetadataName("Nalix.Codec.DataFrames.PacketBase`1");
            INamedTypeSymbol? serializePackableAttribute = compilation.GetTypeByMetadataName("Nalix.Abstractions.Serialization.SerializePackableAttribute");
            INamedTypeSymbol? serializeHeaderAttribute = compilation.GetTypeByMetadataName("Nalix.Abstractions.Serialization.SerializeHeaderAttribute");
            INamedTypeSymbol? serializeOrderAttribute = compilation.GetTypeByMetadataName("Nalix.Abstractions.Serialization.SerializeOrderAttribute");
            INamedTypeSymbol? serializeIgnoreAttribute = compilation.GetTypeByMetadataName("Nalix.Abstractions.Serialization.SerializeIgnoreAttribute");
            INamedTypeSymbol? serializeDynamicSizeAttribute = compilation.GetTypeByMetadataName("Nalix.Abstractions.Serialization.SerializeDynamicSizeAttribute");
            INamedTypeSymbol? serializeLayoutType = compilation.GetTypeByMetadataName("Nalix.Abstractions.Serialization.SerializeLayout");
            INamedTypeSymbol? packetHeaderOffsetType = compilation.GetTypeByMetadataName("Nalix.Abstractions.Networking.Packets.PacketHeaderOffset");
            INamedTypeSymbol? packetContextType = compilation.GetTypeByMetadataName("Nalix.Runtime.Dispatching.PacketContext`1");
            INamedTypeSymbol? packetContextInterface = compilation.GetTypeByMetadataName("Nalix.Abstractions.Networking.Packets.IPacketContext`1");
            INamedTypeSymbol? connectionType = compilation.GetTypeByMetadataName("Nalix.Abstractions.Networking.IConnection");
            INamedTypeSymbol? packetDispatchOptionsType = compilation.GetTypeByMetadataName("Nalix.Network.Routing.PacketDispatchOptions`1")
                ?? compilation.GetTypeByMetadataName("Nalix.Runtime.Dispatching.PacketDispatchOptions`1");
            INamedTypeSymbol? packetRegistryFactoryType = compilation.GetTypeByMetadataName("Nalix.Codec.DataFrames.PacketRegistryFactory");
            INamedTypeSymbol? packetDeserializerType = compilation.GetTypeByMetadataName("Nalix.Abstractions.Networking.Packets.IPacketDeserializer`1");
            INamedTypeSymbol? packetMiddlewareType = compilation.GetTypeByMetadataName("Nalix.Abstractions.Middleware.IPacketMiddleware`1");
            INamedTypeSymbol? networkApplicationBuilderType = compilation.GetTypeByMetadataName("Nalix.Hosting.NetworkApplicationBuilder");
            INamedTypeSymbol? middlewareOrderAttribute = compilation.GetTypeByMetadataName("Nalix.Abstractions.Middleware.MiddlewareOrderAttribute");
            INamedTypeSymbol? middlewareStageAttribute = compilation.GetTypeByMetadataName("Nalix.Abstractions.Middleware.MiddlewareStageAttribute");
            INamedTypeSymbol? middlewareStageType = compilation.GetTypeByMetadataName("Nalix.Abstractions.Middleware.MiddlewareStage");
            INamedTypeSymbol? configurationLoaderType = compilation.GetTypeByMetadataName("Nalix.Environment.Configuration.Binding.ConfigurationLoader");
            INamedTypeSymbol? configurationIgnoreAttribute = compilation.GetTypeByMetadataName("Nalix.Abstractions.ConfiguredIgnoreAttribute");
            INamedTypeSymbol? reservedOpcodePermittedAttribute = compilation.GetTypeByMetadataName("Nalix.Abstractions.ReservedOpcodePermittedAttribute");
            INamedTypeSymbol? packetMetadataProviderType = compilation.GetTypeByMetadataName("Nalix.Runtime.Dispatching.IPacketMetadataProvider");
            INamedTypeSymbol? packetMetadataBuilderType = compilation.GetTypeByMetadataName("Nalix.Runtime.Dispatching.PacketMetadataBuilder");
            INamedTypeSymbol? methodInfoType = compilation.GetTypeByMetadataName("System.Reflection.MethodInfo");
            INamedTypeSymbol? requestOptionsType = compilation.GetTypeByMetadataName("Nalix.SDK.Options.RequestOptions");
            INamedTypeSymbol? requestExtensionsType = compilation.GetTypeByMetadataName("Nalix.SDK.Transport.Extensions.RequestExtensions");
            INamedTypeSymbol? taskType = compilation.GetTypeByMetadataName(typeof(Task).FullName);
            INamedTypeSymbol? genericTaskType = compilation.GetTypeByMetadataName("System.Threading.Tasks.Task`1");
            INamedTypeSymbol? valueTaskType = compilation.GetTypeByMetadataName(typeof(ValueTask).FullName);
            INamedTypeSymbol? genericValueTaskType = compilation.GetTypeByMetadataName("System.Threading.Tasks.ValueTask`1");
            INamedTypeSymbol? cancellationTokenType = compilation.GetTypeByMetadataName(typeof(CancellationToken).FullName);
            INamedTypeSymbol? readOnlyMemoryType = compilation.GetTypeByMetadataName("System.ReadOnlyMemory`1");
            INamedTypeSymbol? memoryType = compilation.GetTypeByMetadataName("System.Memory`1");
            ITypeSymbol byteType = compilation.GetSpecialType(SpecialType.System_Byte);
            INamedTypeSymbol? readOnlyMemoryByteType = readOnlyMemoryType?.Construct(byteType);
            INamedTypeSymbol? memoryByteType = memoryType?.Construct(byteType);
            INamedTypeSymbol? fixedSizeSerializableType = compilation.GetTypeByMetadataName("Nalix.Abstractions.Serialization.IFixedSizeSerializable");
            INamedTypeSymbol? packetDispatchType = compilation.GetTypeByMetadataName("Nalix.Runtime.Dispatching.IPacketDispatch");
            INamedTypeSymbol? bufferLeaseType = compilation.GetTypeByMetadataName("Nalix.Abstractions.IBufferLease");
            INamedTypeSymbol? exceptionClassifierType = compilation.GetTypeByMetadataName("Nalix.Abstractions.Exceptions.ExceptionClassifier");
            INamedTypeSymbol? packetScopeType = compilation.GetTypeByMetadataName("Nalix.Codec.Pooling.PacketScope`1");
            INamedTypeSymbol? caughtExceptionType = compilation.GetTypeByMetadataName("System.Exception");
            INamedTypeSymbol? fromScopeAttribute = compilation.GetTypeByMetadataName("Nalix.Abstractions.Injection.FromScopeAttribute");
            INamedTypeSymbol? rpcServiceAttribute = compilation.GetTypeByMetadataName("Nalix.Abstractions.Networking.Rpc.RpcServiceAttribute");
            INamedTypeSymbol? rpcCallType = compilation.GetTypeByMetadataName("Nalix.SDK.Transport.Rpc.RpcCall`1");
            INamedTypeSymbol? rpcStreamType = compilation.GetTypeByMetadataName("Nalix.SDK.Transport.Rpc.RpcStream`1");
            INamedTypeSymbol? packetAttribute = compilation.GetTypeByMetadataName("Nalix.Abstractions.Networking.Packets.PacketAttribute");
            INamedTypeSymbol? skipCleanAttribute = compilation.GetTypeByMetadataName("Nalix.Abstractions.SkipCleanAttribute");
            INamedTypeSymbol? injectAttribute = compilation.GetTypeByMetadataName("Nalix.Abstractions.Injection.InjectAttribute");
            INamedTypeSymbol? poolableInterface = compilation.GetTypeByMetadataName("Nalix.Abstractions.IPoolable");
            int packetHeaderRegionOffset = 12;
            if (packetHeaderOffsetType is not null)
            {
                foreach (ISymbol member in packetHeaderOffsetType.GetMembers())
                {
                    if (member is IFieldSymbol { Name: "Region", HasConstantValue: true } field)
                    {
                        packetHeaderRegionOffset = Convert.ToInt32(field.ConstantValue, CultureInfo.InvariantCulture);
                        break;
                    }
                }
            }

            if (packetOpcodeAttribute is null)
            {
                missingSymbol = "Nalix.Abstractions.Networking.Packets.PacketOpcodeAttribute";
                return null;
            }

            if (controllerAttribute is null)
            {
                missingSymbol = "Nalix.Abstractions.Networking.Packets.PacketHandlerAttribute";
                return null;
            }

            if (packetInterface is null)
            {
                missingSymbol = "Nalix.Abstractions.Networking.Packets.IPacket";
                return null;
            }

            if (packetBaseType is null)
            {
                missingSymbol = "Nalix.Codec.DataFrames.PacketBase`1";
                return null;
            }

            missingSymbol = null;

            return new SymbolSet(
                    packetOpcodeAttribute,
                    controllerAttribute,
                    packetInterface,
                    packetBaseType,
                    serializePackableAttribute,
                    serializeHeaderAttribute,
                    serializeOrderAttribute,
                    serializeIgnoreAttribute,
                    serializeDynamicSizeAttribute,
                    serializeLayoutType,
                    packetContextType,
                    packetContextInterface,
                    connectionType,
                    packetDispatchOptionsType,
                    packetRegistryFactoryType,
                    packetDeserializerType,
                    packetMiddlewareType,
                    networkApplicationBuilderType,
                    middlewareOrderAttribute,
                    middlewareStageAttribute,
                    middlewareStageType,
                    configurationLoaderType,
                    configurationIgnoreAttribute,
                    reservedOpcodePermittedAttribute,
                    packetMetadataProviderType,
                    packetMetadataBuilderType,
                    methodInfoType,
                    requestOptionsType,
                    requestExtensionsType,
                    taskType,
                    genericTaskType,
                    valueTaskType,
                    genericValueTaskType,
                    cancellationTokenType,
                    readOnlyMemoryByteType,
                    memoryByteType,
                    fixedSizeSerializableType,
                    packetDispatchType,
                    bufferLeaseType,
                    exceptionClassifierType,
                    packetScopeType,
                    caughtExceptionType,
                    fromScopeAttribute,
                    rpcServiceAttribute,
                    rpcCallType,
                    rpcStreamType,
                    packetAttribute,
                    skipCleanAttribute,
                    injectAttribute,
                    poolableInterface,
                    packetHeaderRegionOffset);
        }
    }
}
