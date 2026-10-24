// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Exceptions;
using Nalix.Codec.Serialization;
using Nalix.Observability.Contracts;

namespace Nalix.Codec.Tests.Serialization;

/// <summary>
/// Regression tests for the circular static initialization bug between
/// <see cref="FormatterProvider"/> and <c>ReferenceArrayFormatter&lt;object&gt;</c>.
/// <para>
/// Root cause: <c>FormatterProvider</c>'s static constructor eagerly created
/// <c>new ReferenceArrayFormatter&lt;object&gt;()</c>, whose static field initializer
/// called <c>FormatterProvider.Get&lt;object&gt;()</c> while <c>FormatterProvider</c>
/// was still initializing. On Blazor WASM (and potentially other AOT runtimes)
/// this caused <c>TypeInitializationException</c> / <c>SerializationFailureException</c>.
/// </para>
/// </summary>
public sealed class FormatterProviderCircularInitTests
{
    // ────────────────────────────────────────────────────────────────────
    //  Requirement: Loading Nalix.Observability.Contracts must not throw
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Accessing <see cref="RuntimeObservation.StaticOpCode"/> must not trigger
    /// a <see cref="TypeInitializationException"/> from circular codec init.
    /// </summary>
    [Fact]
    public void RuntimeObservation_StaticOpCode_DoesNotThrow()
    {
        ushort opCode = RuntimeObservation.StaticOpCode;

        Assert.NotEqual(0, opCode);
    }

    /// <summary>
    /// Creating a <see cref="RuntimeObservation"/> instance must not trigger
    /// circular formatter initialization.
    /// </summary>
    [Fact]
    public void RuntimeObservation_CreateInstance_DoesNotThrow()
    {
        RuntimeObservation packet = new();

        Assert.NotNull(packet);
    }

    // ────────────────────────────────────────────────────────────────────
    //  Requirement: FormatterProvider.Get<object>() behavior is well-defined
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>FormatterProvider.Get&lt;object&gt;()</c> must throw
    /// <see cref="SerializationFailureException"/> because <c>System.Object</c>
    /// is a class with no registered formatter — not a
    /// <see cref="TypeInitializationException"/> from circular init.
    /// </summary>
    [Fact]
    public void Get_WhenTypeIsObject_ThrowsSerializationFailureException()
    {
        // The call must not cause a TypeInitializationException (circular init).
        // It may throw SerializationFailureException because there is no formatter
        // registered for System.Object as a complex class.
        SerializationFailureException ex = Assert.Throws<SerializationFailureException>(
            FormatterProvider.Get<object>);

        Assert.Contains("System.Object", ex.Message);
    }

    // ────────────────────────────────────────────────────────────────────
    //  Requirement: object[] and List<object> do not trigger circular init
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>FormatterProvider.Get&lt;object[]&gt;()</c> must resolve without
    /// triggering circular static initialization. The formatter for
    /// <c>object[]</c> is pre-registered via <c>ReferenceArrayFormatter&lt;object&gt;</c>.
    /// </summary>
    [Fact]
    public void Get_WhenTypeIsObjectArray_ResolvesWithoutCircularInit()
    {
        IFormatter<object[]> formatter = FormatterProvider.Get<object[]>();

        Assert.NotNull(formatter);
    }

    /// <summary>
    /// <c>FormatterProvider.Get&lt;List&lt;object&gt;&gt;()</c> must resolve
    /// without triggering circular static initialization.
    /// </summary>
    [Fact]
    public void Get_WhenTypeIsListOfObject_ResolvesWithoutCircularInit()
    {
        IFormatter<List<object>> formatter = FormatterProvider.Get<List<object>>();

        Assert.NotNull(formatter);
    }

    /// <summary>
    /// <c>FormatterProvider.Get&lt;string[]&gt;()</c> must still resolve
    /// correctly (sanity check).
    /// </summary>
    [Fact]
    public void Get_StringArray_StillResolves()
    {
        IFormatter<string[]> formatter = FormatterProvider.Get<string[]>();

        Assert.NotNull(formatter);
    }

    // ────────────────────────────────────────────────────────────────────
    //  Requirement: string[] and primitive arrays still round-trip
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// String arrays must serialize and deserialize correctly after the fix.
    /// </summary>
    [Fact]
    public void SerializeDeserialize_WhenStringArray_RoundTripsValues()
    {
        string[] input = ["hello", "world", null!, ""];

        byte[] bytes = LiteSerializer.Serialize(input);
        string[]? output = LiteSerializer.Deserialize<string[]>(bytes, out _);

        Assert.NotNull(output);
        Assert.Equal(input.Length, output!.Length);
        Assert.Equal("hello", output[0]);
        Assert.Equal("world", output[1]);
        Assert.Null(output[2]);
        Assert.Equal("", output[3]);
    }

    /// <summary>
    /// Primitive arrays must serialize and deserialize correctly after the fix.
    /// </summary>
    [Fact]
    public void SerializeDeserialize_WhenIntArray_RoundTripsValues()
    {
        int[] input = [1, 2, 3, 42];

        byte[] bytes = LiteSerializer.Serialize(input);
        int[]? output = LiteSerializer.Deserialize<int[]>(bytes, out _);

        Assert.NotNull(output);
        Assert.Equal(input, output);
    }

    /// <summary>
    /// <c>List&lt;string&gt;</c> must serialize and deserialize correctly.
    /// </summary>
    [Fact]
    public void SerializeDeserialize_WhenListOfString_RoundTripsValues()
    {
        List<string> input = ["alpha", "beta", "gamma"];

        byte[] bytes = LiteSerializer.Serialize(input);
        List<string>? output = LiteSerializer.Deserialize<List<string>>(bytes, out _);

        Assert.NotNull(output);
        Assert.Equal(input.Count, output!.Count);
        Assert.Equal(input, output);
    }

    // ────────────────────────────────────────────────────────────────────
    //  Requirement: FormatterProvider.Get<object[]>() is cached
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Get_ObjectArray_ReturnsCachedInstance()
    {
        IFormatter<object[]> first = FormatterProvider.Get<object[]>();
        IFormatter<object[]> second = FormatterProvider.Get<object[]>();

        Assert.Same(first, second);
    }

    [Fact]
    public void Get_ListOfObject_ReturnsCachedInstance()
    {
        IFormatter<List<object>> first = FormatterProvider.Get<List<object>>();
        IFormatter<List<object>> second = FormatterProvider.Get<List<object>>();

        Assert.Same(first, second);
    }
}

