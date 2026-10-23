// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Xunit;

namespace Nalix.SDK.Tests.Dispatching;

public sealed class InlineDispatcherTests
{
    [Fact]
    public void Post_WhenActionIsNull_DoesNothing()
    {
        InlineDispatcher dispatcher = new();

        Exception? ex = Record.Exception(() => dispatcher.Post(null!));

        Assert.Null(ex);
    }

    [Fact]
    public void Post_WhenActionExists_ExecutesSynchronously()
    {
        InlineDispatcher dispatcher = new();
        int value = 0;

        dispatcher.Post(() => value = 42);

        Assert.Equal(42, value);
    }
}
