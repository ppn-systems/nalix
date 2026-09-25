// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using Microsoft.Extensions.Logging;
using Nalix.Abstractions.Injection;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Network.Protocols;
using Nalix.Runtime.Dispatching;

namespace Nalix.Hosting.Protocols;

/// <summary>
/// A ready-to-use protocol that forwards all inbound packets to the dispatch pipeline.
/// Use this when your server does not need custom protocol-level logic.
/// </summary>
/// <remarks>
/// <para>
/// Most Nalix servers only need to route packets into the dispatcher.
/// <see cref="DefaultProtocol"/> eliminates the boilerplate of creating a protocol class
/// that simply calls <c>IPacketDispatch.HandlePacket</c>.
/// </para>
/// <para>Usage with the hosting builder:</para>
/// <code>
/// using var app = NetworkApplication.CreateBuilder()
///     .ListenTcp&lt;DefaultProtocol&gt;().Bind()
///     .MapHandlers&lt;MyHandler&gt;()
///     .Build();
/// </code>
/// </remarks>
[Injectable]
public sealed class DefaultProtocol : Protocol
{
    private readonly IPacketDispatch _dispatch;
    private readonly DefaultFrameProcessor _frameProcessor;
    private static readonly IOpCodeExtractor s_opCodeExtractor = new DefaultOpCodeExtractor();

    /// <inheritdoc/>
    public override IFrameProcessor FrameProcessor => _frameProcessor;

    /// <summary>
    /// Gets a value indicating whether packets are handed to the queued <see cref="PacketDispatchChannel"/>,
    /// whose <c>HandlePacket</c> only enqueues and never runs handlers on the caller's thread.
    /// </summary>
    internal bool UsesQueuedDispatch => _dispatch is PacketDispatchChannel;

    /// <inheritdoc/>
    public override IOpCodeExtractor OpCodeExtractor => s_opCodeExtractor;

    /// <summary>
    /// Creates a new <see cref="DefaultProtocol"/> that routes packets into the given dispatch pipeline.
    /// </summary>
    /// <param name="logger">The logger used for recording framework-level events.</param>
    /// <param name="dispatch">The packet dispatcher responsible for routing and handling packets.</param>
    /// <exception cref="ArgumentNullException"><paramref name="dispatch"/> or <paramref name="logger"/> is <see langword="null"/>.</exception>
    public DefaultProtocol(ILogger logger, IPacketDispatch dispatch)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(dispatch);

        _dispatch = dispatch;
        _frameProcessor = new DefaultFrameProcessor(logger, this);

        this.IsAccepting = true;
        this.KeepConnectionOpen = true;
    }

    /// <inheritdoc />
    public override void ProcessMessage(object? sender, IConnectionEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Lease is null)
        {
            return;
        }

        _dispatch.HandlePacket(args.Lease, args.Connection);
    }

    /// <inheritdoc />
    protected override bool ValidateConnection(IConnection connection) => true;
}
