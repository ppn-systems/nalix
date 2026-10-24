// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Serialization;

namespace Nalix.Codec.Tests.TestSupport;

[GenerateFormatter]
internal partial struct SmallStruct
{
    internal byte A;
}

internal struct ComplexStruct
{
    internal int I32;
    internal short I16;
    internal byte B;
}

internal sealed class NullClass
{
    internal int[] I32 = [];
    internal short? I16;
}

[GenerateFormatter]
internal sealed partial class TestObject
{
    internal int Id { get; set; }
    internal string Name { get; set; } = string.Empty;
    internal List<string> Tags { get; set; } = [];

    internal static TestObject Create() => new();
}

internal readonly struct TestStruct(int x, float y)
{
    internal readonly int X = x;
    internal readonly float Y = y;
}















