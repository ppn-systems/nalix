// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using Nalix.Abstractions;
using Nalix.Abstractions.Networking;
using Nalix.Hosting.Protocols;
using Nalix.Network.Protocols;
using Nalix.Runtime.Dispatching;
using Nalix.Tests.Shared;

namespace Nalix.Hosting.Tests.TestSupport;

internal static class TestUtils
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static int GetFreePort() => TestPorts.Next();

    /// <summary>
    /// A minimal protocol for integration testing that dispatches packets to the Nalix runtime.
    /// Mirrors the equivalent helper in Nalix.SDK.Tests/TestUtils.cs.
    /// </summary>
    [Nalix.Abstractions.Injection.Injectable]
    public sealed class IntegrationTestProtocol : Protocol
    {
        private readonly IPacketDispatch _dispatch;
        private readonly DefaultFrameProcessor _frameProcessor;

        private sealed class StubOpCodeExtractor : Nalix.Abstractions.Networking.Protocols.IOpCodeExtractor
        {
            public ushort Extract(ReadOnlySpan<byte> payload) =>
                payload.Length >= 2 ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(payload[0..]) : (ushort)0;
        }

        public override IFrameProcessor FrameProcessor => _frameProcessor;
        public override Nalix.Abstractions.Networking.Protocols.IOpCodeExtractor OpCodeExtractor { get; } = new StubOpCodeExtractor();

        public IntegrationTestProtocol(IPacketDispatch dispatch)
        {
            _dispatch = dispatch;
            _frameProcessor = new DefaultFrameProcessor(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, this);
            this.KeepConnectionOpen = true;
            this.SetConnectionAcceptance(true);
        }

        public override void ProcessMessage(object? sender, IConnectionEventArgs args)
        {
            if (args.Lease is IBufferLease lease)
            {
                _dispatch.HandlePacket(lease, args.Connection);
            }
        }

        public override void OnAccept(IConnection connection, CancellationToken cancellationToken = default)
        {
            base.OnAccept(connection, cancellationToken);
        }
    }
}
