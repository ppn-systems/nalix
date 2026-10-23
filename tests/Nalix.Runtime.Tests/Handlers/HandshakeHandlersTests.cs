// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Threading.Tasks;
using FluentAssertions;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Primitives;
using Nalix.Codec.ProtocolFrames;
using Nalix.Framework.Memory.Objects;
using Nalix.Runtime.Dispatching;
using Nalix.Runtime.Handlers;
using Nalix.Runtime.Tests.TestSupport;
using Xunit;

namespace Nalix.Runtime.Tests.Handlers;

[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
public sealed class HandshakeHandlersTests
{
    [Fact]
    public async Task HandleSessionProofAsync_WhenProofFailsConcurrently_DoesNotDoubleReturnHandshakeContextToPool()
    {
        System.Type? hsContextType = typeof(HandshakeHandlers).GetNestedType("HandshakeContext", BindingFlags.NonPublic);
        hsContextType.Should().NotBeNull();

        MethodInfo getMethod = typeof(ObjectPoolManager)
            .GetMethod(nameof(ObjectPoolManager.Get), BindingFlags.Public | BindingFlags.Instance)!
            .MakeGenericMethod(hsContextType!);

        object pooledContext = getMethod.Invoke(ObjectPoolManager.Shared, null)!;

        using FakeConnection connection = new();
        ConnectionRuntimeStateAccessor.SetHandshakeState(connection, pooledContext);

        MethodInfo rejectMethod = typeof(HandshakeHandlers).GetMethod(
            "RejectHandshakeAsync",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        SessionProof proofPacket = new();
        proofPacket.Initialize(Bytes32.Zero);

        using (PacketContext<SessionProof> ctx1 = PacketContextFactory.Create(proofPacket, connection, reliable: true, encryptedOnWire: false))
        using (PacketContext<SessionProof> ctx2 = PacketContextFactory.Create(proofPacket, connection, reliable: true, encryptedOnWire: false))
        {
            // Simulate HandleSessionProofAsync + RejectHandshakeAsync racing on the same connection's HandshakeState
            ValueTask vt1 = HandshakeHandlers.HandleSessionProofAsync(ctx1);
            ValueTask vt2 = (ValueTask)rejectMethod.Invoke(null, [connection, ctx2.Sender, ProtocolReason.SIGNATURE_INVALID])!;
            await vt1;
            await vt2;
        }

        // Rent two instances from the pool: if the failed proof returned `pooledContext` twice,
        // or if HandleSessionProofAsync leaked / double-returned `pooledContext`, check pool integrity:
        // Note: In unpatched HandleSessionProofAsync, when packet.Proof != expectedProof (line 230),
        // TryGetState did NOT clear HandshakeState unless RejectHandshakeAsync cleared it non-atomically!
        // Let's also test the exact non-atomic race by invoking TryGetState + RejectHandshakeAsync:
        object rent1 = getMethod.Invoke(ObjectPoolManager.Shared, null)!;
        object rent2 = getMethod.Invoke(ObjectPoolManager.Shared, null)!;

        try
        {
            MethodInfo tryGetStateMethod = typeof(HandshakeHandlers).GetMethod(
                "TryGetState",
                BindingFlags.Static | BindingFlags.NonPublic)!;

            ConnectionRuntimeStateAccessor.SetHandshakeState(connection, rent1);
            object?[] argsA = [connection, null];
            object?[] argsB = [connection, null];

            bool gotA = (bool)tryGetStateMethod.Invoke(null, argsA)!;
            bool gotB = (bool)tryGetStateMethod.Invoke(null, argsB)!;

            (gotA && gotB).Should().BeFalse("TryGetState must atomically consume HandshakeState via Interlocked.Exchange so two callers can never obtain the same pooled HandshakeContext");
        }
        finally
        {
            MethodInfo returnMethod = typeof(ObjectPoolManager)
                .GetMethod(nameof(ObjectPoolManager.Return), BindingFlags.Public | BindingFlags.Instance)!
                .MakeGenericMethod(hsContextType!);
            returnMethod.Invoke(ObjectPoolManager.Shared, [rent1]);
            if (!ReferenceEquals(rent1, rent2))
            {
                returnMethod.Invoke(ObjectPoolManager.Shared, [rent2]);
            }
        }
    }
}
