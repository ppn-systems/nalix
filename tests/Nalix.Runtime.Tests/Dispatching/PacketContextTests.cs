// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Runtime.Dispatching;
using Nalix.Runtime.Tests.TestSupport;
using Xunit;

namespace Nalix.Runtime.Tests.Dispatching;

public sealed class PacketContextTests
{
    [Fact]
    public void Return_WhenCalledMultipleTimes_RemainsSafe()
    {
        PacketContext<TestPacket> context = new();

        Assert.False(context.IsReliable);
        Assert.False(context.SkipOutbound);

        context.Return();
        context.Return();
        context.ResetForPool();
    }
}
