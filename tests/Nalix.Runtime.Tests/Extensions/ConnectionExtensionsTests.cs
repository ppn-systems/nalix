// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Runtime.Extensions;
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
}
