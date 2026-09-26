using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nalix.Abstractions;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Networking.Sessions;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Security;
using Nalix.Framework.Memory.Objects;
using Nalix.Runtime.Dispatching;
using Nalix.Runtime.Middleware.Standard;
using Nalix.Runtime.Routing;
using Nalix.Runtime.Extensions;
using Nalix.Runtime.Handlers;
using Nalix.Runtime.Internal.Compilation;
using Xunit;

namespace Nalix.Runtime.Tests;

[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
public sealed class RuntimeDispatchAndHandlersTests
{


    static RuntimeDispatchAndHandlersTests()
    {
        string tempFolderName = "NalixTests_" + Guid.NewGuid().ToString("N");
        if (System.IO.Path.IsPathRooted(tempFolderName))
        {
            throw new InvalidOperationException("Temporary test folder name must be relative.");
        }

        string tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), tempFolderName);
        Nalix.Environment.IO.Directories.SetBasePathOverride(tempPath);

        string configDir = Nalix.Environment.IO.Directories.ConfigurationDirectory;
        System.IO.Directory.CreateDirectory(configDir);

        string certPath = System.IO.Path.Combine(configDir, "certificate.private");
        System.IO.File.WriteAllText(certPath, "0000000000000000000000000000000000000000000000000000000000000000");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65)]
    public void PacketDispatchOptionsWithDispatchLoopCount_WhenOutOfRange_ThrowsArgumentOutOfRangeException(int value)
    {
        PacketDispatchOptions<TestPacket> options = new();

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => { options.WithDispatchLoopCount(value); });
    }

    [Fact]
    public void PacketDispatchOptionsWithDispatchLoopCount_WhenValueIsNullOrValid_SetsProperty()
    {
        PacketDispatchOptions<TestPacket> options = new();

        _ = options.WithDispatchLoopCount(null);
        Assert.Equal(0, options.Drain.Count);

        _ = options.WithDispatchLoopCount(8);
        Assert.Equal(8, options.Drain.Count);
    }

    [Fact]
    public void PacketDispatchOptionsWithMiddleware_WhenMiddlewareIsNull_ThrowsArgumentNullException()
    {
        PacketDispatchOptions<TestPacket> options = new();

        _ = Assert.Throws<ArgumentNullException>(() => { options.WithMiddleware(null!); });
    }





#if DEBUG
    [Fact]
    public void PacketContextDefaultsAndReturn_WhenCalledMultipleTimes_RemainsSafe()
    {
        PacketContext<TestPacket> context = new();

        Assert.False(context.IsReliable);
        Assert.False(context.SkipOutbound);

        context.Return();
        context.Return();
        context.ResetForPool();
    }
#endif

    [Fact]
    public async Task ConnectionExtensionsSendAsync_WhenSenderIsNull_ThrowsArgumentNullException()
    {
        IPacketSender? sender = null;

        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await sender!.SendAsync(
                controlType: ControlType.PING,
                reason: ProtocolReason.NONE,
                action: ProtocolAdvice.NONE,
                options: default));
    }



    [Fact]
    public async Task SessionHandlersHandleAsync_WhenContextIsNull_ThrowsArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await SessionHandlers.HandleAsync(null!).AsTask());
    }

    [Fact]
    public async Task SystemControlHandlersHandleAsync_WhenContextIsNull_ThrowsArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await SystemControlHandlers.HandleAsync(null!).AsTask());
    }





