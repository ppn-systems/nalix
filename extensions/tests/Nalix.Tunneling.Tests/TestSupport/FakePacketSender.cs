// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions.Networking.Packets;

namespace Nalix.Tunneling.Tests.TestSupport;

/// <summary>
/// Records packets sent through <see cref="IPacketSender"/>. Packets are pooled by the handlers, so each
/// recorded packet is a copy of the fields the tests inspect.
/// </summary>
internal sealed class FakePacketSender : IPacketSender
{
    public List<IPacket> Sent { get; } = [];

    public void Initialize<TPacket>(IPacketContext<TPacket> context) where TPacket : IPacket { }

    public ValueTask SendAsync(IPacket packet, CancellationToken ct = default)
    {
        this.Record(packet);
        return ValueTask.CompletedTask;
    }

    public ValueTask SendAsync(IPacket packet, bool forceEncrypt, CancellationToken ct = default)
    {
        this.Record(packet);
        return ValueTask.CompletedTask;
    }

    public ValueTask ReplyAsync(IPacket response, CancellationToken ct = default)
    {
        this.Record(response);
        return ValueTask.CompletedTask;
    }

    public void ResetForPool() => this.Sent.Clear();

    private void Record(IPacket packet) => this.Sent.Add(packet switch
    {
        TunnelConnectAck ack => new TunnelConnectAck { Success = ack.Success, Reason = ack.Reason },
        TunnelProvideAck ack => new TunnelProvideAck { Success = ack.Success, Reason = ack.Reason },
        TunnelRequest request => new TunnelRequest { Token = request.Token },
        _ => packet,
    });
}
