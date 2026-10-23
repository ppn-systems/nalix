// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using Nalix.Abstractions;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Security;
using Nalix.Abstractions.Serialization;
using Nalix.Codec.DataFrames;
using Nalix.Codec.Security.Engine;
using Nalix.Codec.Serialization;
using Nalix.Codec.Transforms;
using Nalix.Environment.Memory;
using Nalix.Framework.Memory.Objects;
using Nalix.Framework.Options;

namespace Nalix.Codec.Tests.Serialization;

[Collection(Nalix.Codec.Tests.Scrubbing.PooledBufferScrubCollection.Name)]
public sealed partial class CodecSerializationSecurityReproductionTests
{
    // =========================================================================
    // 1. [CRIT-04] Silent Deserialization Failure in LiteSerializer
    // =========================================================================

    [Fact]
    public void Crit04_LiteSerializer_Deserialize_TruncatedPayload_ThrowsOnAllOverloads()
    {
        // A string claiming 50 UTF-8 bytes but only 2 bytes follow.
        byte[] truncated = new byte[6];
        BitConverter.GetBytes(50).CopyTo(truncated, 0);
        truncated[4] = (byte)'a';
        truncated[5] = (byte)'b';

        // 1. byte[], ref T
        string? val1 = null;
        _ = Assert.ThrowsAny<SerializationFailureException>(() => LiteSerializer.Deserialize(truncated, ref val1));

        // 2. byte[], out int
        _ = Assert.ThrowsAny<SerializationFailureException>(() => LiteSerializer.Deserialize<string>(truncated, out _));

        // 3. ReadOnlyMemory<byte>, ref T
        string? val3 = null;
        ReadOnlyMemory<byte> mem = truncated;
        _ = Assert.ThrowsAny<SerializationFailureException>(() => LiteSerializer.Deserialize(mem, ref val3));

        // 4. ReadOnlyMemory<byte>, out int
        _ = Assert.ThrowsAny<SerializationFailureException>(() => LiteSerializer.Deserialize<string>(mem, out _));

        // 5. ReadOnlySpan<byte>, ref T
        string? val5 = null;
        _ = Assert.ThrowsAny<SerializationFailureException>(() => LiteSerializer.Deserialize(truncated.AsSpan(), ref val5));

        // 6. ReadOnlySpan<byte>, out int
        _ = Assert.ThrowsAny<SerializationFailureException>(() => LiteSerializer.Deserialize<string>(truncated.AsSpan(), out _));
    }

    [Fact]
    public void Crit04_LiteSerializer_Fill_TruncatedPayload_ThrowsOnAllOverloads()
    {
        // Truncated List<int>: claims 10 elements (40 bytes) but only has 4 bytes after count.
        byte[] truncated = new byte[8];
        BitConverter.GetBytes(10).CopyTo(truncated, 0);
        BitConverter.GetBytes(42).CopyTo(truncated, 4);

        List<int> target = [];

        // 1. byte[]
        _ = Assert.ThrowsAny<SerializationFailureException>(() => LiteSerializer.Fill(truncated, ref target));

        // 2. ReadOnlySpan<byte>
        _ = Assert.ThrowsAny<SerializationFailureException>(() => LiteSerializer.Fill(truncated.AsSpan(), ref target));

        // 3. ReadOnlyMemory<byte>
        ReadOnlyMemory<byte> mem = truncated;
        _ = Assert.ThrowsAny<SerializationFailureException>(() => LiteSerializer.Fill(mem, ref target));
    }

    // =========================================================================
    // 2. [HIGH-10] 16 MB Allocation Bomb in MemoryFormatter / ReadOnlyMemoryFormatter
    // =========================================================================

    [Fact]
    public void High10_MemoryFormatter_HostileLength_DoesNotAllocateBeforeCheckingRemainingBytes()
    {
        // 4-byte payload declaring 1,000,000 long elements (8 MB) with 0 element bytes remaining.
        byte[] hostile = BitConverter.GetBytes(1_000_000);
        IFormatter<Memory<long>> formatter = FormatterProvider.Get<Memory<long>>();

        // Warm up static configuration / JIT
        DataReader warmup = new(hostile);
        _ = formatter.Deserialize(ref warmup);

        long before = GC.GetAllocatedBytesForCurrentThread();
        DataReader reader = new(hostile);
        Memory<long> memOut = formatter.Deserialize(ref reader);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(reader.IsFailed);
        Assert.True(memOut.IsEmpty);
        Assert.True(allocated < 16_384, $"Expected no large allocation, but allocated {allocated} bytes.");
    }

