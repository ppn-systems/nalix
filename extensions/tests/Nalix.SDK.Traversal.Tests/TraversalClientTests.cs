// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.SDK.Transport;
using NSubstitute;

namespace Nalix.SDK.Traversal.Tests;

/// <summary>
/// Verifies the argument checks of <see cref="TraversalClient"/>.
/// </summary>
public sealed class TraversalClientTests
{
    [Fact]
    public void Ctor_WhenControlSessionIsNull_ThrowsArgumentNullException()
    {
        _ = Assert.Throws<ArgumentNullException>(() => new TraversalClient(null!));
    }

    [Fact]
    public async Task ConnectAsync_WhenReflectorEndpointIsNull_ThrowsArgumentNullException()
    {
        TraversalClient client = CreateClient();

        _ = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            client.ConnectAsync(1UL, null!, TimeSpan.FromSeconds(1)));
    }

    private static TraversalClient CreateClient() => new(Substitute.For<TransportSession>((object?)null));
}
