
using System;
using Xunit;
using Nalix.Codec.ProtocolFrames;
using Nalix.Codec.Serialization;
using Nalix.Framework.Memory.Objects;
using Nalix.Framework.Injection;
using Nalix.Codec.Pooling;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;

namespace Nalix.Framework.Tests;

[Collection(TestCollections.SequentialPooling)]
public class LeakReproductionTests
{
    [Fact]
    public void Handshake_Deserialize_ShouldNotReplaceRentedInstance()
    {
        // 1. Setup
        var poolManager = InstanceManager.Instance.GetOrCreateInstance<ObjectPoolManager>();
        
        // Ensure Control is registered in the pool
        using var initialLease = PacketFactory<Control>.Acquire();
        var initialInstance = initialLease.Value;
        
        // Create a dummy buffer for Handshake
        byte[] buffer = new byte[256];
        // We need a valid header for Control
        // Magic for Control is computed from its name.
        // But we can just use LiteSerializer to serialize one first.
        initialInstance.Initialize(ControlType.PING, 55, PacketFlags.SYSTEM, ProtocolReason.NONE);
        int length = LiteSerializer.Serialize(initialInstance, buffer);
        
        // 2. The Test
        using var testLease = PacketFactory<Control>.Acquire();
        var testInstance = testLease.Value;
        var originalReference = testInstance;
        
        // This is what PacketBase.Deserialize does:
        var refToInstance = testInstance;
        int bytesRead = LiteSerializer.Deserialize(buffer.AsSpan(0, length), ref refToInstance);
        
        // 3. Verification
        Assert.True(bytesRead > 0);
        // If this fails, it means LiteSerializer replaced the instance instead of filling it.
        Assert.Same(originalReference, refToInstance);
    }
}