    [Fact]
    public void High10_ReadOnlyMemoryFormatter_HostileLength_DoesNotAllocateBeforeCheckingRemainingBytes()
    {
        byte[] hostile = BitConverter.GetBytes(1_000_000);
        IFormatter<ReadOnlyMemory<long>> formatter = FormatterProvider.Get<ReadOnlyMemory<long>>();

        DataReader warmup = new(BitConverter.GetBytes(0));
        _ = formatter.Deserialize(ref warmup);

        long before = GC.GetAllocatedBytesForCurrentThread();
        DataReader reader = new(hostile);
        ReadOnlyMemory<long> memOut = formatter.Deserialize(ref reader);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(reader.IsFailed);
        Assert.True(memOut.IsEmpty);
        Assert.True(allocated < 16_384, $"Expected no large allocation, but allocated {allocated} bytes.");
    }

    // =========================================================================
    // 3. [HIGH-11] Blind Deserialization Loop on Poisoned DataReader
    // =========================================================================

    [Fact]
    public void High11_DictionaryFormatter_TruncatedStringKey_FailsCleanlyWithoutArgumentNullException()
    {
        // Count = 2, followed by only 2 bytes (enough for CollectionGuard count=2*1, but not enough for ReadInt32 string length).
        // Previously, StringFormatter returned null! on poisoned reader, and dict[null!] threw ArgumentNullException!
        byte[] truncated = new byte[6];
        BitConverter.GetBytes(2).CopyTo(truncated, 0);

        Dictionary<string, int>? dict = null;
        _ = Assert.ThrowsAny<SerializationFailureException>(() => LiteSerializer.Deserialize(truncated, ref dict));
        Assert.Null(dict);
    }

    [Fact]
    public void High11_GeneratedClassFormatter_DoesNotCreateInstancesWhenReaderIsAlreadyPoisoned()
    {
        CountingDto.ResetCount();

        // Array of 5 CountingDto items, with 5 zero bytes following count so CollectionGuard(5, 1) passes.
        // Each CountingDto needs 8 bytes (long). Item 0 fails after 5 bytes, poisoning reader.
        // Items 1..4 must NOT call CountingDto.Create() once reader.IsFailed is true.
        byte[] truncated = new byte[4 + 5];
        BitConverter.GetBytes(5).CopyTo(truncated, 0);

        CountingDto[]? arr = null;
        _ = Assert.ThrowsAny<SerializationFailureException>(() => LiteSerializer.Deserialize(truncated, ref arr));
        Assert.Null(arr);
        Assert.True(CountingDto.CreateCount <= 1, $"Expected at most 1 Create() call before aborting on poisoned reader, got {CountingDto.CreateCount}.");
    }

    // =========================================================================
    // 4. [HIGH-12] Leb128 Pointer Buffer Overflow, DataWriter.Expand, BufferLease
    // =========================================================================

    [Fact]
    public void High12_Leb128_Write_And_WriteUnrolled_ThrowWhenBufferTooSmall()
    {
        byte[] destination = new byte[5];
        destination[1] = 0xAA;
        destination[2] = 0xBB;

        // 0x10_0000u requires 3 bytes in LEB128, but we pass a 1-byte slice.
        // Must throw instead of corrupting destination[1] and destination[2] via pointer arithmetic.
        _ = Assert.ThrowsAny<ArgumentException>(() => Leb128.Write(destination.AsSpan(0, 1), 0x10_0000u));
        Assert.Equal(0xAA, destination[1]);
        Assert.Equal(0xBB, destination[2]);

        _ = Assert.ThrowsAny<ArgumentException>(() => Leb128.WriteUnrolled(destination.AsSpan(0, 1), 0x10_0000u));
        Assert.Equal(0xAA, destination[1]);
        Assert.Equal(0xBB, destination[2]);
    }

