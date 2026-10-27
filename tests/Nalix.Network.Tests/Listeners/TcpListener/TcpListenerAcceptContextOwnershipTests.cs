// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net.Sockets;
using FluentAssertions;
using Nalix.Abstractions.Networking;
using Nalix.Framework.Memory.Objects;
using Nalix.Network.Internal.Pooling;
using Nalix.Network.Listeners.Tcp;
using NSubstitute;
using Xunit;

namespace Nalix.Network.Tests.Listeners.TcpListener;

public sealed class TcpListenerAcceptContextOwnershipTests
{
    private sealed class StubTcpListener : TcpListenerBase
    {
        public StubTcpListener(IProtocol protocol, IConnectionHub hub, IConnectionGuard guard)
            : base(12349, protocol, hub, guard)
        {
        }
    }

    [Fact]
    public void InitializeConnection_WhenSocketOptionsFail_DoesNotReturnContextToPool()
    {
        var protocol = Substitute.For<IProtocol>();
        var hub = Substitute.For<IConnectionHub>();
        var guard = Substitute.For<IConnectionGuard>();
        using var listener = new StubTcpListener(protocol, hub, guard);

        // A disposed socket makes InitializeOptions fail with ObjectDisposedException (peer reset race).
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Dispose();

        ObjectPoolManager pool = ObjectPoolManager.Shared;
        PooledAcceptContext context = pool.Get<PooledAcceptContext>();

        try
        {
            listener.InitializeConnection(socket, context).Should().BeNull();

            // The caller (CreateConnectionAsync finally / accept callback) still owns the context
            // and returns it itself, so the pool must not already hold it.
            PooledAcceptContext other = pool.Get<PooledAcceptContext>();
            try
            {
                other.Should().NotBeSameAs(context, "a failed InitializeConnection must leave the return to its caller");
            }
            finally
            {
                pool.Return(other);
            }
        }
        finally
        {
            pool.Return(context);
        }
    }
}
