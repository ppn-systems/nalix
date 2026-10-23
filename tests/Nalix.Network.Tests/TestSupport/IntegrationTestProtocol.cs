// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Hosting.Protocols;
using Nalix.Network.Protocols;
using Nalix.Runtime.Dispatching;

namespace Nalix.Network.Tests.TestSupport;

/// <summary>
/// A simple protocol for integration testing that dispatches packets to the Nalix runtime.
/// </summary>
[Nalix.Abstractions.Injection.Injectable]
public sealed class IntegrationTestProtocol : Protocol
{
    private readonly IPacketDispatch _dispatch;
    private readonly DefaultFrameProcessor _frameProcessor;

    public static int ProcessedCount;

    private sealed class StubOpCodeExtractor : IOpCodeExtractor
    {
        public ushort Extract(ReadOnlySpan<byte> payload) =>
            payload.Length >= 2 ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(payload[0..]) : (ushort)0;
    }

    public override IFrameProcessor FrameProcessor => _frameProcessor;
    public override IOpCodeExtractor OpCodeExtractor { get; } = new StubOpCodeExtractor();

    public IntegrationTestProtocol(IPacketDispatch dispatch)
    {
        _dispatch = dispatch;
        _frameProcessor = new DefaultFrameProcessor(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, this);
        this.KeepConnectionOpen = true;
        this.SetConnectionAcceptance(true);
    }

    public override void ProcessMessage(object? sender, IConnectionEventArgs args)
    {
        // For testing, we assume the payload is a valid packet frame
        // and we push it to the dispatcher.
        if (args.Lease is IBufferLease lease)
        {
            Interlocked.Increment(ref ProcessedCount);
            _dispatch.HandlePacket(lease, args.Connection);
        }
    }

    public override void OnAccept(IConnection connection, CancellationToken cancellationToken = default)
    {
        base.OnAccept(connection, cancellationToken);
    }
}