    [Fact]
    public void High12_BufferLease_RetainAfterDispose_ThrowsWithoutCorruptingRefCount()
    {
        BufferLease lease = new();

        // Calling Retain on an uninitialized/disposed lease (_refCount == 0) must throw and must NOT leave _refCount == 1.
        _ = Assert.ThrowsAny<Exception>(lease.Retain);

        // Even if we set a backing buffer via reflection or test after Dispose, _refCount must remain 0.
        System.Reflection.FieldInfo? refCountField = typeof(BufferLease).GetField("_refCount", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(refCountField);
        Assert.Equal(0, (int)refCountField!.GetValue(lease)!);
    }

    [Fact]
    public void High12_BufferLease_RentWithHeadroom_IntegerOverflow_ThrowsOverflowException()
    {
        _ = Assert.Throws<OverflowException>(() => BufferLease.Rent(int.MaxValue, zeroOnDispose: false, headroom: 2));
    }

    // =========================================================================
    // 5. [MED-08 & MED-09] PacketBase Pool Leak, Struct Padding, AeadEngine AAD Scrub, FrameTransformer
    // =========================================================================

    [Fact]
    public void Med08_PacketBase_Deserialize_InvalidNullableMarker_ReturnsNullAndDisposesPooledPacket()
    {
        using ObjectPoolManager poolMgr = new(new ObjectPoolOptions { EnableMetrics = true });
        PacketRegistry.Configure(poolMgr);
        try
        {
            Med08NullablePacket valid = new() { OptionalValue = 123 };
            byte[] bytes = valid.Serialize();
            // Corrupt the 1-byte nullable marker right after the 6-byte header to 0x02 (invalid marker)
            bytes[PacketHeader.Size] = 0x02;

            Med08NullablePacket? result = Med08NullablePacket.Deserialize(bytes);

            Assert.Null(result);
            Dictionary<string, object> info = poolMgr.GetTypeInfo<Med08NullablePacket>();
            Assert.Equal(0L, info["Outstanding"]);
        }
        finally
        {
            PacketRegistry.Configure(null);
        }
    }

    [Fact]
    public void Med08_PacketSchemaGenerator_PaddedUnmanagedStruct_LengthMatchesSerializedBytes()
    {
        PaddedStructPacket packet = new()
        {
            Padded = new PaddedUnmanagedStruct { A = 1, B = 0x12345678 }
        };

        byte[] serialized = packet.Serialize();

        Assert.Equal(Unsafe.SizeOf<PaddedUnmanagedStruct>(), 8);
        Assert.Equal(serialized.Length, packet.Length);
        Assert.Equal(serialized.Length, PacketSchema<PaddedStructPacket>.StaticSize);
    }

    [Fact]
    public void Med09_AeadEngine_LargeAad_ZeroesRentedAadBeforeReturningToPool()
    {
        byte[] secretAad = new byte[300];
        Array.Fill(secretAad, (byte)0x5A);

        AadSpyPool spy = new(secretAad.AsSpan(0, 32).ToArray());
        BufferLease.ByteArrayPool.Configure(spy);
        try
        {
            byte[] key = new byte[32];
            byte[] nonce = new byte[12];
            byte[] plaintext = [1, 2, 3, 4];
            byte[] ciphertext = new byte[128];

            AeadEngine.Encrypt(key, plaintext, ciphertext, nonce, secretAad, seq: 1, CipherSuiteType.Chacha20Poly1305, out int written);

            byte[] decrypted = new byte[plaintext.Length];
            AeadEngine.Decrypt(key, ciphertext.AsSpan(0, written), decrypted, secretAad, out _);

            Assert.Empty(spy.UnzeroedReturns);
        }
        finally
        {
            BufferLease.ByteArrayPool.Configure(new DefaultArrayPoolWrapper());
        }
    }

    [Fact]
    public void Med09_FrameTransformer_GetPlaintextLength_EnvelopeShorterThanOffset_ThrowsInternalErrorException()
    {
        byte[] tooShort = new byte[FrameTransformer.Offset - 1];
        _ = Assert.ThrowsAny<InternalErrorException>(() => FrameTransformer.GetPlaintextLength(tooShort));
    }

    // =========================================================================
    // Helper Types
    // =========================================================================

    [GenerateFormatter]
    [SerializePackable(SerializeLayout.Explicit)]
    public sealed class CountingDto
    {
        public static int CreateCount { get; private set; }
        public static void ResetCount() => CreateCount = 0;

        public static CountingDto Create()
        {
            CreateCount++;
            return new CountingDto();
        }

        [SerializeOrder(0)]
        public long Value { get; set; }
    }

    public struct PaddedUnmanagedStruct
    {
        public byte A;
        public int B;
    }

    [Packet]
    [GenerateFormatter]
    [SerializePackable(SerializeLayout.Explicit)]
    public sealed partial class PaddedStructPacket : PacketBase<PaddedStructPacket>, IPacketStaticOpcode
    {
        public static ushort StaticOpCode => 0x7F01;

        [SerializeOrder(0)]
        public PaddedUnmanagedStruct Padded { get; set; }
    }

    [Packet]
    [GenerateFormatter]
    [SerializePackable(SerializeLayout.Explicit)]
    public sealed partial class Med08NullablePacket : PacketBase<Med08NullablePacket>, IPacketStaticOpcode
    {
        public static ushort StaticOpCode => 0x7F02;

        [SerializeOrder(0)]
        public int? OptionalValue { get; set; }
    }

    private sealed class AadSpyPool(byte[] marker) : IBufferPoolManager
    {
        public ConcurrentBag<int> UnzeroedReturns { get; } = [];

        public byte[] Rent(int minimumLength = 256) => ArrayPool<byte>.Shared.Rent(minimumLength);

        public void Return(byte[]? array, bool arrayClear = false)
        {
            if (array is null)
            {
                return;
            }

            if (!arrayClear && array.AsSpan().IndexOf(marker) >= 0)
            {
                this.UnzeroedReturns.Add(array.Length);
            }

            ArrayPool<byte>.Shared.Return(array, arrayClear);
        }

        public string GenerateReport() => string.Empty;
        public void WriteReportData(System.Text.Json.Utf8JsonWriter writer) { }
    }

    private sealed class DefaultArrayPoolWrapper : IBufferPoolManager
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
        public void WriteReportData(System.Text.Json.Utf8JsonWriter writer) { }
    }
}
