// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using Nalix.Environment.Memory;
using Xunit;

namespace Nalix.Environment.Tests.Audit;

public sealed class Leb128AuditTests
{
    [Fact]
    [Trait("Category", "Audit")]
    public void TryRead_WhenFifthByteExceedsUInt32Range_ThrowsFormatException()
    {
        byte[] buffer = [0x80, 0x80, 0x80, 0x80, 0x10]; // encodes 2^32, which does not fit in uint

        _ = Assert.Throws<FormatException>(() => Leb128.TryRead(buffer, out uint _, out int _));
    }
}
