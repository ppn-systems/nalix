// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.SDK.Extensions;
using Nalix.SDK.Tests.TestSupport;
using Nalix.SDK.Transport;
using Xunit;

namespace Nalix.SDK.Tests.Extensions;

public sealed class SubscriptionExtensionsTests
{
    [Fact]
    public void SubscribeTemp_WhenSessionIsNull_ThrowsArgumentNullException()
    {
        TransportSession? session = null;

        _ = Assert.Throws<ArgumentNullException>(() =>
            session!.SubscribeTemp(
                onMessageReceived: (_, _) => { },
                onDisconnected: (_, _) => { }));
    }

    [Fact]
    public void SubscribeTemp_AfterDispose_UnsubscribesHandlers()
    {
        RaisableFakeSession session = new();
        int messageCount = 0;
        int disconnectCount = 0;

        IDisposable sub = session.SubscribeTemp(
            onMessageReceived: (_, _) => messageCount++,
            onDisconnected: (_, _) => disconnectCount++);

        session.RaiseMessage();
        session.RaiseDisconnect(new InvalidOperationException("d1"));
        sub.Dispose();
        session.RaiseMessage();
        session.RaiseDisconnect(new InvalidOperationException("d2"));

        Assert.Equal(1, messageCount);
        Assert.Equal(1, disconnectCount);
    }
}
