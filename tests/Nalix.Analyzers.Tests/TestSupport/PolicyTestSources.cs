// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Nalix.Analyzers.Tests.TestSupport;

/// <summary>
/// Stubs for the types behind NALIX111–NALIX117. They are passed as a separate source instead of living in
/// <see cref="TestSources.Prelude"/>: with <c>PacketPermissionAttribute</c> defined, every handler in the other
/// analyzer tests would also (correctly) report NALIX111.
/// </summary>
internal static class PolicyTestSources
{
    public const string Stubs = """
namespace Nalix.Abstractions.Security
{
    public enum PermissionLevel : byte { NONE = 0, USER = 100, SYSTEM_ADMINISTRATOR = 225 }
    public enum PermissionEvaluation : byte { MinimumLevel = 0, StrictMatch = 1 }
}

namespace Nalix.Abstractions.Networking.Packets
{
    using Nalix.Abstractions.Security;

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class PacketPermissionAttribute : Attribute
    {
        public PacketPermissionAttribute() { }
        public PacketPermissionAttribute(PermissionLevel level) { }
        public PacketPermissionAttribute(PermissionLevel level, PermissionEvaluation evaluation) { }
    }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class PacketRateLimitAttribute(int requestsPerSecond, double burst = 1.0) : Attribute
    {
        public string? PolicyId { get; init; }
    }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class PacketTimeoutAttribute(int timeoutMilliseconds) : Attribute;

    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class PacketIdempotentAttribute : Attribute;
}

namespace Nalix.Runtime.Middleware.Standard
{
    using Nalix.Abstractions.Middleware;
    using Nalix.Abstractions.Networking.Packets;

    [MiddlewareOrder(-40)]
    [MiddlewareStage(MiddlewareStage.Inbound)]
    public class RateLimitMiddleware : IPacketMiddleware<IPacket>
    {
        public ValueTask InvokeAsync(IPacketContext<IPacket> context, Func<CancellationToken, ValueTask> next) => next(context.CancellationToken);
    }

    [MiddlewareOrder(-30)]
    [MiddlewareStage(MiddlewareStage.Inbound)]
    public class TimeoutMiddleware : IPacketMiddleware<IPacket>
    {
        public ValueTask InvokeAsync(IPacketContext<IPacket> context, Func<CancellationToken, ValueTask> next) => next(context.CancellationToken);
    }
}

namespace Nalix.SDK.Transport.Extensions
{
    using Nalix.Abstractions.Networking.Packets;
    using Nalix.SDK.Transport;

    public static class TransportSessionSubscriptions
    {
        public static IDisposable On<TPacket>(this IClientConnection client, Action<TPacket> handler, bool disposeAfter = true)
            where TPacket : class, IPacket => new Subscription();

        public static IDisposable OnExact<TPacket>(this IClientConnection client, Action<TPacket> handler)
            where TPacket : class, IPacket => new Subscription();

        public static IDisposable SubscribeTemp<TPacket>(this IClientConnection client, Action<TPacket> onMessage, Action<Exception>? onDisconnected = null)
            where TPacket : class, IPacket => new Subscription();

        [Obsolete("One-shot")]
        public static IDisposable OnOnce<TPacket>(this IClientConnection client, Func<TPacket, bool> predicate, Action<TPacket> handler)
            where TPacket : class, IPacket => new Subscription();

        private sealed class Subscription : IDisposable
        {
            public void Dispose() { }
        }
    }
}
""";
}
