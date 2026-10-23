using Nalix.Environment.Memory;
using Nalix.Abstractions.Serialization;
// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Nalix.Analyzers.Tests;

internal static class TestSources
{
    public const string Prelude = """
global using System;
global using System.Threading;
global using System.Threading.Tasks;
global using Nalix.Codec.DataFrames;
global using Nalix.Network.Routing;
global using Nalix.Abstractions.Networking.Packets;
global using Nalix.Abstractions.Middleware;
global using Nalix.Runtime.Dispatching;

namespace Nalix.Codec.Serialization
{
    public enum SerializeLayout : byte { Sequential = 0, Explicit = 1 }
    public interface IFixedSizeSerializable { }
    public sealed class SerializePackableAttribute : Attribute { public SerializePackableAttribute(SerializeLayout layout) { } }
    public sealed class SerializeHeaderAttribute : Attribute { public SerializeHeaderAttribute(int order) { } }
    public sealed class SerializeOrderAttribute : Attribute { public SerializeOrderAttribute(int order) { } }
    public sealed class SerializeIgnoreAttribute : Attribute { }
    public sealed class SerializeDynamicSizeAttribute : Attribute { public SerializeDynamicSizeAttribute(int size = 0) { } }
}

namespace Nalix.Abstractions.Networking.Packets
{
    public enum PacketHeaderOffset { MagicNumber = 0, OpCode = 4, Flags = 6, Priority = 7, Transport = 8, SequenceId = 9, Region = 12 }
    [Nalix.Codec.Serialization.SerializePackable(Nalix.Codec.Serialization.SerializeLayout.Explicit)]
    public interface IPacket { }
    public interface IPacketRegistry { }
    public interface IPacketDeserializer<TPacket> where TPacket : IPacket { }
    public interface IPacketStaticOpcode { static abstract ushort StaticOpCode { get; } }
    public sealed class PacketOpcodeAttribute : Attribute { public PacketOpcodeAttribute(ushort opcode) { } }
    public sealed class PacketAttribute : Attribute { }
    public sealed class PacketHandlerAttribute : Attribute
    {
        public PacketHandlerAttribute(string? name = null) { }
    }
    public enum PacketFlags : ushort { None = 0 }
    public enum PacketPriority : byte { Normal = 0 }
}

namespace Nalix.Abstractions.Pooling
{
    public interface IPoolable { void ResetForPool(); }
    public sealed class SkipCleanAttribute : Attribute { }
}

namespace Nalix.Abstractions.Injection
{
    public sealed class InjectAttribute : Attribute { }
}

namespace Nalix.Abstractions.Networking
{
    public interface IConnection { }
}

namespace Nalix.Abstractions.Middleware
{
    public enum MiddlewareStage : byte { Inbound, Outbound, Both }
    public sealed class MiddlewareOrderAttribute : Attribute { public MiddlewareOrderAttribute(int order) { } }
    public sealed class MiddlewareStageAttribute : Attribute
    {
        public MiddlewareStageAttribute(MiddlewareStage stage) { }
        public bool AlwaysExecute { get; init; }
    }
}

namespace Nalix.Abstractions.Injection
{
    public interface IPacketScope : IDisposable, IAsyncDisposable { }
    public sealed class FromScopeAttribute : Attribute { }
}

namespace Nalix.Abstractions
{
    public interface IBufferLease { }
    public sealed class ConfigurationIgnoreAttribute : Attribute
    {
        public ConfigurationIgnoreAttribute(string? reason = null) { }
    }
    public sealed class ReservedOpcodePermittedAttribute : Attribute { }
}

namespace Nalix.Abstractions.Exceptions
{
    public static class ExceptionClassifier
    {
        public static bool IsNonFatal(Exception ex) => ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException);
    }
}

namespace Nalix.Codec.Pooling
{
    using Nalix.Abstractions.Networking.Packets;
    using Nalix.Codec.DataFrames;

    public readonly struct PacketScope<TPacket> : IDisposable where TPacket : PacketBase<TPacket>, IPacketStaticOpcode, new()
    {
        private readonly TPacket _packet;
        public PacketScope(TPacket packet) => _packet = packet;
        public TPacket Value => _packet;
        public void Dispose() { /* returns _packet to pool */ }
        public static implicit operator TPacket(PacketScope<TPacket> lease) => lease._packet;
    }
}

namespace Nalix.Network.Routing
{
    using Nalix.Abstractions.Networking.Packets;
    public sealed class PacketDispatchOptions<TPacket> where TPacket : IPacket
    {
        public PacketDispatchOptions<TPacket> WithHandler<TController>() where TController : class => this;
        public PacketDispatchOptions<TPacket> WithMiddleware(Nalix.Abstractions.Middleware.IPacketMiddleware<TPacket> middleware) => this;
        public PacketDispatchOptions<TPacket> WithDispatchLoopCount(int count) => this;
    }
}

namespace Nalix.Runtime.Dispatching
{
    using System.Reflection;
    using Nalix.Abstractions.Networking.Packets;
    using Nalix.Abstractions.Networking;

    public sealed class PacketContext<TPacket> : IPacketContext<TPacket> where TPacket : IPacket
    {
        public TPacket Packet => default!;
        public IConnection Connection => null!;
        public Nalix.Abstractions.Injection.IPacketScope Scope => null!;
        public CancellationToken CancellationToken => default;
    }

    public sealed class PacketMetadataBuilder
    {
        public PacketOpcodeAttribute? Opcode { get; set; }
        public void Add(Attribute attribute) { }
        public TAttribute? Get<TAttribute>() where TAttribute : Attribute => null;
    }

    public interface IPacketDispatch { }

    public interface IPacketMetadataProvider
    {
        void Populate(MethodInfo method, PacketMetadataBuilder builder);
    }
}

namespace Nalix.Abstractions.Middleware
{
    using Nalix.Abstractions;
    using Nalix.Abstractions.Networking;
    using Nalix.Abstractions.Networking.Packets;
    using Nalix.Abstractions.Networking.Packets;

    public interface IPacketMiddleware<TPacket> where TPacket : IPacket
    {
        ValueTask InvokeAsync(IPacketContext<TPacket> context, Func<CancellationToken, ValueTask> next);
    }

}

namespace Nalix.Abstractions.Networking.Packets
{
    public interface IPacketContext<TPacket> where TPacket : IPacket
    {
        TPacket Packet { get; }
        IConnection Connection { get; }
        CancellationToken CancellationToken { get; }
    }
}
namespace Nalix.Codec.DataFrames
{
    using Nalix.Abstractions.Networking.Packets;

    public abstract class PacketBase<TSelf> : IPacket, IPacketDeserializer<TSelf> where TSelf : PacketBase<TSelf>
    {
        public virtual void ResetForPool() { }
        public static TSelf Deserialize(ReadOnlySpan<byte> buffer) => default!;
    }

    public sealed class PacketRegistryFactory
    {
        public PacketRegistryFactory RegisterPacket<TPacket>() where TPacket : IPacket => this;
    }
}

namespace Nalix.SDK.Transport
{
    using System.Threading;
    using System.Threading.Tasks;
    using Nalix.Abstractions;
    using Nalix.Abstractions.Networking.Packets;

    public interface ITransportOptions { }

    public interface IClientConnection : IDisposable
    {
        ITransportOptions Options { get; }
        bool IsConnected { get; }
        IPacketRegistry Catalog { get; }
        event EventHandler OnConnected;
        event EventHandler<Exception> OnDisconnected;
        event EventHandler<IBufferLease> OnMessageReceived;
        event EventHandler<long> OnBytesSent;
        event EventHandler<long> OnBytesReceived;
        event EventHandler<Exception> OnError;
        Task ConnectAsync(string? host = null, ushort? port = null, CancellationToken ct = default);
        Task DisconnectAsync();
        Task SendAsync(IPacket packet, CancellationToken ct = default);
        Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default);
    }

    public abstract class TcpSession : IClientConnection
    {
        public ITransportOptions Options => null!;
        public bool IsConnected => true;
        public IPacketRegistry Catalog => null!;
        public event EventHandler OnConnected { add { } remove { } }
        public event EventHandler<Exception> OnDisconnected { add { } remove { } }
        public event EventHandler<IBufferLease> OnMessageReceived { add { } remove { } }
        public event EventHandler<long> OnBytesSent { add { } remove { } }
        public event EventHandler<long> OnBytesReceived { add { } remove { } }
        public event EventHandler<Exception> OnError { add { } remove { } }
        public Task ConnectAsync(string? host = null, ushort? port = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync() => Task.CompletedTask;
        public Task SendAsync(IPacket packet, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendAsync(IPacket packet, bool encrypt, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Dispose() { }
    }
}

namespace Nalix.SDK.Options
{
    public sealed record RequestOptions
    {
        public static RequestOptions Default { get; } = new();
        public int TimeoutMs { get; init; }
        public int RetryCount { get; init; }
        public bool Encrypt { get; init; }
        public RequestOptions WithTimeout(int ms) => this with { TimeoutMs = ms };
        public RequestOptions WithRetry(int count) => this with { RetryCount = count };
        public RequestOptions WithEncrypt(bool encrypt = true) => this with { Encrypt = encrypt };
    }
}

namespace Nalix.SDK.Transport.Extensions
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Nalix.Abstractions.Networking.Packets;
    using Nalix.SDK.Options;
    using Nalix.SDK.Transport;

    public static class RequestExtensions
    {
        public static Task<TResponse> RequestAsync<TResponse>(
            this IClientConnection client,
            IPacket request,
            RequestOptions? options = null,
            Func<TResponse, bool>? predicate = null,
            CancellationToken ct = default)
            where TResponse : class, IPacket => Task.FromResult<TResponse>(null!);
    }
}

namespace Nalix.Environment.Configuration.Binding
{
    public abstract class ConfigurationLoader
    {
    }
}

namespace Nalix.Hosting
{
    public interface IProtocolBindingBuilder
    {
        IProtocolBindingBuilder OnPort(ushort port);
        IProtocolBindingBuilder WithFactory(object factory);
        IProtocolBindingBuilder WithAuthentication(object authen);
        NetworkApplicationBuilder Bind();
    }

    public sealed class NetworkApplicationBuilder
    {
        public NetworkApplicationBuilder UseBufferPoolManager(object? manager = null) => this;
        public NetworkApplicationBuilder UseConnectionHub(object? hub = null) => this;
        public IProtocolBindingBuilder ListenTcp<T>() => new StubBindingBuilder(this);
        public IProtocolBindingBuilder ListenUdp<T>() => new StubBindingBuilder(this);
        public NetworkApplicationBuilder MapHandlers<THandler>() => this;
        public NetworkApplicationBuilder AddMetadataProvider<TProvider>() => this;
        public object Build() => new();

        private sealed class StubBindingBuilder(NetworkApplicationBuilder parent) : IProtocolBindingBuilder
        {
            public IProtocolBindingBuilder OnPort(ushort port) => this;
            public IProtocolBindingBuilder WithFactory(object factory) => this;
            public IProtocolBindingBuilder WithAuthentication(object authen) => this;
            public NetworkApplicationBuilder Bind() => parent;
        }
    }
}
""";
}

















