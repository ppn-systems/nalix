// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Buffers;
using System.Text;
using System.Text.Json;
using Nalix.Observability.Internal;

namespace Nalix.Observability.Tests.Internal;

/// <summary>
/// Verifies the array cap, string cap, root truncation marker and lossless re-emission done by <see cref="JsonTrimmer"/>.
/// </summary>
public sealed class JsonTrimmerTests
{
    [Fact]
    public void Trim_WhenArrayExceedsCap_CutsArrayToCap()
    {
        string output = Trim("""{"a":[1,2,3,4,5]}""", 2, out bool trimmed);

        Assert.True(trimmed);
        Assert.Equal("""{"a":[1,2],"_truncated":true}""", output);
    }

    [Fact]
    public void Trim_WhenNestedArrayExceedsCap_CutsNestedArray()
    {
        string output = Trim("""{"outer":[{"inner":[1,2,3]},{"inner":[4]}]}""", 2, out bool trimmed);

        Assert.True(trimmed);
        using JsonDocument document = JsonDocument.Parse(output);
        Assert.Equal(2, document.RootElement.GetProperty("outer")[0].GetProperty("inner").GetArrayLength());
        Assert.Equal(1, document.RootElement.GetProperty("outer")[1].GetProperty("inner").GetArrayLength());
    }

    [Fact]
    public void Trim_WhenNothingDropped_OmitsTruncatedMarkerAndReturnsFalse()
    {
        string output = Trim("""{"a":[1,2],"b":"short"}""", 2, out bool trimmed);

        Assert.False(trimmed);
        Assert.Equal("""{"a":[1,2],"b":"short"}""", output);
    }

    [Fact]
    public void Trim_WhenItemsDropped_AddsTruncatedMarkerOnlyAtRoot()
    {
        string output = Trim("""{"a":{"nested":[1,2,3]},"b":[1,2,3]}""", 1, out bool trimmed);

        Assert.True(trimmed);
        using JsonDocument document = JsonDocument.Parse(output);
        Assert.True(document.RootElement.GetProperty(JsonTrimmer.TruncatedProperty).GetBoolean());
        Assert.False(document.RootElement.GetProperty("a").TryGetProperty(JsonTrimmer.TruncatedProperty, out _));
    }

    [Fact]
    public void Trim_WhenCapIsZero_EmptiesArrays()
    {
        string output = Trim("""{"a":[1,"x",{"y":1}],"b":[]}""", 0, out bool trimmed);

        Assert.True(trimmed);
        Assert.Equal("""{"a":[],"b":[],"_truncated":true}""", output);
    }

    [Fact]
    public void Trim_WhenStringExceeds512Chars_CutsToLimitWithEllipsis()
    {
        string longText = new('q', 600);

        string output = Trim($$"""{"s":"{{longText}}"}""", 10, out bool trimmed);

        Assert.True(trimmed);
        using JsonDocument document = JsonDocument.Parse(output);
        string text = document.RootElement.GetProperty("s").GetString()!;
        Assert.Equal(512 + 1, text.Length);
        Assert.EndsWith("…", text, StringComparison.Ordinal);
        Assert.Equal(new string('q', 512), text[..512]);
    }

    [Fact]
    public void Trim_WhenStringWithinLimit_KeepsStringWhole()
    {
        string text = new('q', 512);

        string output = Trim($$"""{"s":"{{text}}"}""", 10, out bool trimmed);

        Assert.False(trimmed);
        using JsonDocument document = JsonDocument.Parse(output);
        Assert.Equal(text, document.RootElement.GetProperty("s").GetString());
    }

    [Fact]
    public void Trim_WhenPropertyNameAndValueAreEscaped_PreservesThem()
    {
        string output = Trim("""{"a\"b":"c\"d\\e\n"}""", 10, out bool trimmed);

        Assert.False(trimmed);
        using JsonDocument document = JsonDocument.Parse(output);
        Assert.Equal("c\"d\\e\n", document.RootElement.GetProperty("a\"b").GetString());
    }

    [Fact]
    public void Trim_WhenNumbersPresent_KeepsExactNumberText()
    {
        string output = Trim("""{"a":1.50,"b":-0.0e+2,"c":12345678901234567890}""", 10, out _);

        Assert.Equal("""{"a":1.50,"b":-0.0e+2,"c":12345678901234567890}""", output);
    }

    [Theory]
    [InlineData("""{"a":[1,[2,[3,[4]]]],"s":"text","n":null,"t":true,"f":false}""", 1)]
    [InlineData("""{"list":[{"k":"v"},{"k":"w"},{"k":"x"}]}""", 2)]
    [InlineData("""{"deep":{"deeper":{"deepest":[1,2,3,4,5,6]}}}""", 3)]
    public void Trim_WhenInputVaries_ProducesValidJson(string json, int cap)
    {
        string output = Trim(json, cap, out _);

        using JsonDocument document = JsonDocument.Parse(output);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
    }

    private static string Trim(string json, int maxItems, out bool trimmed)
    {
        ArrayBufferWriter<byte> buffer = new();
        trimmed = JsonTrimmer.Trim(Encoding.UTF8.GetBytes(json), maxItems, buffer);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
