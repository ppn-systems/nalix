// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Nalix.Analyzers.Generators.Internal;

internal class KnownNames
{
    // Namespace
    public const string CodecMemoryNamespace = "Nalix.Environment.Memory";
    public const string AbstractionsNamespace = "Nalix.Abstractions";
    public const string PacketBaseNamespace = "Nalix.Codec.DataFrames";
    public const string CodecExtensionsNamespace = "Nalix.Codec.Extensions";
    public const string LiteSerializerNamespace = "Nalix.Codec.Serialization";
    public const string FormatterProviderName = "FormatterProvider";
    public const string CollectionsFormattersNamespace = "Nalix.Codec.Serialization.Formatters.Collections";
    public const string GenerateFormatterAttributeName = "GenerateFormatterAttribute";
    public const string ExceptionsAbstractionsNamespace = "Nalix.Abstractions.Exceptions";
    public const string PacketAbstractionsNamespace = "Nalix.Abstractions.Networking.Packets";
    public const string SerializationAbstractionsNamespace = "Nalix.Abstractions.Serialization";
    public const string ConfigurationBindingNamespace = "Nalix.Environment.Configuration.Binding";
    public const string ValidationNamespace = "Nalix.Abstractions.Validation";
    public const string ConfigurationManagerMetadataName = "Nalix.Environment.Configuration.ConfigurationManager";
    public const string ConfigurationLoaderMetadataName = "Nalix.Environment.Configuration.Binding.ConfigurationLoader";
    public const string PacketAttributeMetadataName = "Nalix.Abstractions.Networking.Packets.PacketAttribute";
    public const string InjectableAttributeName = "InjectableAttribute";
    public const string InjectableAttributeMetadataName = "Nalix.Abstractions.Injection.InjectableAttribute";
    public const string InstanceManagerMetadataName = "Nalix.Framework.Injection.InstanceManager";
    public const string SingletonActivatorCacheMetadataName = "Nalix.Framework.Injection.DI.SingletonActivatorCache";
    public const string SingletonBaseMetadataName = "Nalix.Framework.Injection.DI.SingletonBase<T>";
    public const string PacketHandlerAttributeMetadataName = "Nalix.Abstractions.Networking.Packets.PacketHandlerAttribute";
    public const string InjectAttributeMetadataName = "Nalix.Abstractions.Injection.InjectAttribute";
    public const string PacketOpcodeAttributeMetadataName = "Nalix.Abstractions.Networking.Packets.PacketOpcodeAttribute";
    public const string PacketTimeoutAttributeMetadataName = "Nalix.Abstractions.Networking.Packets.PacketTimeoutAttribute";
    public const string PacketPermissionAttributeMetadataName = "Nalix.Abstractions.Networking.Packets.PacketPermissionAttribute";
    public const string PacketEncryptionAttributeMetadataName = "Nalix.Abstractions.Networking.Packets.PacketEncryptionAttribute";
    public const string PacketRateLimitAttributeMetadataName = "Nalix.Abstractions.Networking.Packets.PacketRateLimitAttribute";
    public const string FromScopeAttributeMetadataName = "Nalix.Abstractions.Injection.FromScopeAttribute";
    public const string RpcServiceAttributeMetadataName = "Nalix.Abstractions.Networking.Rpc.RpcServiceAttribute";

    // Types
    public const string IniCommentShort = "IniComment";
    public const string ConfigurationLoader = "ConfigurationLoader";

    public const string PacketBaseName = "PacketBase";
    public const string IPacketInterfaceName = "IPacket";

    public const string IFixedSizeSerializableName = "IFixedSizeSerializable";

    public const string LiteSerializerName = "LiteSerializer";

    // Common Types
    public const string GuidName = "Guid";
    public const string TimeSpanName = "TimeSpan";
    public const string TimeOnlyName = "TimeOnly";
    public const string DateOnlyName = "DateOnly";
    public const string FormatterName = "Formatter";
    public const string DateTimeOffsetName = "DateTimeOffset";

    // Attributes
    public const string SkipCleanName = "SkipClean";
    public const string SerializeIgnoreShortName = "SerializeIgnore";
    public const string SerializeOrderAttributeName = "SerializeOrderAttribute";
    public const string SerializeIgnoreAttributeName = "SerializeIgnoreAttribute";
    public const string SerializeHeaderAttributeName = "SerializeHeaderAttribute";
    public const string SerializeDynamicSizeAttributeName = "SerializeDynamicSizeAttribute";

    public const string PacketSchemaName = "PacketSchema";
    public const string PacketRegistryName = "PacketRegistry";
    public const string PacketDispatchName = "PacketDispatch";

    public const string IniCommentAttribute = "IniCommentAttribute";
    public const string ConfiguredIgnoreAttribute = "ConfiguredIgnoreAttribute";

    // Validation attributes (Nalix.Abstractions.Validation)
    public const string ValueRangeAttributeName = "ValueRangeAttribute";
    public const string LengthAttributeName = "LengthAttribute";
    public const string DurationRangeAttributeName = "DurationRangeAttribute";
    public const string RequiredAttributeName = "RequiredAttribute";
    public const string AllowedEnumAttributeName = "AllowedEnumAttribute";

}
