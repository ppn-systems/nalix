// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using System.Net.Sockets;
using Nalix.SDK.Options;
using Nalix.SDK.Tests.TestSupport;
using Nalix.SDK.Transport;

namespace Nalix.SDK.Tests.Audit;

// SDK-01 audit reproduction: an application-initiated DisconnectAsync() issued while the auto-reconnect
// supervisor is waiting out its backoff must stop the supervisor. The current code only raises the
// intentional-disconnect flag when a live socket is torn down, so the flag is consumed as a no-op and
// the supervisor keeps going, opening a new TCP connection after the application logged out.
//
// A raw TcpListener is used instead of a Nalix server so the drop is deterministic: closing the accepted
// socket is an unexpected peer close, and a pending TCP connection on the listener is direct evidence
// that the supervisor reconnected.
[Trait("Category", "Audit")]
public sealed class ReconnectDisconnectAuditTests : IDisposable
{
    [Fact]
    public async Task DisconnectAsync_WhenIssuedDuringReconnectBackoff_StopsReconnectLoop()
    {
        int port = TestEnvironment.GetFreePort();
        TcpListener listener = new(IPAddress.Loopback, port);
        listener.Start();

        try
        {
            using TcpSession session = new(new TransportOptions
            {
                Address = "127.0.0.1",
                Port = (ushort)port,
                ConnectTimeoutMillis = 2000,
                AutoReconnectEnabled = true,
                ReconnectMaxAttempts = 0,
                ReconnectBaseDelayMillis = 1500,
                ReconnectMaxDelayMillis = 1500,
            });

            TaskCompletionSource dropped = new(TaskCreationOptions.RunContinuationsAsynchronously);
            session.OnDisconnected += (_, _) => dropped.TrySetResult();

            await session.ConnectAsync();
            Assert.True(session.IsConnected);

            // Unexpected drop: the peer closes the connection, the supervisor enters its 1.5 s backoff.
            using TcpClient serverSide = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10));
            serverSide.Close();
            await dropped.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(session.IsConnected);

            // Application logs out while the supervisor is still waiting out its backoff.
            await session.DisconnectAsync();

            // Give the backoff time to elapse. A logged-out client must not open a new connection.
            await Task.Delay(TimeSpan.FromSeconds(5));

            Assert.False(
                listener.Pending(),
                "DisconnectAsync() was ignored by the auto-reconnect loop: a new connection was opened after an application-initiated disconnect.");
        }
        finally
        {
            listener.Stop();
        }
    }

    public void Dispose() => Nalix.Framework.Injection.InstanceManager.Instance.Clear(dispose: false);
}
