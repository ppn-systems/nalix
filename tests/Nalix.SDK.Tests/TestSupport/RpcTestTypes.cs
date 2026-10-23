// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Rpc;
using Nalix.Abstractions.Primitives;
using Nalix.SDK.Options;
using Nalix.SDK.Transport;
using Nalix.SDK.Transport.Extensions;
using Nalix.SDK.Transport.Rpc;
using Xunit;
using Nalix.SDK.Tests.TestSupport;

namespace Nalix.SDK.Tests.TestSupport;

public struct TestRpcRequest : IPacket
{
    public PacketHeader Header { get; set; }
    public int Length => 0;
    public byte[] Serialize() => [];
    public int Serialize(Span<byte> buffer) => 0;
}

public class TestRpcResponse : IPacket, IPacketStaticOpcode
{
    public PacketHeader Header { get; set; }
    public int Length => 0;
    public byte[] Serialize() => [];
    public int Serialize(Span<byte> buffer) => 0;
    public static ushort StaticOpCode => 1002;
}

public class TestRpcStreamResponse : IPacket, IPacketStaticOpcode, IPacketStreamable
{
    public PacketHeader Header { get; set; }
    public int Length => 0;
    public byte[] Serialize() => [];
    public int Serialize(Span<byte> buffer) => 0;
    public static ushort StaticOpCode => 1003;
    public bool IsEndOfStream { get; set; }
}

[RpcService]
public interface ITestRpcService
{
    ValueTask PingAsync(TestRpcRequest request);
    ValueTask PingWithEncryptAsync(TestRpcRequest request, bool? encrypt);
    ValueTask PingWithCtAsync(TestRpcRequest request, CancellationToken ct);

    RpcCall<TestRpcResponse> RequestAsync(TestRpcRequest request);
    RpcCall<TestRpcResponse> RequestWithOptionsAsync(TestRpcRequest request, RequestOptions? options);

    RpcStream<TestRpcStreamResponse> StreamAsync(TestRpcRequest request);
    RpcStream<TestRpcStreamResponse> StreamWithOptionsAsync(TestRpcRequest request, RequestOptions? options);
}
