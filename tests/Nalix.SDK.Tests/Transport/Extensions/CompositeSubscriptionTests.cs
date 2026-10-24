// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.SDK.Transport.Extensions;
using Xunit;

namespace Nalix.SDK.Tests.Transport.Extensions;

public sealed class CompositeSubscriptionTests
{
    [Fact]
    public void Add_WhenDisposed_DisposesIncomingSubscriptionImmediately()
    {
        CompositeSubscription composite = new();
        composite.Dispose();

        TrackingDisposable tracking = new();
        composite.Add(tracking);

        Assert.True(tracking.Disposed);
    }

    [Fact]
    public void Dispose_WhenOneDisposableThrows_DisposesRemainingWithoutThrowing()
    {
        TrackingDisposable ok1 = new();
        ThrowingDisposable bad = new();
        TrackingDisposable ok2 = new();
        CompositeSubscription composite = new(ok1, bad, ok2);

        Exception? ex = Record.Exception(composite.Dispose);

        Assert.Null(ex);
        Assert.True(ok1.Disposed);
        Assert.True(ok2.Disposed);
    }

    private sealed class TrackingDisposable : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => this.Disposed = true;
    }

    private sealed class ThrowingDisposable : IDisposable
    {
        public void Dispose() => throw new InvalidOperationException("dispose failed");
    }
}