#if DEBUG
    /// <summary>
    /// Area 4: a packet handler that throws synchronously (before ever returning a ValueTask)
    /// must propagate the exception directly out of ExecuteResolvedHandlerAsync — the terminal
    /// fast path (PacketDispatchOptions.Execution.cs ExecuteTerminalHandler) has no try/catch
    /// around a synchronous-throw invoker.
    /// </summary>
    [Fact]
    public async Task ExecuteResolvedHandlerAsync_SynchronousThrowingHandler_PropagatesException()
    {
        PacketDispatchOptions<TestPacket> options = new();
        PacketHandler<TestPacket> descriptor = CreateDescriptor(
            (_, _) => throw new InvalidOperationException("sync boom"));

        FakeConnection connection = new();
        try
        {
            Func<Task> act = async () => await options.ExecuteResolvedHandlerAsync(
                descriptor, new TestPacket(), connection, reliable: true, encryptedOnWire: false).AsTask();

            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("sync boom");
        }
        finally
        {
            connection.Dispose();
        }
    }

    /// <summary>
    /// Area 4: a packet handler that throws asynchronously (after an await) must be caught by
    /// PacketDispatchOptions.HandleDispatchExceptionAsync and converted into a FAIL control
    /// directive sent over the connection's transport, rather than propagated to the caller.
    /// The registered WithErrorHandling callback must also fire with the original exception.
    /// </summary>
    [Fact]
    public async Task ExecuteResolvedHandlerAsync_AsynchronousThrowingHandler_IsContainedAndReported()
    {
        Exception? observedException = null;
        ushort observedOpCode = 0;

        PacketDispatchOptions<TestPacket> options = new PacketDispatchOptions<TestPacket>()
            .WithErrorHandling((ex, opCode) =>
            {
                observedException = ex;
                observedOpCode = opCode;
            });

        PacketHandler<TestPacket> descriptor = CreateDescriptor(async (_, _) =>
        {
            await Task.Yield();
            throw new InvalidOperationException("async boom");
        });

        FakeConnection connection = new();
        try
        {
            await options.ExecuteResolvedHandlerAsync(
                descriptor, new TestPacket(), connection, reliable: true, encryptedOnWire: false).AsTask();

            observedException.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Be("async boom");
            observedOpCode.Should().Be(descriptor.OpCode);
            connection.FakeTcp.SentMessages.Should().NotBeEmpty(
                "an asynchronously-thrown handler exception must be converted into a sent control directive");
        }
        finally
        {
            connection.Dispose();
        }
    }

    /// <summary>
    /// Area 4: ExecuteTerminalHandler calls ct.ThrowIfCancellationRequested() synchronously
    /// before invoking the handler — a context whose token is already cancelled must throw
    /// immediately without ever invoking the handler delegate.
    /// </summary>
    [Fact]
    public async Task ExecuteResolvedHandlerAsync_AlreadyCancelledToken_ThrowsWithoutInvokingHandler()
    {
        PacketDispatchOptions<TestPacket> options = new();
        bool invoked = false;
        PacketHandler<TestPacket> descriptor = CreateDescriptor((_, _) =>
        {
            invoked = true;
            return new ValueTask<object?>((object?)null);
        });

        FakeConnection connection = new();
        using CancellationTokenSource cts = new();
        cts.Cancel();

        try
        {
            Func<Task> act = async () => await options.ExecuteResolvedHandlerAsync(
                descriptor, new TestPacket(), connection, reliable: true, encryptedOnWire: false, token: cts.Token).AsTask();

            await act.Should().ThrowAsync<OperationCanceledException>();
            invoked.Should().BeFalse("a pre-cancelled token must short-circuit before the handler runs");
        }
        finally
        {
            connection.Dispose();
        }
    }

    /// <summary>
    /// A handler exceeding the configured <see cref="Nalix.Abstractions.Networking.Packets.PacketTimeoutAttribute"/>
    /// timeout must be interrupted and cause a TIMEOUT directive to be sent.
    /// </summary>
    [Fact]
    public async Task TimeoutMiddleware_HandlerExceedsTimeout_SendsTimeoutDirective()
    {
        PacketDispatchOptions<IPacket> options = new PacketDispatchOptions<IPacket>()
            .WithMiddleware(new TimeoutMiddleware());

        PacketHandler<IPacket> descriptor = CreateDescriptor<IPacket>(
            async (_, _) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                return (object?)null;
            },
            timeoutMs: 50);

        FakeConnection connection = new();
        try
        {
            await options.ExecuteResolvedHandlerAsync(
                descriptor, new TestPacket(), connection, reliable: true, encryptedOnWire: false).AsTask();

            connection.FakeTcp.SentMessages.Should().NotBeEmpty(
                "a handler exceeding its configured timeout must cause a TIMEOUT directive to be sent");
        }
        finally
        {
            connection.Dispose();
        }
    }

    /// <summary>
    /// Cancellation of the caller-supplied root token (not the internal timeout CTS) must propagate
    /// as <see cref="OperationCanceledException"/> to the caller, and must not be misreported as a
    /// TIMEOUT directive.
    /// </summary>
    [Fact]
    public async Task TimeoutMiddleware_OuterTokenCancelled_DoesNotSendTimeoutDirective()
    {
        PacketDispatchOptions<IPacket> options = new PacketDispatchOptions<IPacket>()
            .WithMiddleware(new TimeoutMiddleware());

        using CancellationTokenSource outerCts = new();

        PacketHandler<IPacket> descriptor = CreateDescriptor<IPacket>(
            async (_, _) =>
            {
                outerCts.Cancel();
                await Task.Delay(TimeSpan.FromSeconds(5), outerCts.Token);
                return (object?)null;
            },
            timeoutMs: 60_000);

        FakeConnection connection = new();
        try
        {
            Func<Task> act = async () => await options.ExecuteResolvedHandlerAsync(
                descriptor, new TestPacket(), connection, reliable: true, encryptedOnWire: false, token: outerCts.Token).AsTask();

            await act.Should().ThrowAsync<OperationCanceledException>();
            connection.FakeTcp.SentMessages.Should().BeEmpty(
                "cancellation of the outer context token (not the internal timeout CTS) must not be reported as a TIMEOUT directive");
        }
        finally
        {
            connection.Dispose();
        }
    }

    /// <summary>
    /// A handler returning <c>IAsyncEnumerable&lt;TResponse&gt;</c> must have every yielded item sent,
    /// with the LAST item (and only the last) marked <see cref="IPacketStreamable.IsEndOfStream"/> —
    /// the runtime, not the handler, owns that bookkeeping. Covers the synchronous-completion path
    /// (<c>ExecuteTerminalHandler</c>'s fast path into <c>SendHandlerResponseAsync</c>).
    /// </summary>
    [Fact]
    public async Task ExecuteResolvedHandlerAsync_SyncStreamHandler_SendsAllItemsAndFlagsOnlyLastAsEndOfStream()
    {
        PacketDispatchOptions<TestPacket> options = new();
        StreamTestPacket[] yielded = [new() { Value = 1 }, new() { Value = 2 }, new() { Value = 3 }];

        static async IAsyncEnumerable<StreamTestPacket> ToStream(StreamTestPacket[] items)
        {
            foreach (StreamTestPacket item in items)
            {
                yield return item;
            }

            await Task.CompletedTask;
        }

        PacketHandler<TestPacket> descriptor = CreateDescriptor(
            (_, _) => new ValueTask<object?>(ToStream(yielded)));

        FakeConnection connection = new();
        try
        {
            await options.ExecuteResolvedHandlerAsync(
                descriptor, new TestPacket(), connection, reliable: true, encryptedOnWire: false).AsTask();

            connection.FakeTcp.SentMessages.Should().HaveCount(3,
                "every item the handler's stream yields must be sent as its own packet");
            yielded[0].IsEndOfStream.Should().BeFalse();
            yielded[1].IsEndOfStream.Should().BeFalse();
            yielded[2].IsEndOfStream.Should().BeTrue(
                "the runtime must mark the LAST yielded item as the end of the stream so the client's StreamAsync completes");
        }
        finally
        {
            connection.Dispose();
        }
    }

    /// <summary>
    /// Same contract as the synchronous test above, but for a handler that yields asynchronously
    /// (an <c>await</c> before the first item) — covers <c>AwaitHandlerAndRespondAsync</c>'s copy of
    /// the same stream branch.
    /// </summary>
    [Fact]
    public async Task ExecuteResolvedHandlerAsync_AsyncStreamHandler_SendsAllItemsAndFlagsOnlyLastAsEndOfStream()
    {
        PacketDispatchOptions<TestPacket> options = new();
        StreamTestPacket[] yielded = [new() { Value = 1 }, new() { Value = 2 }];

        static async IAsyncEnumerable<StreamTestPacket> ToStream(StreamTestPacket[] items)
        {
            foreach (StreamTestPacket item in items)
            {
                await Task.Yield();
                yield return item;
            }
        }

        PacketHandler<TestPacket> descriptor = CreateDescriptor(async (_, _) =>
        {
            await Task.Yield();
            return (object?)ToStream(yielded);
        });

        FakeConnection connection = new();
        try
        {
            await options.ExecuteResolvedHandlerAsync(
                descriptor, new TestPacket(), connection, reliable: true, encryptedOnWire: false).AsTask();

            connection.FakeTcp.SentMessages.Should().HaveCount(2);
            yielded[0].IsEndOfStream.Should().BeFalse();
            yielded[1].IsEndOfStream.Should().BeTrue();
        }
        finally
        {
            connection.Dispose();
        }
    }

    /// <summary>
    /// A handler marked [PacketEncryption(true)] must be denied with ProtocolReason.FORBIDDEN
    /// when the packet context reports the frame did not arrive encrypted on the wire —
    /// EncryptedOnWire is the dedicated audit signal CanExecute reads (the packet header's
    /// transient ENCRYPTED flag is cleared by the inbound cipher transforms and cannot be
    /// used for this check).
    /// </summary>
    [Fact]
    public void CanExecute_EncryptionRequiredAndFrameNotEncryptedOnWire_DeniesWithForbidden()
    {
        PacketHandler<TestPacket> descriptor = CreateDescriptor(
            (_, _) => new ValueTask<object?>((object?)null), requireEncryption: true);

        PacketContext<TestPacket> context = new();
        FakeConnection connection = new();
        try
        {
            context.Initialize(
                new TestPacket(), connection, descriptor.Metadata,
                reliable: true, encryptedOnWire: false);

            bool canExecute = descriptor.CanExecute(context, out ProtocolReason denyReason);

            canExecute.Should().BeFalse();
            denyReason.Should().Be(ProtocolReason.FORBIDDEN);
        }
        finally
        {
            context.Return();
            connection.Dispose();
        }
    }

    /// <summary>
    /// A handler marked [PacketEncryption(true)] must be allowed to execute when the packet
    /// context reports the frame did arrive encrypted on the wire.
    /// </summary>
    [Fact]
    public void CanExecute_EncryptionRequiredAndFrameEncryptedOnWire_Allows()
    {
        PacketHandler<TestPacket> descriptor = CreateDescriptor(
            (_, _) => new ValueTask<object?>((object?)null), requireEncryption: true);

        PacketContext<TestPacket> context = new();
        FakeConnection connection = new();
        try
        {
            context.Initialize(
                new TestPacket(), connection, descriptor.Metadata,
                reliable: true, encryptedOnWire: true);

            bool canExecute = descriptor.CanExecute(context, out ProtocolReason denyReason);

            canExecute.Should().BeTrue();
            denyReason.Should().Be(ProtocolReason.NONE);
        }
        finally
        {
            context.Return();
            connection.Dispose();
        }
    }

    private static PacketHandler<TestPacket> CreateDescriptor(
        Func<object?, PacketContext<TestPacket>, ValueTask<object?>> invoker,
        int timeoutMs = 0, bool requireEncryption = false)
        => CreateDescriptor<TestPacket>(invoker, timeoutMs, requireEncryption);

    private static PacketHandler<TPacket> CreateDescriptor<TPacket>(
        Func<object?, PacketContext<TPacket>, ValueTask<object?>> invoker,
        int timeoutMs = 0, bool requireEncryption = false)
        where TPacket : IPacket
    {
        PacketTimeoutAttribute? timeout = timeoutMs > 0 ? new PacketTimeoutAttribute(timeoutMs) : null;
        PacketEncryptionAttribute? encryption = requireEncryption ? new PacketEncryptionAttribute(true) : null;
        PacketMetadata metadata = new(
            opCode: new PacketOpcodeAttribute((ushort)1),
            timeout: timeout,
            permission: null,
            encryption: encryption,
            rateLimit: null,
            transport: null);

        return new PacketHandler<TPacket>(
            opCode: 1,
            metadata: metadata,
            controllerInstance: null,
            methodName: "FakeHandler",
            returnType: typeof(object),
            compiledInvoker: invoker,
            expectedPacketType: typeof(TPacket));
    }

    private sealed class FakeSequenceCounter : ISequenceCounter
    {
        private uint _value;
        public uint Next() => ++_value;
        public uint Current() => _value;
        public void Reset(uint newValue = 0) => _value = newValue;
        public bool IsValid(uint? receivedSeq, uint window = 0) => true;
        public void UpdateTo(uint receivedSeq) => _value = receivedSeq;
        public void ResumeFrom(uint lastKnownSeq, uint safetyGap = 1000) => _value = lastKnownSeq;
        public bool IsApproachingOverflow(uint margin = 1_000_000) => false;
    }

    private sealed class FakeTransport : IConnection.ITransport
    {
        public System.Collections.Generic.List<byte[]> SentMessages { get; } = [];

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

    private sealed class FakeConnection : IConnection
    {
        public FakeTransport FakeTcp { get; } = new();

        public bool IsDisposed { get; private set; }
        public bool IsUdpCreated => false;
        public ulong ConnectionId => 1;
        public string? UserId { get; set; }
        public long UpTime => 0;
        public long LastPingTime => 0;
        public bool ExcludeFromIdleTimeout { get; set; }
        public IOpCodeExtractor PacketClassifier => null!;
        public INetworkEndpoint NetworkEndpoint => null!;
        public IObjectMap<AttributeKey, object> Attributes { get; } = ObjectMap<AttributeKey, object>.Rent();
        public ConcurrentDictionary<ushort, object> RateLimitCache { get; } = new();
        public Bytes32 Secret { get; set; }
        public PermissionLevel Level { get; set; }
        public CipherSuiteType Algorithm { get; set; }

        public IConnection.ITransport TCP => FakeTcp;
        public IConnection.ITransport? UDP => null;

        public event EventHandler<IConnectionEventArgs>? ConnectionClosed;
        public event EventHandler<IConnectionEventArgs>? MessageProcessing;
        public event EventHandler<IConnectionEventArgs>? MessageProcessed;

        public void Disconnect(string? reason = null) { }
        public void Dispose() => IsDisposed = true;
        public int ErrorCount => 0;
        public void IncrementErrorCount() { }

        public int IdleTimeoutMs { get; set; } = 60000;
        public void UpdateIdleTimeout(int newTimeoutMs) => IdleTimeoutMs = newTimeoutMs;
    }
#endif

    private sealed class FakeSessionService : ISessionService
    {
        public System.Threading.Tasks.ValueTask SaveSessionAsync(IConnection connection, System.Threading.CancellationToken cancellationToken = default)
            => System.Threading.Tasks.ValueTask.CompletedTask;

        public System.Threading.Tasks.ValueTask<SessionScope> ConsumeAsync(ulong sessionToken, Func<SessionEntry, bool>? predicate = null, System.Threading.CancellationToken cancellationToken = default)
            => new(new SessionScope(null));

        public void Dispose() { }
    }

    private sealed class TestPacket : IPacket
    {
        public int Length => 0;
        public PacketHeader Header { get; set; }
        public byte[] Serialize() => [];
        public int Serialize(Span<byte> buffer) => 0;
    }

    private sealed class StreamTestPacket : IPacket, IPacketStreamable
    {
        public int Length => 1;
        public PacketHeader Header { get; set; }
        public bool IsEndOfStream { get; set; }
        public int Value { get; init; }
        public byte[] Serialize() => [(byte)Value];
        public int Serialize(Span<byte> buffer)
        {
            buffer[0] = (byte)Value;
            return 1;
        }
    }
}











