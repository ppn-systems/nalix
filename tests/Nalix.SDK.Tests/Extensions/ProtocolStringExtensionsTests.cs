// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Networking.Protocols;
using Nalix.SDK.Extensions;
using Xunit;

namespace Nalix.SDK.Tests.Extensions;

public sealed class ProtocolStringExtensionsTests
{
    [Fact]
    public void ToDisplayString_ForKnownValues_ReturnsFriendlyText()
    {
        Assert.Equal("Please reconnect.", ProtocolAdvice.RECONNECT.ToDisplayString());
        Assert.Equal("Request timed out.", ProtocolReason.TIMEOUT.ToDisplayString());
    }

    [Fact]
    public void ToDisplayString_ForUnknownValues_ReturnsFallbackText()
    {
        ProtocolAdvice unknownAdvice = (ProtocolAdvice)255;
        ProtocolReason unknownReason = (ProtocolReason)65535;

        Assert.Equal("Unknown action.", unknownAdvice.ToDisplayString());
        Assert.Equal("Unspecified error.", unknownReason.ToDisplayString());
    }
}
