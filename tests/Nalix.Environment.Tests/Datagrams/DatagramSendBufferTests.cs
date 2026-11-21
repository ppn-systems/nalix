// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Nalix.Abstractions;
using Nalix.Abstractions.Networking.Datagrams;
using Nalix.Environment.Datagrams;
using Nalix.Environment.Memory;

namespace Nalix.Environment.Tests.Datagrams;

/// <summary>
/// A fragmented message is copied once into a pooled buffer that its chunks share: the buffer must go back to the
/// pool exactly once, cleared, whether every chunk is acknowledged or the session closes first.
/// </summary>
public sealed class DatagramSendBufferTests : IDisposable
{
    // Message sizes no other test rents, so the tracking pool sees only the buffers of these tests.
    private static readonly int[] s_sizes = [20_011, 30_029, 45_007];

    private readonly TrackingPool _pool = new(s_sizes);

    public DatagramSendBufferTests() => BufferLease.ByteArrayPool.Configure(_pool);

    public void Dispose() => BufferLease.ByteArrayPool.Configure(new SharedPool());

    [Theory]
    [InlineData(DeliveryMethod.ReliableOrdered)]
    [InlineData(DeliveryMethod.ReliableUnordered)]
    public void FragmentedMessages_WhenEveryChunkIsAcknowledged_ReturnTheirBufferOnceAndCleared(DeliveryMethod method)
    {
        using NetworkEmulator net = new(seed: 61) { LossRate = 0.1 };
        byte[][] messages = [.. s_sizes.Select(Payload)];

        foreach (byte[] message in messages)
        {
            Assert.True(net.Client.TrySend(message, method, 1, 0, net.Now));
        }

        Assert.True(net.RunUntil(() => net.ServerHandler.Messages.Count == messages.Length && net.Client.BytesInFlight == 0, 600_000_000));

        Assert.Equal(s_sizes.Length, _pool.Rented.Count);
        Assert.All(_pool.Rented, buffer =>
        {
            Assert.Equal(1, _pool.Returns(buffer));
            Assert.All(buffer, b => Assert.Equal(0, b));
        });
    }

    [Fact]
    public void FragmentedMessages_WhenTheEngineIsDisposedWithChunksQueuedAndInFlight_ReturnTheirBufferOnce()
    {
        using NetworkEmulator net = new(seed: 62);
        Assert.True(net.RunUntil(() => net.Client.State == DatagramEngineState.Established));

        foreach (int size in s_sizes)
        {
            Assert.True(net.Client.TrySend(Payload(size), DeliveryMethod.ReliableOrdered, 1, 0, net.Now));
        }

        // Send part of the first message, then close with the rest in flight or still queued.
        net.Connected = false;
        for (int i = 0; i < 5; i++)
        {
            net.Step();
        }

        // Closing fails the queued chunks; the in-flight ones are released when the engine is disposed.
        Assert.True(net.Client.BytesInFlight > 0);
        net.Client.Close(DatagramCloseCode.NoError, null, net.Now);
        net.Client.Dispose();

        Assert.Equal(s_sizes.Length, _pool.Rented.Count);
        Assert.All(_pool.Rented, buffer => Assert.Equal(1, _pool.Returns(buffer)));
    }

    private static byte[] Payload(int size)
    {
        byte[] payload = new byte[size];
        new System.Random(size).NextBytes(payload);
        payload[(int)Nalix.Abstractions.Networking.Packets.PacketHeaderOffset.Flags] = 0; // Not a fragment header.
        return payload;
    }

    /// <summary>
    /// Hands out fresh arrays for the tracked sizes (so no other renter can ever hold them) and counts their returns;
    /// every other size goes to the shared pool.
    /// </summary>
    private sealed class TrackingPool(int[] trackedSizes) : IBufferPoolManager
    {
        private readonly ConcurrentDictionary<byte[], int> _returns = new(ReferenceEqualityComparer.Instance);

        public List<byte[]> Rented { get; } = [];

        public int Returns(byte[] buffer) => _returns.TryGetValue(buffer, out int count) ? count : 0;

        public byte[] Rent(int minimumLength = 256)
        {
            if (!trackedSizes.Contains(minimumLength))
            {
                return ArrayPool<byte>.Shared.Rent(minimumLength);
            }

            byte[] buffer = new byte[minimumLength];
            lock (this.Rented)
            {
                this.Rented.Add(buffer);
            }

            return buffer;
        }

        public void Return(byte[]? array, bool arrayClear = false)
        {
            if (array is null)
            {
                return;
            }

            if (trackedSizes.Contains(array.Length))
            {
                _ = _returns.AddOrUpdate(array, 1, static (_, count) => count + 1);
                return;
            }

            ArrayPool<byte>.Shared.Return(array, arrayClear);
        }

        public string GenerateReport() => string.Empty;

        public void WriteReportData(System.Text.Json.Utf8JsonWriter writer)
        {
        }
    }

    /// <summary>The default backend, restored after each test.</summary>
    private sealed class SharedPool : IBufferPoolManager
    {
        public byte[] Rent(int minimumLength = 256) => ArrayPool<byte>.Shared.Rent(minimumLength);

        public void Return(byte[]? array, bool arrayClear = false)
        {
            if (array is not null)
            {
                ArrayPool<byte>.Shared.Return(array, arrayClear);
            }
        }

        public string GenerateReport() => string.Empty;

        public void WriteReportData(System.Text.Json.Utf8JsonWriter writer)
        {
        }
    }
}
