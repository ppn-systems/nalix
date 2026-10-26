// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nalix.Abstractions;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Network.Connections;
using Xunit;

namespace Nalix.Network.Tests.Connections;

/// <summary>
/// The hub registers a new connection while its receive loop may already dispatch the first frame, so both
/// threads can reach <see cref="IConnection.Attributes"/> before it exists. They must share one map; otherwise
/// the later write discards the other's entries (the handshake slot) and the handshake fails with STATE_VIOLATION.
/// </summary>
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
public sealed class ConnectionAttributesConcurrencyTests
{
    private const int Threads = 16;
    private const int Rounds = 200;

    private static readonly IOpCodeExtractor s_extractor = new TestOpCodeExtractor();
    private static readonly AttributeKey s_keyA = AttributeKey.FromName("test.concurrency.a");
    private static readonly AttributeKey s_keyB = AttributeKey.FromName("test.concurrency.b");

    [Fact]
    public async Task Attributes_FirstAccessFromManyThreads_EveryThreadGetsTheSameMap()
    {
        for (int round = 0; round < Rounds; round++)
        {
            using Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            using Connection connection = new(socket, s_extractor, new IPEndPoint(IPAddress.Loopback, 0));

            IObjectMap<AttributeKey, object>[] seen = await RaceAsync(() => connection.Attributes);

            seen.Distinct().Should().ContainSingle("the first access must publish exactly one map");
        }
    }

    [Fact]
    public async Task Attributes_WritesFromTwoThreadsOnFirstAccess_BothSurvive()
    {
        for (int round = 0; round < Rounds; round++)
        {
            using PassthroughConnection connection = new(s_extractor, new IPEndPoint(IPAddress.Loopback, 9000));
            using Barrier barrier = new(2);

            Task first = Task.Run(() => { barrier.SignalAndWait(); connection.Attributes[s_keyA] = 1; });
            Task second = Task.Run(() => { barrier.SignalAndWait(); connection.Attributes[s_keyB] = 2; });
            await Task.WhenAll(first, second);

            connection.Attributes.ContainsKey(s_keyA).Should().BeTrue();
            connection.Attributes.ContainsKey(s_keyB).Should().BeTrue();
        }
    }

    private static async Task<T[]> RaceAsync<T>(Func<T> read)
    {
        using Barrier barrier = new(Threads);
        List<Task<T>> tasks = [];
        for (int i = 0; i < Threads; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                barrier.SignalAndWait();
                return read();
            }));
        }

        return await Task.WhenAll(tasks);
    }

    private sealed class TestOpCodeExtractor : IOpCodeExtractor
    {
        public ushort Extract(ReadOnlySpan<byte> payload) =>
            payload.Length >= 2 ? BinaryPrimitives.ReadUInt16LittleEndian(payload[0..]) : (ushort)0;
    }
}
