// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Primitives;
using Nalix.Benchmarks.Shared;
using Nalix.Codec.DataFrames;

namespace Nalix.Runtime.Benchmarks.Packets;

/// <summary>
/// Measures the registry's op-code dispatch on the inbound path.
/// </summary>
/// <remarks>
/// The registry is keyed by <see cref="IPacketStaticOpcode.StaticOpCode"/>; it used to be keyed by a
/// magic number carried in the header, which no longer exists. The stub below is the smallest thing
/// that can be registered, so what the benchmark reports is the lookup and dispatch rather than any
/// real formatter.
/// </remarks>
[Config(typeof(NalixBenchmarkConfig))]
public class PacketRegistryBenchmarks
{
    private const ushort BenchmarkOpCode = 0x7F01;

    private byte[] _rawBytes = null!;

    /// <summary>Smallest packet the registry will accept, carrying nothing but its header.</summary>
    private readonly struct StubPacket(PacketHeader header) : IPacket, IPacketStaticOpcode
    {
        public static ushort StaticOpCode => BenchmarkOpCode;

        public int Length => PacketConstants.HeaderSize;

        public PacketHeader Header
        {
            get => header;
            set => throw new NotSupportedException("StubPacket header is read-only.");
        }

        public byte[] Serialize() => [];

        public int Serialize(Span<byte> buffer) => 0;
    }

    [GlobalSetup]
    public void Setup()
    {
        if (!PacketRegistry.IsBuilt)
        {
            PacketRegistry.RegisterGenerated<StubPacket>(
                nameof(StubPacket),
                static raw => new StubPacket(MemoryMarshal.Read<PacketHeader>(raw[..PacketConstants.HeaderSize])));

            PacketRegistry.Build();
        }

        PacketHeader header = new()
        {
            OpCode = BenchmarkOpCode,
            Flags = PacketFlags.NONE,
            Priority = PacketPriority.NONE,
            SequenceId = 1
        };

        _rawBytes = new byte[32];
        MemoryMarshal.Write(_rawBytes.AsSpan(0, PacketConstants.HeaderSize), in header);
    }

    [Benchmark]
    public bool TryDeserialize() => PacketRegistry.TryDeserialize(_rawBytes, out _);
}
