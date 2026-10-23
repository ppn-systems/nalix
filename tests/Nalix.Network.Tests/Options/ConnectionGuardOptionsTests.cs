// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Exceptions;
using Nalix.Network.Options;
using Xunit;

namespace Nalix.Network.Tests.Options;

public sealed class ConnectionGuardOptionsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_WhenMaxPacketPerSecondNotPositive_ThrowsValidationException(int value)
    {
        ConnectionGuardOptions options = new()
        {
            MaxPacketPerSecond = value
        };

        _ = Assert.Throws<ValidationException>(() => options.Validate());
    }
}
