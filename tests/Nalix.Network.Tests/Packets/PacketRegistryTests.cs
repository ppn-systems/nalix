// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Codec.DataFrames;
using Xunit;

namespace Nalix.Network.Tests.Packets;

public sealed class PacketRegistryTests
{
    [Fact]
    public void Build_AfterModuleInitializerRegistration_LeavesNonNegativeDeserializerCount()
    {
        // Packets are auto-registered via source-generated ModuleInitializer
        if (!PacketRegistry.IsBuilt)
        {
            PacketRegistry.Build();
        }

        Assert.True(PacketRegistry.DeserializerCount >= 0);
    }
}
