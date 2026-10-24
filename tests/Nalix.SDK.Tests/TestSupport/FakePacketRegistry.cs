// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Concurrent;
using Nalix.Abstractions.Networking.Packets;

namespace Nalix.SDK.Tests.TestSupport;

internal sealed class FakePacketRegistry
{
    private readonly ConcurrentQueue<IPacket> _queue = new();

    public void Enqueue(IPacket packet) => _queue.Enqueue(packet);
    public bool TryDequeue(out IPacket? packet) => _queue.TryDequeue(out packet);
}
