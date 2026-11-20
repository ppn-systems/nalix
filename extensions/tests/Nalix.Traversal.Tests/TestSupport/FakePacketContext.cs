// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Threading;
using Nalix.Abstractions.Injection;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;

namespace Nalix.Traversal.Tests.TestSupport;

internal sealed class FakePacketContext<TPacket>(TPacket packet, IConnection connection, IPacketSender sender) : IPacketContext<TPacket>
    where TPacket : IPacket
{

    public bool EncryptedOnWire => false;

    public bool SkipOutbound => false;

    public TPacket Packet { get; } = packet;

    public IConnection Connection { get; } = connection;

    public PacketMetadata Attributes => default;

    public IPacketSender Sender { get; } = sender;

    public IPacketScope Scope => null!;

    public CancellationToken CancellationToken => CancellationToken.None;

    public void ResetForPool() { }
}
