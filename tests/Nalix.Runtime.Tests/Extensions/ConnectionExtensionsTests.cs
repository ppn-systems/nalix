// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Runtime.Extensions;
using Nalix.Runtime.Tests.TestSupport;
using Xunit;

namespace Nalix.Runtime.Tests.Extensions;

[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
public sealed class ConnectionExtensionsTests
{
    [Fact]
    public async Task SendAsync_WhenSenderIsNull_ThrowsArgumentNullException()
    {
        IPacketSender? sender = null;

        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await sender!.SendAsync(
                controlType: ControlType.PING,
                reason: ProtocolReason.NONE,
                action: ProtocolAdvice.NONE,
                options: default));
    }

    [Theory]
    [InlineData("GetRuntimeState")]
    [InlineData("GetSequenceState")]
    public async Task StateAccessor_FirstCallsFromManyThreads_ShareOneInstance(string accessor)
    {
        MethodInfo method = StateAccessor(accessor);
        for (int round = 0; round < 200; round++)
        {
            using FakeConnection connection = new();
            using Barrier barrier = new(16);
            List<Task<object>> calls = [];
            for (int i = 0; i < 16; i++)
            {
                calls.Add(Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    return method.Invoke(null, [connection])!;
                }));
            }

            object[] states = await Task.WhenAll(calls);

            states.Distinct().Should().ContainSingle("the handshake slot claimed through one instance must be seen through every other");
            method.Invoke(null, [connection]).Should().BeSameAs(states[0]);
        }
    }

    [Fact]
    public void GetRuntimeState_KeyHoldsAnotherType_ReplacesItWithTheState()
    {
        using FakeConnection connection = new();
        connection.Attributes[ConnectionAttributes.RuntimeState] = "not a state";
        MethodInfo method = StateAccessor("GetRuntimeState");

        object state = method.Invoke(null, [connection])!;

        state.GetType().Name.Should().Be("RuntimeConnectionState");
        connection.Attributes[ConnectionAttributes.RuntimeState].Should().BeSameAs(state);
    }

    private static MethodInfo StateAccessor(string name) =>
        typeof(ConnectionExtensions).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;
}
