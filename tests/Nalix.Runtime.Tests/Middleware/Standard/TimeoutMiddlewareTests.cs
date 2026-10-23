// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Runtime.Dispatching;
using Nalix.Runtime.Internal.Compilation;
using Nalix.Runtime.Middleware.Standard;
using Nalix.Runtime.Tests.TestSupport;
using Xunit;
using Nalix.Runtime.Routing;

namespace Nalix.Runtime.Tests.Middleware.Standard;

[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
public sealed class TimeoutMiddlewareTests
{
    /// <summary>
    /// A handler exceeding the configured <see cref="PacketTimeoutAttribute"/>
    /// timeout must be interrupted and cause a TIMEOUT directive to be sent.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_WhenHandlerExceedsTimeout_SendsTimeoutDirective()
    {
        PacketDispatchOptions<IPacket> options = new PacketDispatchOptions<IPacket>()
            .WithMiddleware(new TimeoutMiddleware());

        PacketHandler<IPacket> descriptor = PacketHandlerFactory.CreateDescriptor<IPacket>(
            async (_, _) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                return (object?)null;
            },
            timeoutMs: 50);

        FakeConnection connection = new();
        try
        {
            await options.ExecuteResolvedHandlerAsync(
                descriptor, new TestPacket(), connection, reliable: true, encryptedOnWire: false).AsTask();

            connection.FakeTcp.SentMessages.Should().NotBeEmpty(
                "a handler exceeding its configured timeout must cause a TIMEOUT directive to be sent");
        }
        finally
        {
            connection.Dispose();
        }
    }

    /// <summary>
    /// Cancellation of the caller-supplied root token (not the internal timeout CTS) must propagate
    /// as <see cref="OperationCanceledException"/> to the caller, and must not be misreported as a
    /// TIMEOUT directive.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_WhenOuterTokenCancelled_DoesNotSendTimeoutDirective()
    {
        PacketDispatchOptions<IPacket> options = new PacketDispatchOptions<IPacket>()
            .WithMiddleware(new TimeoutMiddleware());

        using CancellationTokenSource outerCts = new();

        PacketHandler<IPacket> descriptor = PacketHandlerFactory.CreateDescriptor<IPacket>(
            async (_, _) =>
            {
                outerCts.Cancel();
                await Task.Delay(TimeSpan.FromSeconds(5), outerCts.Token);
                return (object?)null;
            },
            timeoutMs: 60_000);

        FakeConnection connection = new();
        try
        {
            Func<Task> act = async () => await options.ExecuteResolvedHandlerAsync(
                descriptor, new TestPacket(), connection, reliable: true, encryptedOnWire: false, token: outerCts.Token).AsTask();

            await act.Should().ThrowAsync<OperationCanceledException>();
            connection.FakeTcp.SentMessages.Should().BeEmpty(
                "cancellation of the outer context token (not the internal timeout CTS) must not be reported as a TIMEOUT directive");
        }
        finally
        {
            connection.Dispose();
        }
    }
}
