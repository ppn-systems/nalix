// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;

namespace Nalix.Abstractions.Networking.Packets;

/// <summary>
/// Marks a class as a Nalix packet so that the packet registry generator
/// registers it automatically at module load.
/// </summary>
/// <remarks>
/// <para>
/// Types annotated with this attribute are discovered at compile time by the
/// Nalix source generators (<c>PacketRegistryGenerator</c>), which emit the
/// registration code. No runtime reflection is used.
/// </para>
/// <para>
/// This attribute is applied to classes that inherit from
/// <c>PacketBase&lt;T&gt;</c>. The packet must declare its operation code with
/// <see cref="IPacketStaticOpcode.StaticOpCode"/>; the opcode is not taken from this attribute.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [Packet]
/// public sealed partial class PingRequest : PacketBase&lt;PingRequest&gt;, IPacketStaticOpcode
/// {
///     public static ushort StaticOpCode =&gt; 0x1001;
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface, Inherited = true)]
public sealed class PacketAttribute : Attribute
{
}
