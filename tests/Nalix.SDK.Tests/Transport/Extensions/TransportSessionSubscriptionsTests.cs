// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Codec.DataFrames;
using Nalix.Codec.ProtocolFrames;
using Nalix.SDK.Tests.TestSupport;
using Nalix.SDK.Transport.Extensions;
using Xunit;

namespace Nalix.SDK.Tests.Transport.Extensions;

public sealed class TransportSessionSubscriptionsTests
{
    [Fact]
    public void On_WhenArgumentsAreNull_ThrowsArgumentNullException()
    {
        RaisableFakeSession session = new();

        _ = Assert.Throws<ArgumentNullException>(() => TransportSessionSubscriptions.On<Control>(null!, _ => { }));
        _ = Assert.Throws<ArgumentNullException>(() => session.On<Control>(null!));
    }

    [Fact]
#pragma warning disable CS0618
    public void OnOnce_WhenMultipleMessagesArrive_FiresOnlyOnce()
    {
        RaisableFakeSession session = new();

        if (!PacketRegistry.IsBuilt)
        {
            session.EnsureRegistry();
        }

        int count = 0;
        using IDisposable sub = session.OnOnce<Control>(_ => true, _ => count++);

        session.SetNextPacket(new Control());
        session.RaiseMessage();
        session.SetNextPacket(new Control());
        session.RaiseMessage();

        Assert.Equal(1, count);
    }
#pragma warning restore CS0618

    [Fact]
    public void On_WhenPredicateThrows_DoesNotPropagateToCaller()
    {
        RaisableFakeSession session = new();
        using IDisposable sub = session.On(_ => throw new InvalidOperationException("predicate failed"), _ => { });

        Exception? ex = Record.Exception(session.RaiseMessage);

        Assert.Null(ex);
    }
}
