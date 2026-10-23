// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nalix.Abstractions;
using Nalix.Abstractions.Identity;
using Nalix.Abstractions.Injection;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Networking.Sessions;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Security;
using Nalix.Codec.ProtocolFrames;
using Nalix.Codec.Security;
using Nalix.Environment.Random;
using Nalix.Environment.Time;
using Nalix.Framework.Injection;
using Nalix.Framework.Memory.Objects;
using Nalix.Network.Connections;
using Nalix.Runtime.Dispatching;
using Nalix.Runtime.Extensions;
using Nalix.Runtime.Handlers;
using Nalix.Runtime.Options;
using Nalix.Runtime.Routing;
using Nalix.Runtime.Sessions;
using Nalix.Runtime.Throttling;
using Xunit;

namespace Nalix.Runtime.Tests;

[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
public sealed class RuntimeSecurityRegressionTests
{
    static RuntimeSecurityRegressionTests() => System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(RuntimeDispatchAndHandlersTests).TypeHandle);

    // =========================================================================
    // Group 1: [CRIT-02 & HIGH-01 & MED-07] SystemControlHandlers
    // =========================================================================

    [Fact]
    public async Task HandleCipherUpdate_WhenUnencryptedOnWire_DisconnectsWithoutUpdatingAlgorithm()
    {
        using FakeConnection connection = new()
        {
            Algorithm = CipherSuiteType.Chacha20Poly1305,
            Level = PermissionLevel.ESTABLISHED
        };
        SetHandshakeEstablished(connection, true);

        Control packet = new();
        packet.Initialize(ControlType.CIPHER_UPDATE, sequenceId: 1, reasonCode: (ProtocolReason)CipherSuiteType.Salsa20Poly1305);

        PacketContext<Control> context = CreateContext(packet, connection, reliable: true, encryptedOnWire: false);
        try
        {
            await SystemControlHandlers.HandleAsync(context);

            connection.Disconnected.Should().BeTrue("plaintext CIPHER_UPDATE must be rejected and disconnect the connection");
            connection.Algorithm.Should().Be(CipherSuiteType.Chacha20Poly1305, "algorithm must not be mutated by unencrypted CIPHER_UPDATE");
        }
        finally
        {
            context.Dispose();
        }
    }

    [Fact]
    public async Task HandleCipherUpdate_WhenCipherSuiteIsNoneOrUndefined_DisconnectsWithoutUpdatingAlgorithm()
    {
        using FakeConnection connection = new()
        {
            Algorithm = CipherSuiteType.Chacha20Poly1305,
            Level = PermissionLevel.ESTABLISHED
        };
        SetHandshakeEstablished(connection, true);

        Control packet = new();
        packet.Initialize(ControlType.CIPHER_UPDATE, sequenceId: 1, reasonCode: (ProtocolReason)CipherSuiteType.None);

        PacketContext<Control> context = CreateContext(packet, connection, reliable: true, encryptedOnWire: true);
        try
        {
            await SystemControlHandlers.HandleAsync(context);

            connection.Disconnected.Should().BeTrue("downgrade to CipherSuiteType.None must disconnect the connection");
            connection.Algorithm.Should().Be(CipherSuiteType.Chacha20Poly1305, "algorithm must not be downgraded to None");
        }
        finally
        {
            context.Dispose();
        }
    }

    [Fact]
    public async Task HandleSessionRekey_WhenUnencryptedOnWire_DisconnectsWithoutSendingKeyAgreement()
    {
        using FakeConnection connection = new()
        {
            Algorithm = CipherSuiteType.Chacha20Poly1305,
            Level = PermissionLevel.ESTABLISHED
        };
        SetHandshakeEstablished(connection, true);

        Control packet = new();
        packet.Initialize(ControlType.SESSION_REKEY, sequenceId: 1);

        PacketContext<Control> context = CreateContext(packet, connection, reliable: true, encryptedOnWire: false);
        try
        {
            await SystemControlHandlers.HandleAsync(context);

            connection.Disconnected.Should().BeTrue("unencrypted SESSION_REKEY must disconnect the connection");
            connection.FakeTcp.SentMessages.Should().BeEmpty("unencrypted SESSION_REKEY must not trigger key agreement");
        }
        finally
        {
            context.Dispose();
        }
    }

    [Fact]
    public async Task HandlePowRequestAndPublicKeyRequest_RapidDuplicates_AreRateLimitedByDirectiveGuard()
    {
        using FakeConnection connection = new();

        Control powReq = new();
        powReq.Initialize(ControlType.POW_REQUEST, sequenceId: 1);

        using (PacketContext<Control> ctx1 = CreateContext(powReq, connection, reliable: true, encryptedOnWire: false))
        using (PacketContext<Control> ctx2 = CreateContext(powReq, connection, reliable: true, encryptedOnWire: false))
        {
            await SystemControlHandlers.HandleAsync(ctx1);
            await SystemControlHandlers.HandleAsync(ctx2);
        }

        connection.FakeTcp.SentMessages.Should().HaveCount(1, "rapid duplicate POW_REQUEST on the same connection must be rate-limited");

        Control pubKeyReq = new();
        pubKeyReq.Initialize(ControlType.PUBLIC_KEY_REQUEST, sequenceId: 2);

        using (PacketContext<Control> ctx3 = CreateContext(pubKeyReq, connection, reliable: true, encryptedOnWire: false))
        using (PacketContext<Control> ctx4 = CreateContext(pubKeyReq, connection, reliable: true, encryptedOnWire: false))
        {
            await SystemControlHandlers.HandleAsync(ctx3);
            await SystemControlHandlers.HandleAsync(ctx4);
        }

        connection.FakeTcp.SentMessages.Should().HaveCount(2,
            "first PUBLIC_KEY_REQUEST must succeed after POW_REQUEST, while rapid duplicate PUBLIC_KEY_REQUEST must be rate-limited");
    }

    // =========================================================================
    // Group 2: [HIGH-02 & MED-01 & MED-05] Session Persistence & Store
    // =========================================================================

    [Fact]
    public async Task SessionPersistenceObserver_WhenConnectionDisposedImmediatelyAfterEvent_PreservesSnapshotForSave()
    {
        byte[] secretBytes = new byte[32];
        secretBytes[0] = 0x5A;
        Bytes32 expectedSecret = new(secretBytes);
        AttributeKey testAttrKey = new(0x7777UL);

        using FakeConnection connection = new()
        {
            Secret = expectedSecret,
            Level = PermissionLevel.USER,
            Algorithm = CipherSuiteType.Chacha20Poly1305,
            UserId = "user-42"
        };
        SetHandshakeEstablished(connection, true);
        connection.Attributes[testAttrKey] = "preserved-value";

        TaskCompletionSource<IConnectionSnapshotResult> saveCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        AsyncInspectingSessionService sessionService = new(saveCompleted);
        using ConnectionHub hub = new();
        hub.RegisterConnection(connection);

        Type? observerType = typeof(SessionService).Assembly.GetType("Nalix.Runtime.Sessions.SessionPersistenceObserver");
        observerType.Should().NotBeNull();

        using IDisposable observer = (IDisposable)Activator.CreateInstance(
            observerType!,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [hub, sessionService],
            culture: null)!;

        // ConnectionHub.UnregisterConnection fires ConnectionUnregistered, then immediately Disposes the connection synchronously
        hub.UnregisterConnection(connection);

        IConnectionSnapshotResult captured = await saveCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        captured.WasDisposedDuringSave.Should().BeFalse("SaveSessionAsync must receive a non-disposed snapshot even after the live connection is disposed");
        captured.ObservedSecret.Should().Be(expectedSecret, "SaveSessionAsync must observe the connection's Secret before Dispose zeroes it");
        captured.ObservedAttribute.Should().Be("preserved-value", "SaveSessionAsync must observe the connection's Attributes before Dispose returns them to the pool");
    }

    [Fact]
    public void DefaultSessionPersistencePolicy_WhenHandshakeEstablishedWithSecret_PersistsUnderDefaultOptions()
    {
        DefaultSessionPersistencePolicy policy = new();

        byte[] secretBytes = new byte[32];
        secretBytes[0] = 0x77;
        using FakeConnection connection = new()
        {
            Secret = new Bytes32(secretBytes),
            Level = PermissionLevel.ESTABLISHED
        };
        SetHandshakeEstablished(connection, true);
        AttributeKey sessionTokenKey = new(0x8888UL);
        connection.Attributes[sessionTokenKey] = 12345UL;

        bool shouldPersist = policy.ShouldPersist(connection);

        shouldPersist.Should().BeTrue("a connection with an established cryptographic handshake (non-zero Secret) must be persisted under default options");
    }

    [Fact]
    public async Task InMemorySessionStore_WhenExceedingMaxSessions_EnforcesCapacityBound()
    {
        InMemorySessionStore store = new();

        PropertyInfo? maxSessionsProp = typeof(InMemorySessionStore).GetProperty("MaxSessions", BindingFlags.Public | BindingFlags.Instance);
        maxSessionsProp.Should().NotBeNull("InMemorySessionStore must expose a configurable MaxSessions capacity cap");
        maxSessionsProp!.SetValue(store, 4);

        long expiresAt = Clock.UnixMillisecondsNow() + 60_000L;
        for (ulong i = 1; i <= 10; i++)
        {
            SessionSnapshot snapshot = new()
            {
                SessionToken = i,
                ExpiresAtUnixMilliseconds = expiresAt
            };
            await store.StoreAsync(new SessionEntry(snapshot, i));
        }

        FieldInfo storeField = typeof(InMemorySessionStore).GetField("_store", BindingFlags.NonPublic | BindingFlags.Instance)!;
        System.Collections.ICollection dict = (System.Collections.ICollection)storeField.GetValue(store)!;
        dict.Count.Should().BeLessThanOrEqualTo(4, "active sessions in InMemorySessionStore must not exceed MaxSessions");
    }

    // =========================================================================
    // Group 3: [HIGH-03 & HIGH-04] Handshake, ProofOfWork & PacketDispatchChannel
    // =========================================================================

    [Fact]
    public void ProofOfWorkHandlers_WhenAlreadyVerified_IgnoresRepeatedOrInvalidProofWithoutDisconnecting()
    {
        using FakeConnection established = new() { Level = PermissionLevel.ESTABLISHED, IdleTimeoutMs = 15_000 };

        ProofOfWorkProof replayOrInvalidProof = new();

        using PacketContext<ProofOfWorkProof> ctx = CreateContext(replayOrInvalidProof, established);
        ProofOfWorkHandlers.HandleAsync(ctx);

        established.Disconnected.Should().BeFalse("an already POW_VERIFIED / ESTABLISHED connection must ignore redundant POW_PROOF frames without running PoW verification or disconnecting");
        established.Level.Should().Be(PermissionLevel.ESTABLISHED);
    }

    [Fact]
    public async Task HandshakeHandlers_WhenSessionProofFailsConcurrently_DoesNotDoubleReturnHandshakeContextToPool()
    {
        Type? hsContextType = typeof(HandshakeHandlers).GetNestedType("HandshakeContext", BindingFlags.NonPublic);
        hsContextType.Should().NotBeNull();

        MethodInfo getMethod = typeof(ObjectPoolManager)
            .GetMethod(nameof(ObjectPoolManager.Get), BindingFlags.Public | BindingFlags.Instance)!
            .MakeGenericMethod(hsContextType!);

        object pooledContext = getMethod.Invoke(ObjectPoolManager.Shared, null)!;

        using FakeConnection connection = new();
        SetHandshakeState(connection, pooledContext);

        MethodInfo rejectMethod = typeof(HandshakeHandlers).GetMethod(
            "RejectHandshakeAsync",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        SessionProof proofPacket = new();
        proofPacket.Initialize(Bytes32.Zero);

        using (PacketContext<SessionProof> ctx1 = CreateContext(proofPacket, connection, reliable: true, encryptedOnWire: false))
        using (PacketContext<SessionProof> ctx2 = CreateContext(proofPacket, connection, reliable: true, encryptedOnWire: false))
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

            SetHandshakeState(connection, rent1);
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

    [Fact]
    public async Task PacketDispatchChannel_ExecutePacketAsync_WhenHandlerIsAsynchronous_RemainsNonBlockingAndContinuationReleasesResources()
    {
        TaskCompletionSource handlerGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int secondPacketExecuted = 0;

        PacketDispatchChannel channel = new(options =>
        {
            RegisterAsyncMemoryPacketHandler(options, opcode: 0x0101, async () =>
            {
                await handlerGate.Task;
                return null;
            });

            RegisterAsyncMemoryPacketHandler(options, opcode: 0x0102, () =>
            {
                Interlocked.Exchange(ref secondPacketExecuted, 1);
                return Task.FromResult<object?>(null);
            });
        });

        MethodInfo? executePacketMethod = typeof(PacketDispatchChannel).GetMethod(
            "ExecutePacketAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        executePacketMethod.Should().NotBeNull();

        using FakeConnection connection = new();
        FakeBufferLease lease1 = CreateRawPacketLease(opcode: 0x0101, sequence: 1, priority: PacketPriority.NONE);
        FakeBufferLease lease2 = CreateRawPacketLease(opcode: 0x0102, sequence: 2, priority: PacketPriority.NONE);

        // 1. First packet is async and incomplete: ExecutePacketAsync must return immediately (non-blocking)
        ValueTask executionTask1 = (ValueTask)executePacketMethod!.Invoke(channel, [connection, lease1, CancellationToken.None])!;
        executionTask1.IsCompleted.Should().BeTrue(
            "ExecutePacketAsync must return immediately via DispatchContinuation without blocking the worker loop");
        lease1.IsDisposed.Should().BeFalse("lease1 must remain alive while the async handler is still pending");

        // 2. Worker can immediately execute the next packet while the first packet is still awaiting handlerGate
        ValueTask executionTask2 = (ValueTask)executePacketMethod.Invoke(channel, [connection, lease2, CancellationToken.None])!;
        await executionTask2;
        Volatile.Read(ref secondPacketExecuted).Should().Be(1);
        lease2.IsDisposed.Should().BeTrue();

        // 3. Complete the first handler and verify DispatchContinuation.UnsafeOnCompleted disposes lease1
        handlerGate.SetResult();

        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        while (!lease1.IsDisposed && sw.ElapsedMilliseconds < 2000)
        {
            await Task.Delay(5);
        }

        lease1.IsDisposed.Should().BeTrue("DispatchContinuation must dispose the buffer lease once the async task completes");

        // 4. Verify DispatchContinuation directly records IncrementErrorCount() and disposes packet/lease on faulted task
        Type continuationType = typeof(PacketDispatchChannel).Assembly
            .GetType("Nalix.Runtime.Internal.Routing.DispatchContinuation")!;
        MethodInfo getContinuation = typeof(ObjectPoolManager)
            .GetMethod(nameof(ObjectPoolManager.Get), BindingFlags.Public | BindingFlags.Instance)!
            .MakeGenericMethod(continuationType);
        MethodInfo initContinuation = continuationType.GetMethod("Initialize", BindingFlags.Instance | BindingFlags.Public)!;

        TaskCompletionSource faultedGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeBufferLease faultLease = CreateRawPacketLease(opcode: 0x0101, sequence: 3, priority: PacketPriority.NONE);
        DisposableDummyPacket disposablePacket = new();

        object workItem = getContinuation.Invoke(ObjectPoolManager.Shared, null)!;
        initContinuation.Invoke(workItem, [connection, faultLease, disposablePacket, faultedGate.Task, CancellationToken.None]);

        faultedGate.SetException(new InvalidOperationException("simulated async failure"));

        sw.Restart();
        while (!faultLease.IsDisposed && sw.ElapsedMilliseconds < 2000)
        {
            await Task.Delay(5);
        }

        faultLease.IsDisposed.Should().BeTrue();
        disposablePacket.Disposed.Should().BeTrue();
        connection.ErrorCount.Should().BeGreaterThan(0, "DispatchContinuation must increment connection error count when the awaited task faults");
    }

    // =========================================================================
    // Group 4: [MED-04 & MED-06 & LOW-01 & LOW-02] PacketHandler, PolicyRateLimiter & DispatchChannel
    // =========================================================================

    [Fact]
    public void PacketHandler_CanExecute_WithStrictMatchPermission_DeniesHigherLevelWithUnauthorized()
    {
        PacketMetadata metadata = new(
            opCode: new PacketOpcodeAttribute(1),
            timeout: null,
            permission: new PacketPermissionAttribute(PermissionLevel.GUEST, PermissionEvaluation.StrictMatch),
            encryption: null,
            rateLimit: null,
            transport: null);

        object descriptor = CreatePacketHandlerDescriptor(metadata);
        MethodInfo canExecuteMethod = descriptor.GetType().GetMethod("CanExecute", BindingFlags.Instance | BindingFlags.Public)!;

        using FakeConnection connection = new()
        {
            Level = PermissionLevel.USER // Higher than GUEST, which violates StrictMatch
        };

        using PacketContext<DummyPacket> context = CreateContext(new DummyPacket(), connection, metadata: metadata);

        object?[] args = [context, ProtocolReason.NONE];
        bool canExecute = (bool)canExecuteMethod.Invoke(descriptor, args)!;
        ProtocolReason denyReason = (ProtocolReason)args[1]!;

        canExecute.Should().BeFalse("PermissionEvaluation.StrictMatch must reject connections whose level does not exactly match");
        denyReason.Should().Be(ProtocolReason.UNAUTHORIZED, "permission denial in CanExecute must report ProtocolReason.UNAUTHORIZED consistent with PermissionMiddleware");
    }

    [Fact]
    public async Task PolicyRateLimiter_SamePolicyIdAcrossDifferentOpCodes_SharesSingleRateLimitBucket()
    {
        using PolicyRateLimiter limiter = new();
        TokenBucketOptions options = new()
        {
            CapacityTokens = 1,
            InitialTokens = 1,
            TokenScale = 100,
            RefillTokensPerSecond = 0.001,
            MaxSoftViolations = 1,
            ShardCount = 8
        };

        FieldInfo? sharedField = typeof(PolicyRateLimiter).GetField("_shared", BindingFlags.NonPublic | BindingFlags.Instance);
        sharedField.Should().NotBeNull();
        sharedField!.SetValue(limiter, new TokenBucketLimiter(options));

        using ConnectedSocketScope scope = await ConnectedSocketScope.CreateAsync();
        using Connection connection = new(scope.ServerSocket, new SimpleOpCodeExtractor());

        PacketRateLimitAttribute sharedPolicy = new(requestsPerSecond: 1, burst: 1) { PolicyId = "SharedAuthPolicy" };

        DummyPacket pktA = new() { Header = new PacketHeader { OpCode = 0x2001 } };
        PacketMetadata metaA = new(new PacketOpcodeAttribute(0x2001), null, null, null, sharedPolicy, null);

        DummyPacket pktB = new() { Header = new PacketHeader { OpCode = 0x2002 } };
        PacketMetadata metaB = new(new PacketOpcodeAttribute(0x2002), null, null, null, sharedPolicy, null);

        using PacketContext<IPacket> ctxA = CreateContext<IPacket>(pktA, connection, metadata: metaA);
        using PacketContext<IPacket> ctxB = CreateContext<IPacket>(pktB, connection, metadata: metaB);

        limiter.Evaluate(ctxA).Allowed.Should().BeTrue("first request under SharedAuthPolicy should be allowed");
        limiter.Evaluate(ctxB).Allowed.Should().BeFalse("second request with a different OpCode but the same PolicyId must share the same rate-limit bucket");
    }

    [Fact]
    public void DispatchChannel_UnauthenticatedConnectionPushingUrgentPriority_IsClampedToStandardPriority()
    {
        Type? openChannelType = typeof(PacketDispatchChannel).Assembly.GetType("Nalix.Runtime.Internal.Routing.DispatchChannel`1");
        openChannelType.Should().NotBeNull();
        Type closedChannelType = openChannelType!.MakeGenericType(typeof(DummyPacket));

        using IDisposable channel = (IDisposable)Activator.CreateInstance(closedChannelType)!;
        MethodInfo pushMethod = closedChannelType.GetMethod("Push", BindingFlags.Instance | BindingFlags.Public)!;
        PropertyInfo pendingPerPriorityProp = closedChannelType.GetProperty("PendingPerPriority", BindingFlags.Instance | BindingFlags.Public)!;

        using FakeConnection unauthenticated = new() { Level = PermissionLevel.NONE };
        FakeBufferLease urgentLease = CreateRawPacketLease(opcode: 1, sequence: 1, priority: PacketPriority.URGENT);

        pushMethod.Invoke(channel, [unauthenticated, urgentLease]);

        int[] pending = (int[])pendingPerPriorityProp.GetValue(channel)!;
        pending[(int)PacketPriority.URGENT].Should().Be(0, "unauthenticated connections must not be allowed to enqueue into the URGENT priority queue");
        pending[(int)PacketPriority.NONE].Should().Be(1, "elevated priority from unauthenticated connections must be clamped to NONE");
    }

    // =========================================================================
    // Test Helpers & Fakes
    // =========================================================================

    private static PacketContext<TPacket> CreateContext<TPacket>(
        TPacket packet,
        IConnection connection,
        bool reliable = true,
        bool encryptedOnWire = false,
        PacketMetadata? metadata = null)
        where TPacket : IPacket
    {
        PacketContext<TPacket> context = new();
        PacketMetadata desc = metadata ?? new PacketMetadata(
            new PacketOpcodeAttribute(packet.Header.OpCode),
            timeout: null,
            permission: null,
            encryption: null,
            rateLimit: null,
            transport: null);

        MethodInfo initMethod = typeof(PacketContext<TPacket>).GetMethod(
            "Initialize",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        initMethod.Invoke(context, [packet, connection, desc, reliable, encryptedOnWire, false, CancellationToken.None, null]);
        return context;
    }

    private static object GetRuntimeStateObject(IConnection connection)
    {
        MethodInfo getRuntimeState = typeof(ConnectionExtensions).GetMethod(
            "GetRuntimeState",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
        return getRuntimeState.Invoke(null, [connection])!;
    }

    private static void SetHandshakeEstablished(IConnection connection, bool established)
    {
        object state = GetRuntimeStateObject(connection);
        FieldInfo field = state.GetType().GetField("HandshakeEstablished", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
        field.SetValue(state, established);
    }

    private static void SetHandshakeState(IConnection connection, object? handshakeState)
    {
        object state = GetRuntimeStateObject(connection);
        FieldInfo field = state.GetType().GetField("HandshakeState", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
        field.SetValue(state, handshakeState);
    }

    private static object CreatePacketHandlerDescriptor(PacketMetadata metadata)
    {
        Type handlerType = typeof(PacketDispatchChannel).Assembly
            .GetType("Nalix.Runtime.Internal.Compilation.PacketHandler`1")!
            .MakeGenericType(typeof(DummyPacket));

        Func<object?, PacketContext<DummyPacket>, ValueTask<object?>> invoker = static (_, _) => new ValueTask<object?>((object?)null);

        ConstructorInfo ctor = handlerType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)[0];
        return ctor.Invoke([metadata.PacketOpcode.OpCode, metadata, null, "FakeHandler", typeof(object), invoker, typeof(DummyPacket)]);
    }

    private static void RegisterAsyncMemoryPacketHandler(
        PacketDispatchOptions<IPacket> options,
        ushort opcode,
        Func<Task<object?>> asyncCallback)
    {
        Type handlerType = typeof(PacketDispatchChannel).Assembly
            .GetType("Nalix.Runtime.Internal.Compilation.PacketHandler`1")!
            .MakeGenericType(typeof(IPacket));

        PacketMetadata metadata = new(
            new PacketOpcodeAttribute(opcode),
            timeout: null,
            permission: null,
            encryption: null,
            rateLimit: null,
            transport: null);

        Func<object?, PacketContext<IPacket>, ValueTask<object?>> invoker = (_, _) => new ValueTask<object?>(asyncCallback());

        ConstructorInfo ctor = handlerType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)[0];
        object handlerStruct = ctor.Invoke([opcode, metadata, null, "AsyncMemoryHandler", typeof(ValueTask), invoker, typeof(MemoryPacket)]);

        FieldInfo tableField = typeof(PacketDispatchOptions<IPacket>).GetField("_handlerTable", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Array table = (Array)tableField.GetValue(options)!;
        Type slotType = typeof(PacketDispatchOptions<>).GetNestedType("HandlerSlot", BindingFlags.NonPublic)!.MakeGenericType(typeof(IPacket));
        object slot = Activator.CreateInstance(slotType, handlerStruct)!;
        table.SetValue(slot, (int)opcode);
    }

    private static FakeBufferLease CreateRawPacketLease(ushort opcode, ushort sequence, PacketPriority priority)
    {
        byte[] buffer = new byte[PacketHeader.Size];
        PacketHeader header = default;
        header.OpCode = opcode;
        header.Priority = priority;
        header.SequenceId = sequence;
        MemoryMarshal.Write(buffer, in header);
        return new FakeBufferLease(buffer) { IsReliable = true };
    }

    private interface IConnectionSnapshotResult
    {
        bool WasDisposedDuringSave { get; }
        Bytes32 ObservedSecret { get; }
        object? ObservedAttribute { get; }
    }

    private sealed record ConnectionSnapshotResult(
        bool WasDisposedDuringSave,
        Bytes32 ObservedSecret,
        object? ObservedAttribute) : IConnectionSnapshotResult;

    private sealed class AsyncInspectingSessionService(TaskCompletionSource<IConnectionSnapshotResult> tcs) : ISessionService
    {
        private static readonly AttributeKey s_testKey = new(0x7777UL);

        public async ValueTask SaveSessionAsync(IConnection connection, CancellationToken cancellationToken = default)
        {
            await Task.Delay(30, cancellationToken);
            bool wasDisposed = connection.IsDisposed;
            Bytes32 secret = connection.Secret;
            _ = connection.Attributes.TryGetValue(s_testKey, out object? attr);
            tcs.TrySetResult(new ConnectionSnapshotResult(wasDisposed, secret, attr));
        }

        public ValueTask<SessionScope> ConsumeAsync(ulong sessionToken, Func<SessionEntry, bool>? predicate = null, CancellationToken cancellationToken = default)
            => new(new SessionScope(null));

        public void Dispose() { }
    }

    private sealed class SimpleOpCodeExtractor : IOpCodeExtractor
    {
        public ushort Extract(ReadOnlySpan<byte> payload) =>
            payload.Length >= 2 ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(payload) : (ushort)0;
    }

    private sealed class DummyPacket : IPacket
    {
        public int Length => PacketHeader.Size;
        public PacketHeader Header { get; set; }
        public byte[] Serialize() => new byte[PacketHeader.Size];
        public int Serialize(Span<byte> buffer) => PacketHeader.Size;
    }

    private sealed class DisposableDummyPacket : IPacket, IDisposable
    {
        public bool Disposed { get; private set; }
        public int Length => PacketHeader.Size;
        public PacketHeader Header { get; set; }
        public byte[] Serialize() => new byte[PacketHeader.Size];
        public int Serialize(Span<byte> buffer) => PacketHeader.Size;
        public void Dispose() => Disposed = true;
    }

    private sealed class FakeBufferLease(byte[] buffer) : IBufferLease
    {
        private int _disposed;
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
        public int Length => buffer.Length;
        public bool IsReliable { get; set; }
        public bool EncryptedOnWire { get; set; }
        public int Capacity => buffer.Length;
        public Span<byte> Span => _disposed != 0 ? throw new ObjectDisposedException(nameof(FakeBufferLease)) : buffer;
        public Span<byte> SpanFull => Span;
        public ReadOnlyMemory<byte> Memory => buffer;
        public void Retain() { }
        public void CommitLength(int length) { }
        public bool ReleaseOwnership(out byte[]? buf, out int start, out int length)
        {
            buf = buffer;
            start = 0;
            length = buffer.Length;
            return true;
        }
        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
    }

    private sealed class FakeSequenceCounter : ISequenceCounter
    {
        private uint _value;
        public uint Next() => ++_value;
        public uint Current() => _value;
        public void Reset(uint newValue = 0) => _value = newValue;
        public bool IsValid(uint? receivedSeq, uint window = 0) => true;
        public void UpdateTo(uint receivedSeq) => _value = receivedSeq;
        public void ResumeFrom(uint lastKnownSeq, uint safetyGap = 1000) => _value = lastKnownSeq + safetyGap;
        public bool IsApproachingOverflow(uint margin = 1_000_000) => false;
    }

    private sealed class FakeTransport : IConnection.ITransport
    {
        public List<byte[]> SentMessages { get; } = [];
        public TransportFraming Framing => TransportFraming.UInt16LengthPrefixed;
        public ISequenceCounter SendSequence { get; } = new FakeSequenceCounter();
        public ISequenceCounter ReceiveSequence { get; } = new FakeSequenceCounter();
        public uint NextSendSequence() => SendSequence.Next();
        public uint NextReceiveSequence() => ReceiveSequence.Next();
        public uint CurrentSendSequence => SendSequence.Current();
        public uint CurrentReceiveSequence => ReceiveSequence.Current();
        public void Send(ReadOnlySpan<byte> message) => SentMessages.Add(message.ToArray());
        public ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
        {
            SentMessages.Add(message.ToArray());
            return ValueTask.CompletedTask;
        }
        public void BeginReceive(CancellationToken cancellationToken = default) { }
        public void UseFraming(TransportFraming framing) { }
    }

    private sealed class FakeEndpoint : INetworkEndpoint
    {
        public string Address => "127.0.0.1";
        public int Port => 12345;
        public bool HasPort => true;
        public bool IsIPv6 => false;
    }

    private sealed class FakeConnection : IConnection
    {
        private static ulong s_nextId = 1000;
        public FakeTransport FakeTcp { get; } = new();
        public bool IsDisposed { get; private set; }
        public bool Disconnected { get; private set; }
        public string? DisconnectReason { get; private set; }
        public bool IsUdpCreated => false;
        public ulong ConnectionId { get; } = Interlocked.Increment(ref s_nextId);
        public string? UserId { get; set; }
        public long UpTime => 10;
        public long LastPingTime => 0;
        public bool ExcludeFromIdleTimeout { get; set; }
        public IOpCodeExtractor PacketClassifier { get; } = new SimpleOpCodeExtractor();
        public INetworkEndpoint NetworkEndpoint { get; } = new FakeEndpoint();
        public IObjectMap<AttributeKey, object> Attributes { get; } = ObjectMap<AttributeKey, object>.Rent();
        public ConcurrentDictionary<ushort, object> RateLimitCache { get; } = new();
        public Bytes32 Secret { get; set; }
        public PermissionLevel Level { get; set; }
        public CipherSuiteType Algorithm { get; set; }
        public IConnection.ITransport TCP => FakeTcp;
        public IConnection.ITransport? UDP => null;

#pragma warning disable CS0067
        public event EventHandler<IConnectionEventArgs>? ConnectionClosed;
        public event EventHandler<IConnectionEventArgs>? MessageProcessing;
        public event EventHandler<IConnectionEventArgs>? MessageProcessed;
#pragma warning restore CS0067

        public void Disconnect(string? reason = null)
        {
            Disconnected = true;
            DisconnectReason = reason;
        }

        public void Dispose()
        {
            if (!IsDisposed)
            {
                IsDisposed = true;
                Secret = Bytes32.Zero;
                Attributes.Return();
            }
        }

        public int ErrorCount { get; private set; }
        public void IncrementErrorCount() => ErrorCount++;
        public int IdleTimeoutMs { get; set; } = 60_000;
        public void UpdateIdleTimeout(int newTimeoutMs) => IdleTimeoutMs = newTimeoutMs;
    }

    private sealed class ConnectedSocketScope : IDisposable
    {
        private ConnectedSocketScope(Socket listenerSocket, Socket clientSocket, Socket serverSocket)
        {
            ListenerSocket = listenerSocket;
            ClientSocket = clientSocket;
            ServerSocket = serverSocket;
        }

        public Socket ListenerSocket { get; }
        public Socket ClientSocket { get; }
        public Socket ServerSocket { get; }

        public static async Task<ConnectedSocketScope> CreateAsync()
        {
            IPAddress ip = IPAddress.Loopback;
            Socket listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(ip, 0));
            listener.Listen(1);

            int port = ((IPEndPoint)listener.LocalEndPoint!).Port;
            Task<Socket> acceptTask = Task.Run(() => listener.Accept());

            Socket client = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await client.ConnectAsync(ip, port);

            Socket server = await acceptTask;
            return new ConnectedSocketScope(listener, client, server);
        }

        public void Dispose()
        {
            try { ClientSocket.Dispose(); } catch { }
            try { ServerSocket.Dispose(); } catch { }
            try { ListenerSocket.Dispose(); } catch { }
        }
    }
}
