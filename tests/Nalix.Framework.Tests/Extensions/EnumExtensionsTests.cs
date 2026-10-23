// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Buffers.Binary;
using Nalix.Abstractions;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Primitives;
using Nalix.Codec.Extensions;
using Nalix.Environment.Extensions;
using Nalix.Framework.Extensions;

namespace Nalix.Framework.Tests.Extensions;

public sealed class EnumExtensionsTests
{
    [Fact]
    public void AddFlagAndRemoveFlag_WithByteFlags_UpdatesValue()
    {
        ByteFlags value = ByteFlags.None;
        value = value.AddFlag(ByteFlags.A);
        value = value.AddFlag(ByteFlags.C);

        Assert.Equal(ByteFlags.A | ByteFlags.C, value);

        value = value.RemoveFlag(ByteFlags.A);
        Assert.Equal(ByteFlags.C, value);
    }

    [Fact]
    public void AddFlagAndRemoveFlag_WithUShortFlags_UpdatesValue()
    {
        UShortFlags value = UShortFlags.None;
        value = value.AddFlag(UShortFlags.B);
        value = value.AddFlag(UShortFlags.C);

        Assert.Equal(UShortFlags.B | UShortFlags.C, value);

        value = value.RemoveFlag(UShortFlags.B);
        Assert.Equal(UShortFlags.C, value);
    }

    [Fact]
    public void AddFlagAndRemoveFlag_WithUIntFlags_UpdatesValue()
    {
        UIntFlags value = UIntFlags.None;
        value = value.AddFlag(UIntFlags.A);
        value = value.AddFlag(UIntFlags.B);

        Assert.Equal(UIntFlags.A | UIntFlags.B, value);

        value = value.RemoveFlag(UIntFlags.A);
        Assert.Equal(UIntFlags.B, value);
    }

    [Fact]
    public void AddFlagAndRemoveFlag_WithULongFlags_UpdatesValue()
    {
        ULongFlags value = ULongFlags.None;
        value = value.AddFlag(ULongFlags.A);
        value = value.AddFlag(ULongFlags.C);

        Assert.Equal(ULongFlags.A | ULongFlags.C, value);

        value = value.RemoveFlag(ULongFlags.A);
        Assert.Equal(ULongFlags.C, value);
    }

    // EnumExtensions validates the [Flags] contract in Debug builds only.
#if DEBUG
    [Fact]
    public void AddFlagAndRemoveFlag_WhenTypeIsNotFlags_ThrowsArgumentException()
    {
        NotFlags value = NotFlags.A;

        Assert.Throws<ArgumentException>(() => value.AddFlag(NotFlags.B));
        Assert.Throws<ArgumentException>(() => value.RemoveFlag(NotFlags.B));
    }
#endif

    [Flags]
    private enum ByteFlags : byte
    {
        None = 0,
        A = 1,
        B = 2,
        C = 4
    }

    [Flags]
    private enum UShortFlags : ushort
    {
        None = 0,
        A = 1,
        B = 2,
        C = 4
    }

    [Flags]
    private enum UIntFlags : uint
    {
        None = 0,
        A = 1,
        B = 2
    }

    [Flags]
    private enum ULongFlags : ulong
    {
        None = 0,
        A = 1,
        B = 2,
        C = 4
    }

    private enum NotFlags : byte
    {
        A = 1,
        B = 2
    }
}
