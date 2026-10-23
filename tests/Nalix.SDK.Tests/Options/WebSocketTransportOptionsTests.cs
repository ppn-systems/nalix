// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.SDK.Options;
using Xunit;

namespace Nalix.SDK.Tests.Options;

public sealed class WebSocketTransportOptionsTests
{
    [Fact]
    public void Validate_WhenMaxMessageSizeIsInvalid_ThrowsValidationException()
    {
        WebSocketTransportOptions options = new()
        {
            MaxMessageSize = 0
        };

        _ = Assert.Throws<Nalix.Abstractions.Exceptions.ValidationException>(options.Validate);
    }
}
