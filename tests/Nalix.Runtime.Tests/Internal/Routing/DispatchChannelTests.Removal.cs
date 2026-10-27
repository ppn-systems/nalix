// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Runtime.Internal.Routing;
using Xunit;

namespace Nalix.Runtime.Tests.Internal.Routing;

public sealed partial class DispatchChannelTests
{
    /// <summary>
    /// A reader loop that is still pushing while the connection is being unregistered must not resurrect the
    /// node that <c>RemoveConnection</c> is tearing down: the channel must end with no active connection, no
    /// queued packet and no node that has lost its connection while still being marked live.
    /// </summary>
    [Fact]
    public void RemoveConnection_WhilePushingConcurrently_LeavesNoResurrectedNodeOrDriftedCounters()
    {
        using DispatchChannel<FakePacket> channel = new();
        FieldInfo removedField = GetNodeField(channel, "Removed");
        FieldInfo connectionField = GetNodeField(channel, "Connection");

        for (int iteration = 0; iteration < 300; iteration++)
        {
            FakeConnection connection = new();
            channel.Push(connection, CreatePacketLease(1, PacketPriority.NONE));

            int stop = 0;
            Task pusher = Task.Run(() =>
            {
                int sequence = 2;
                while (Volatile.Read(ref stop) == 0)
                {
                    channel.Push(connection, CreatePacketLease(sequence++ & 0x7FFF, PacketPriority.NONE));
                }
            }, TestContext.Current.CancellationToken);

            Thread.Sleep(1);

            // The transport disposes the connection first, then the hub raises ConnectionUnregistered.
            connection.IsDisposed = true;
            InvokeRemoveConnection(channel, connection);

            Thread.Sleep(1);
            Volatile.Write(ref stop, 1);
            pusher.Wait(TestContext.Current.CancellationToken);

            Assert.Equal(0, channel.TotalConnections);
            Assert.Equal(0, channel.TotalPackets);

            foreach (object node in GetAllNodes(channel))
            {
                bool orphanedButLive = connectionField.GetValue(node) is null && (int)removedField.GetValue(node)! == 0;
                Assert.False(orphanedButLive, $"iteration {iteration}: node lost its connection but is not marked removed");
            }
        }
    }
}
