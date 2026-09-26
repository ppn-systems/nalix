// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Generic;
using System.Threading.Tasks;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Framework.Memory.Objects;

namespace Nalix.Runtime.Dispatching;

/// <summary>
/// Provides a zero-reflection bridge to transition from a generic IPacket context
/// to a strongly-typed context for specific packet handlers.
/// </summary>
public static class PacketContextBridge
{
    private static readonly ObjectPoolManager s_pool = ObjectPoolManager.Shared;

    /// <summary>
    /// Creates a strongly-typed packet context by renting from the object pool
    /// and initializing it with the properties of the base context.
    /// </summary>
    /// <typeparam name="TConcrete">The concrete packet type.</typeparam>
    /// <typeparam name="TBase">The base packet type of the pipeline.</typeparam>
    /// <param name="baseContext">The original generic packet context.</param>
    /// <param name="concretePacket">The downcasted concrete packet.</param>
    /// <returns>A rented, initialized packet context for the specific type.</returns>
    public static PacketContext<TConcrete> Create<TConcrete, TBase>(PacketContext<TBase> baseContext, TConcrete concretePacket)
        where TConcrete : IPacket, new()
        where TBase : IPacket
    {
        System.ArgumentNullException.ThrowIfNull(baseContext);

        PacketContext<TConcrete> bridgeContext = s_pool.Get<PacketContext<TConcrete>>();

        // Call the internal Initialize method within Nalix.Runtime boundary
        bridgeContext.Initialize(
            concretePacket,
            baseContext.Connection,
            baseContext.Attributes,
            baseContext.IsReliable,
            baseContext.EncryptedOnWire,
            ownsPacket: false,
            baseContext.CancellationToken,
            scope: baseContext.Scope);

        return bridgeContext;
    }

    /// <summary>
    /// Returns a bridge context to the object pool without disposing the borrowed packet.
    /// The original base context retains packet ownership and is responsible for its disposal.
    /// </summary>
    /// <typeparam name="TConcrete">The concrete packet type.</typeparam>
    /// <param name="bridgeContext">The bridge context to return.</param>
    public static void Return<TConcrete>(PacketContext<TConcrete> bridgeContext)
        where TConcrete : IPacket, new()
    {
        System.ArgumentNullException.ThrowIfNull(bridgeContext);
        bridgeContext.Dispose();
    }

    /// <summary>
    /// Wraps a bridge-context handler's <see cref="IAsyncEnumerable{TChunk}"/> return value so
    /// <paramref name="bridgeContext"/> is returned to the pool only once the caller has fully
    /// consumed or disposed the stream, instead of the instant the enumerable object is constructed.
    /// </summary>
    /// <remarks>
    /// A handler that takes a bridge context (<c>IPacketContext&lt;TConcrete&gt;</c>) and returns
    /// <c>async IAsyncEnumerable&lt;TChunk&gt;</c> does not start running its body — including every
    /// read of <c>context</c> — until the first <c>MoveNextAsync</c> call. The generated invoker,
    /// however, only has the enumerable OBJECT the instant it calls the handler method; it cannot
    /// itself await the enumeration before returning. Cleaning the bridge context up in a
    /// <c>try/finally</c> around that call therefore returns it to the pool before the handler body has
    /// read anything from it — an uncontended pool slot reads back as nulled-out state
    /// (<see cref="System.NullReferenceException"/>, swallowed by the dispatch pipeline, so the stream
    /// silently never yields); a contended one reads back another connection's packet, connection, and
    /// sender (a cross-tenant data leak on a multi-tenant server). This method exists so the generator
    /// never has to choose between those two outcomes: <paramref name="bridgeContext"/> stays rented for
    /// the whole enumeration and is returned in this method's own <c>finally</c>, which runs when the
    /// consumer's <c>await foreach</c> completes OR when its enumerator is disposed early (cancellation,
    /// an exception downstream, or the caller simply stopping partway through).
    /// </remarks>
    /// <typeparam name="TConcrete">The concrete packet type the bridge context was created for.</typeparam>
    /// <typeparam name="TChunk">The stream's item type.</typeparam>
    /// <param name="source">The handler's own <see cref="IAsyncEnumerable{TChunk}"/> return value.</param>
    /// <param name="bridgeContext">The bridge context to keep alive until <paramref name="source"/> is exhausted or disposed.</param>
    public static async IAsyncEnumerable<TChunk> WrapStream<TConcrete, TChunk>(
        IAsyncEnumerable<TChunk> source, PacketContext<TConcrete> bridgeContext)
        where TConcrete : IPacket, new()
    {
        System.ArgumentNullException.ThrowIfNull(source);
        System.ArgumentNullException.ThrowIfNull(bridgeContext);

        try
        {
            await foreach (TChunk item in source.ConfigureAwait(false))
            {
                yield return item;
            }
        }
        finally
        {
            Return(bridgeContext);
        }
    }
}
