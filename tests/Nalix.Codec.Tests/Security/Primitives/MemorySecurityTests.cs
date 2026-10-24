// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Linq;
using System.Reflection;
using System.Text;
using Nalix.Abstractions.Primitives;
using Nalix.Codec.Security;
using Nalix.Codec.Security.Hashing;
using Nalix.Codec.Security.Primitives;
using Nalix.Codec.Security.Symmetric;

namespace Nalix.Codec.Tests.Security.Primitives;

public sealed class MemorySecurityTests
{
    [Fact]
    public void ZeroMemory_WithArraySpanNullAndEmpty_ClearsBuffersWithoutThrowing()
    {
        byte[] arr = [1, 2, 3];
        Span<byte> span = stackalloc byte[] { 4, 5, 6 };

        MemorySecurity.ZeroMemory(arr);
        MemorySecurity.ZeroMemory(span);
        MemorySecurity.ZeroMemory((byte[])null!);
        MemorySecurity.ZeroMemory(Array.Empty<byte>());

        Assert.Equal([0, 0, 0], arr);
        Assert.True(span.ToArray().SequenceEqual(new byte[] { 0, 0, 0 }));
    }

    [Fact]
    public void ZeroMemory_With64ByteBuffer_ClearsEveryByte()
    {
        byte[] buffer = new byte[64];
        for (int i = 0; i < buffer.Length; i++)
        {
            buffer[i] = (byte)(i + 1);
        }

        Nalix.Codec.Security.Primitives.MemorySecurity.ZeroMemory(buffer);

        Assert.All(buffer, b => Assert.Equal(0, b));
    }
}
