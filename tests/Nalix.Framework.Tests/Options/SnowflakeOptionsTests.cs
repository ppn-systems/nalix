// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using Nalix.Abstractions.Exceptions;
using Nalix.Codec.Options;
using Nalix.Environment.Options;
using Nalix.Framework.Options;
using Xunit;

namespace Nalix.Framework.Tests.Options;

public sealed class SnowflakeOptionsTests
{
    [Fact]
    public void Ctor_WithDefaultsAndCustomMachineId_ExposesMachineId()
    {
        SnowflakeOptions defaults = new();
        SnowflakeOptions custom = new() { MachineId = 512 };

        Assert.Equal((ushort)1, defaults.MachineId);
        Assert.Equal((ushort)512, custom.MachineId);
    }
}
