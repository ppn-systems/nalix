// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Network.Connections;
using Nalix.Runtime.Options;
using Nalix.Runtime.Throttling;
using Xunit;

namespace Nalix.Runtime.Tests.Throttling;

public sealed class PolicyRateLimiterTests : IDisposable
{
    private readonly PolicyRateLimiter _limiter;
    private static readonly IOpCodeExtractor s_testOpCodeExtractor = new TestOpCodeExtractor();
    private static int s_opCodeCounter = 0x5000;
    private static int s_ipCounter = 1;

    private ushort NextOpCode() => (ushort)Interlocked.Increment(ref s_opCodeCounter);
    private string NextIp() => $"127.0.0.{Interlocked.Increment(ref s_ipCounter)}";

    public PolicyRateLimiterTests()
    {
        _limiter = new PolicyRateLimiter();
        
        // Isolate the test by using a fresh, strict limiter engine instead of the shared singleton
        var options = new TokenBucketOptions
        {
            CapacityTokens = 1,
            InitialTokens = 1,
            TokenScale = 100,
            RefillTokensPerSecond = 0.001, // Extremely slow refill to prevent flakiness
            MaxSoftViolations = 1,
            ShardCount = 8
        };
        
        var field = typeof(PolicyRateLimiter).GetField("_shared", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field?.SetValue(_limiter, new TokenBucketLimiter(options));
    }

    [Fact]
    public async Task Evaluate_WithSeparatePolicies_UsesSeparateBuckets()
    {
        using var scope = await ConnectedSocketScope.CreateAsync(NextIp());
        using var connection = new Connection(scope.ServerSocket, s_testOpCodeExtractor);

        ushort opA = NextOpCode();
        ushort opB = NextOpCode();

        var attrA = new PacketRateLimitAttribute(1, 1) { PolicyId = "PolicyA" };
        var contextA = new TestPacketContext(connection, opA, attrA);

        var attrB = new PacketRateLimitAttribute(1, 1) { PolicyId = "PolicyB" };
        var contextB = new TestPacketContext(connection, opB, attrB);

        _limiter.Evaluate(contextA).Allowed.Should().BeTrue();
        _limiter.Evaluate(contextB).Allowed.Should().BeTrue();

        _limiter.Evaluate(contextA).Allowed.Should().BeFalse();
        _limiter.Evaluate(contextB).Allowed.Should().BeFalse();
    }

    [Fact]
    public async Task Evaluate_WithSameIpAndDifferentOpCodes_UsesSeparateBuckets()
    {
        using var scope = await ConnectedSocketScope.CreateAsync(NextIp());
        using var connection = new Connection(scope.ServerSocket, s_testOpCodeExtractor);

        ushort op1 = NextOpCode();
        ushort op2 = NextOpCode();

        var attr = new PacketRateLimitAttribute(1, 1);
        var context1 = new TestPacketContext(connection, op1, attr);
        var context2 = new TestPacketContext(connection, op2, attr);

        _limiter.Evaluate(context1).Allowed.Should().BeTrue();
        _limiter.Evaluate(context2).Allowed.Should().BeTrue();
        
        _limiter.Evaluate(context1).Allowed.Should().BeFalse();
        _limiter.Evaluate(context2).Allowed.Should().BeFalse();
    }

    [Fact]
    public async Task Evaluate_WithInvalidBurst_DeniesWithHardLockout()
    {
        using var scope = await ConnectedSocketScope.CreateAsync(NextIp());
        using var connection = new Connection(scope.ServerSocket, s_testOpCodeExtractor);

        ushort op = NextOpCode();
        var attr = new PacketRateLimitAttribute(10, 0); 
        var context = new TestPacketContext(connection, op, attr);

        var decision = _limiter.Evaluate(context);
        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Be(TokenBucketLimiter.RateLimitReason.HardLockout);
    }

    [Fact]
    public async Task Evaluate_WithoutRateLimitAttribute_AllowsRequest()
    {
        using var scope = await ConnectedSocketScope.CreateAsync(NextIp());
        using var connection = new Connection(scope.ServerSocket, s_testOpCodeExtractor);

        ushort op = NextOpCode();
        var context = new TestPacketContext(connection, op, null);

        var decision = _limiter.Evaluate(context);
        decision.Allowed.Should().BeTrue();
        decision.Reason.Should().Be(TokenBucketLimiter.RateLimitReason.None);
    }

    [Fact]
    public async Task Evaluate_WithSamePolicyIdAcrossDifferentOpCodes_SharesSingleRateLimitBucket()
    {
        using var scope = await ConnectedSocketScope.CreateAsync(NextIp());
        using var connection = new Connection(scope.ServerSocket, s_testOpCodeExtractor);

        var sharedPolicy = new PacketRateLimitAttribute(1, 1) { PolicyId = "SharedAuthPolicy" };
        var contextA = new TestPacketContext(connection, 0x2001, sharedPolicy);
        var contextB = new TestPacketContext(connection, 0x2002, sharedPolicy);

        _limiter.Evaluate(contextA).Allowed.Should().BeTrue("first request under SharedAuthPolicy should be allowed");
        _limiter.Evaluate(contextB).Allowed.Should().BeFalse("second request with a different OpCode but the same PolicyId must share the same rate-limit bucket");
    }

    [Fact]
    public void GenerateReport_WhenCalled_ContainsSharedEngineSections()
    {
        string report = _limiter.GenerateReport();
        report.Should().Contain("PolicyRateLimiter Status");
        report.Should().Contain("Shared TokenBucket Engine Report");
    }

    public void Dispose()
    {
        _limiter.Dispose();
    }

    private sealed class TestPacketContext(IConnection connection, ushort opCode, PacketRateLimitAttribute? rateLimit) : IPacketContext<IPacket>
    {
        public bool IsReliable => true;
        public bool EncryptedOnWire => false;
        public bool SkipOutbound => false;
        public IPacket Packet { get; } = new TestPacket(opCode);
        public IConnection Connection { get; } = connection;
        public PacketMetadata Attributes { get; } = new PacketMetadata(
                new PacketOpcodeAttribute(opCode),
                timeout: null,
                permission: null,
                encryption: null,
                rateLimit: rateLimit,
                transport: null);

        public IPacketSender Sender => null!;
        public Nalix.Abstractions.Injection.IPacketScope Scope => null!;
        public CancellationToken CancellationToken => CancellationToken.None;
        public void ResetForPool() { }
    }

    private sealed class TestPacket(ushort opCode) : IPacket
    {
        public int Length => 0;
        public PacketHeader Header { get; set; } = new PacketHeader { OpCode = opCode };
        public byte[] Serialize() => [];
        public int Serialize(Span<byte> buffer) => 0;
    }

    private sealed class TestOpCodeExtractor : IOpCodeExtractor
    {
        public ushort Extract(ReadOnlySpan<byte> payload) =>
            payload.Length >= 2 ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(payload[0..]) : (ushort)0;
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

        public static async Task<ConnectedSocketScope> CreateAsync(string address)
        {
            var ip = IPAddress.Loopback;
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

