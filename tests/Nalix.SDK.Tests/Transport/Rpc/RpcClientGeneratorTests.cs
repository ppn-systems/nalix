// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Rpc;
using Nalix.Abstractions.Primitives;
using Nalix.SDK.Options;
using Nalix.SDK.Transport;
using Nalix.SDK.Transport.Extensions;
using Nalix.SDK.Transport.Rpc;
using Xunit;
using Nalix.SDK.Tests.TestSupport;

namespace Nalix.SDK.Tests.Transport.Rpc;

public sealed class RpcClientGeneratorTests
{
    [Fact]
    public void CreateRpcClient_WhenInterfaceIsRpcService_ReturnsGeneratedProxy()
    {
        // Arrange
        TransportSession session = new FakeSession(true);

        // Act
        ITestRpcService client = session.CreateRpcClient<ITestRpcService>();

        // Assert
        Assert.NotNull(client);
        Assert.Equal("TestRpcService_RpcClient", client.GetType().Name);

        // Verify that the generated methods can be called without exception
        RpcCall<TestRpcResponse> call = client.RequestAsync(default);
        Assert.NotNull(call);

        RpcStream<TestRpcStreamResponse> stream = client.StreamAsync(default);
        Assert.NotNull(stream);
    }
}
