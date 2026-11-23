// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using Nalix.Abstractions.Diagnostics;
using Nalix.Abstractions.Exceptions;
using Nalix.Codec.Serialization.Formatters.Cache;
using Nalix.Codec.Serialization.Formatters.Collections;
using Nalix.Codec.Serialization.Formatters.Primitives;

namespace Nalix.Codec.Serialization;

/// <summary>
/// Provides a global registry for registering and retrieving formatters without boxing.
/// The provider centralizes formatter lookup so serializer code can ask for a
/// formatter once and then reuse the resolved instance on the hot path.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class FormatterProvider
{
    #region Fields

    private static readonly DiagnosticListener s_listener = DiagnosticsEvents.Source;

    private static int s_cntTotal, s_cntPrimitives, s_cntNullables, s_cntArrays,
                       s_cntNullableArrays, s_cntLists, s_cntEnums, s_cntStrings;

    private static readonly Stopwatch s_sw = Stopwatch.StartNew();

    #endregion Fields

    #region Static Constructor

    static FormatterProvider()
    {
        // String formatters are registered first because they are among the most
        // common and often participate in higher-level composite serializers.
        Register(new StringFormatter());
        Register(new BufferLeaseFormatter());
        Register(new StringArrayFormatter());
        Register(new ReferenceListFormatter<string>());
        Register(new ReferenceArrayFormatter<string>());
        Register(new QueueFormatter<string>());
        Register(new StackFormatter<string>());
        Register(new HashSetFormatter<string>());
        Register(new ReferenceArrayFormatter<object>());
        Register(new ReferenceListFormatter<object>());

        // Pre-register all formatter variants for every built-in unmanaged type.
        // The AOT compiler emits closed generics for each new XxxFormatter<T>() call
        // because the type argument is a compile-time constant at each call site.
        RegisterAllUnmanaged<char>();
        RegisterAllUnmanaged<byte>();
        RegisterAllUnmanaged<sbyte>();
        RegisterAllUnmanaged<short>();
        RegisterAllUnmanaged<int>();
        RegisterAllUnmanaged<long>();
        RegisterAllUnmanaged<ushort>();
        RegisterAllUnmanaged<uint>();
        RegisterAllUnmanaged<ulong>();
        RegisterAllUnmanaged<float>();
        RegisterAllUnmanaged<double>();
        RegisterAllUnmanaged<bool>();
        RegisterAllUnmanaged<decimal>();
        RegisterAllUnmanaged<Guid>();
        RegisterAllUnmanaged<DateOnly>();
        RegisterAllUnmanaged<DateTime>();
        RegisterAllUnmanaged<TimeSpan>();
        RegisterAllUnmanaged<TimeOnly>();
        RegisterAllUnmanaged<DateTimeOffset>();

        // Pre-register NullableValueListFormatter for all built-in unmanaged types.
        RegisterNullableValueLists();

        // Pre-register common Dictionary<K,V> formatters.
        Register(new DictionaryFormatter<string, string>());
        Register(new DictionaryFormatter<string, int>());
        Register(new DictionaryFormatter<string, long>());
        Register(new DictionaryFormatter<string, byte>());
        Register(new DictionaryFormatter<int, int>());
        Register(new DictionaryFormatter<int, long>());
        Register(new DictionaryFormatter<int, string>());

        // Pre-register common ValueTuple formatters.
        Register(new ValueTupleFormatter<int, int>());
        Register(new ValueTupleFormatter<int, string>());
        Register(new ValueTupleFormatter<int, bool>());
        Register(new ValueTupleFormatter<string, int>());
        Register(new ValueTupleFormatter<string, string>());
        Register(new ValueTupleFormatter<string, bool>());
        Register(new ValueTupleFormatter<bool, int>());
        Register(new ValueTupleFormatter<bool, string>());
        Register(new ValueTupleFormatter<int, string, bool>());
        Register(new ValueTupleFormatter<int, string, float>());
        Register(new ValueTupleFormatter<int, int, int>());
        Register(new ValueTupleFormatter<string, string, string>());
        Register(new ValueTupleFormatter<int, string, bool, double>());
        Register(new ValueTupleFormatter<int, string, float, long>());
        Register(new ValueTupleFormatter<int, int, int, int>());
        Register(new ValueTupleFormatter<int, string, bool, double, long>());
        Register(new ValueTupleFormatter<int, string, float, long, bool>());
        Register(new ValueTupleFormatter<int, int, int, int, int>());

        if (s_listener?.IsEnabled(DiagnosticsEvents.Serialization.Initialization) == true)
        {
            string elapsed = s_sw.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture);
            DiagnosticsEvents.Write(
                DiagnosticsEvents.Serialization.Initialization,
                new DiagnosticLog(
                    "CD.FormatterProvider:Internal",
                    $"initialized elapsed-ms={elapsed} total={s_cntTotal} primitives={s_cntPrimitives} nullables={s_cntNullables} arrays={s_cntArrays} nullable-arrays={s_cntNullableArrays} lists={s_cntLists} enums={s_cntEnums} strings={s_cntStrings}"));
        }
    }

    /// <summary>
    /// Pre-registers all formatter variants for a single unmanaged element type.
    /// The AOT compiler emits the closed generic for each <c>new XxxFormatter&lt;T&gt;()</c>
    /// call because the type argument is a compile-time constant at each call site.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RegisterAllUnmanaged<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties |
            DynamicallyAccessedMemberTypes.NonPublicProperties)] T>() where T : unmanaged
    {
        Register(new UnmanagedFormatter<T>());
        Register(new ArrayFormatter<T>());
        Register(new NullableFormatter<T>());
        Register(new NullableArrayFormatter<T>());
        Register(new ListFormatter<T>());
        Register(new QueueFormatter<T>());
        Register(new StackFormatter<T>());
        Register(new HashSetFormatter<T>());
        Register(new MemoryFormatter<T>());
        Register(new ReadOnlyMemoryFormatter<T>());

        // Dictionary formatters with common key types
        Register(new DictionaryFormatter<string, T>());
        Register(new DictionaryFormatter<int, T>());
        Register(new DictionaryFormatter<long, T>());
    }

    /// <summary>
    /// Pre-registers <see cref="NullableValueListFormatter{T}"/> for all built-in unmanaged types.
    /// This cannot use a generic helper because the formatter's type parameter is the
    /// underlying (non-nullable) type, not the nullable wrapper.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RegisterNullableValueLists()
    {
        Register(new NullableValueListFormatter<char>());
        Register(new NullableValueListFormatter<byte>());
        Register(new NullableValueListFormatter<sbyte>());
        Register(new NullableValueListFormatter<short>());
        Register(new NullableValueListFormatter<int>());
        Register(new NullableValueListFormatter<long>());
        Register(new NullableValueListFormatter<ushort>());
        Register(new NullableValueListFormatter<uint>());
        Register(new NullableValueListFormatter<ulong>());
        Register(new NullableValueListFormatter<float>());
        Register(new NullableValueListFormatter<double>());
        Register(new NullableValueListFormatter<decimal>());
        Register(new NullableValueListFormatter<bool>());
        Register(new NullableValueListFormatter<Guid>());
        Register(new NullableValueListFormatter<DateOnly>());
        Register(new NullableValueListFormatter<DateTime>());
        Register(new NullableValueListFormatter<TimeSpan>());
        Register(new NullableValueListFormatter<TimeOnly>());
        Register(new NullableValueListFormatter<DateTimeOffset>());
    }

    #endregion Static Constructor

    #region Generated Formatter Registry

    /// <summary>
    /// Stores source-generated formatters registered via <c>[ModuleInitializer]</c>
    /// bootstrapper code emitted by <c>SerializeFormatterGenerator</c>.
    /// </summary>
    private static readonly ConcurrentDictionary<Type, object> s_generatedFormatters = new();

    /// <summary>
    /// Registers a source-generated formatter for runtime lookup.
    /// Called by source-generated <c>[ModuleInitializer]</c> code — do not call manually.
    /// </summary>
    /// <typeparam name="T">The serialization target type.</typeparam>
    /// <param name="formatter">The source-generated formatter instance.</param>
    public static void RegisterGenerated<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties |
            DynamicallyAccessedMemberTypes.NonPublicProperties)] T>(IFormatter<T> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        s_generatedFormatters[typeof(T)] = formatter;
        Register(formatter);
    }

    /// <summary>
    /// Attempts to retrieve a source-generated formatter for <typeparamref name="T"/>.
    /// Returns <see langword="null"/> if no generated formatter exists for the type.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static IFormatter<T>? TryGetGenerated<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties |
            DynamicallyAccessedMemberTypes.NonPublicProperties)] T>()
    {
        if (s_generatedFormatters.TryGetValue(typeof(T), out object? obj))
        {
            return (IFormatter<T>)obj;
        }
        return null;
    }

    #endregion Generated Formatter Registry

    #region Public Registration Helpers

    /// <summary>
    /// Pre-registers all formatter variants for an unmanaged element type <typeparamref name="T"/>.
    /// Call this for custom enum or struct types to enable automatic collection, nullable,
    /// and array formatting without runtime reflection.
    /// </summary>
    /// <typeparam name="T">An unmanaged type (primitive, enum, or blittable struct).</typeparam>
    public static void RegisterAllFormatters<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties |
            DynamicallyAccessedMemberTypes.NonPublicProperties)] T>() where T : unmanaged
        => RegisterAllUnmanaged<T>();

    /// <summary>
    /// Pre-registers <see cref="List{T}"/> and <c>T[]</c> formatters for a reference type.
    /// Called by source-generated bootstrapper code for <c>[GenerateFormatter]</c> class types.
    /// </summary>
    /// <typeparam name="T">A reference type (class) with a registered formatter.</typeparam>
    public static void RegisterCollectionFormatters<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties |
            DynamicallyAccessedMemberTypes.NonPublicProperties)] T>() where T : class
    {
        Register(new ReferenceListFormatter<T>());
        Register(new ReferenceArrayFormatter<T>());
    }

    /// <summary>
    /// Pre-registers a <see cref="Dictionary{TKey, TValue}"/> formatter.
    /// Use this for dictionary types with non-standard key/value combinations.
    /// </summary>
    public static void RegisterDictionary<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties |
            DynamicallyAccessedMemberTypes.NonPublicProperties)] TKey,
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties |
            DynamicallyAccessedMemberTypes.NonPublicProperties)] TValue>() where TKey : notnull => Register(new DictionaryFormatter<TKey, TValue>());

    // TODO: Audit whether RegisterTuple is a workaround or an architectural requirement.
    //       RegisterTuple exists because ValueTupleFormatter is internal and generated code
    //       in consumer assemblies cannot instantiate it directly. If ValueTupleFormatter
    //       were made public (or if the generator emitted a different registration strategy),
    //       these overloads could be removed. Evaluate whether:
    //         (a) making ValueTupleFormatter public + having the generator call Register directly
    //             is cleaner, or
    //         (b) RegisterTuple should stay as a stable public API surface for hand-written
    //             tuple registration by application code (not just generated bootstrappers).

    /// <summary>
    /// Pre-registers a <see cref="ValueTuple{T1, T2}"/> formatter.
    /// Called by source-generated bootstrapper code for tuples containing enum types.
    /// </summary>
    public static void RegisterTuple<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T1,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T2>()
        => Register(new ValueTupleFormatter<T1, T2>());

    /// <summary>
    /// Pre-registers a <see cref="ValueTuple{T1, T2, T3}"/> formatter.
    /// Called by source-generated bootstrapper code for tuples containing enum types.
    /// </summary>
    public static void RegisterTuple<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T1,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T2,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T3>()
        => Register(new ValueTupleFormatter<T1, T2, T3>());

    /// <summary>
    /// Pre-registers a <see cref="ValueTuple{T1, T2, T3, T4}"/> formatter.
    /// Called by source-generated bootstrapper code for tuples containing enum types.
    /// </summary>
    public static void RegisterTuple<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T1,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T2,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T3,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T4>()
        => Register(new ValueTupleFormatter<T1, T2, T3, T4>());

    /// <summary>
    /// Pre-registers a <see cref="ValueTuple{T1, T2, T3, T4, T5}"/> formatter.
    /// Called by source-generated bootstrapper code for tuples containing enum types.
    /// </summary>
    public static void RegisterTuple<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T1,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T2,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T3,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T4,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T5>()
        => Register(new ValueTupleFormatter<T1, T2, T3, T4, T5>());

    #endregion Public Registration Helpers

    #region APIs

    /// <summary>
    /// Registers a formatter for the specified type.
    /// </summary>
    [DebuggerStepThrough]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Register<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties |
            DynamicallyAccessedMemberTypes.NonPublicProperties)] T>(IFormatter<T> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);

        FormatterCache<T>.Instance = formatter;

        Type t = typeof(T);
        Type ut = t;
        bool isArray = ut.IsArray;
        bool isNullable = ut.IsGenericType && ut.GetGenericTypeDefinition() == typeof(Nullable<>);

        _ = Interlocked.Increment(ref s_cntTotal);

        if (t == typeof(string)) { _ = Interlocked.Increment(ref s_cntStrings); return; }
        if (isArray)
        {
            Type elem = ut.GetElementType()!;
            _ = elem.IsGenericType && elem.GetGenericTypeDefinition() == typeof(Nullable<>)
                ? Interlocked.Increment(ref s_cntNullableArrays)
                : Interlocked.Increment(ref s_cntArrays);
            return;
        }
        if (isNullable) { _ = Interlocked.Increment(ref s_cntNullables); return; }
        if (ut.IsEnum) { _ = Interlocked.Increment(ref s_cntEnums); return; }
        if (ut.IsPrimitive ||
            ut == typeof(Guid) ||
            ut == typeof(char) ||
            ut == typeof(decimal) ||
            ut == typeof(DateTime) ||
            ut == typeof(TimeSpan) ||
            ut == typeof(DateTimeOffset)) { _ = Interlocked.Increment(ref s_cntPrimitives); return; }
        if (ut.IsGenericType && ut.GetGenericTypeDefinition() == typeof(List<>))
        { _ = Interlocked.Increment(ref s_cntLists); }
    }

    /// <summary>
    /// Registers a formatter for complex types (struct / class).
    /// Uses <see cref="Interlocked.CompareExchange{T}"/> so the first writer wins.
    /// </summary>
    [DebuggerStepThrough]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RegisterComplex<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties |
            DynamicallyAccessedMemberTypes.NonPublicProperties)] T>(IFormatter<T> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);

        Type type = typeof(T);

        if (type.IsValueType && !type.IsEnum)
        {
            _ = Interlocked.CompareExchange(ref ComplexTypeCache<T>.Struct, formatter, default);
            return;
        }

        if (type.IsClass)
        {
            _ = Interlocked.CompareExchange(ref ComplexTypeCache<T>.Class, formatter, default);
            return;
        }

        throw new SerializationFailureException($"Unsupported type: {type.FullName}");
    }

    /// <summary>
    /// Retrieves the registered formatter for <typeparamref name="T"/>.
    /// All common built-in types (primitives, collections, nullable, tuples) are
    /// pre-registered in the static constructor. Source-generated DTO formatters are
    /// registered via <see cref="RegisterGenerated{T}"/>. Enum types are created
    /// on-demand (AOT-safe: <typeparamref name="T"/> is the enum type itself).
    /// Unregistered types throw <see cref="SerializationFailureException"/>.
    /// </summary>
    [DebuggerStepThrough]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static IFormatter<T> Get<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties |
            DynamicallyAccessedMemberTypes.NonPublicProperties)] T>()
    {
        // ── Fast path: already cached ─────────────────────────────────────
        IFormatter<T>? cached = FormatterCache<T>.Instance;
        if (cached is not null)
        {
            return cached;
        }

        // ── Source-generated formatters (registered via [ModuleInitializer]) ──
        IFormatter<T>? generated = TryGetGenerated<T>();
        if (generated is not null)
        {
            return CacheOrGetExisting(generated);
        }

        // ── Enum types: created on-demand (T is the enum type, AOT-safe) ──
        if (typeof(T).IsEnum)
        {
            EnumFormatter<T> f = new();
            Register(f);
            return f;
        }

        // ── Complex type (class / struct): must be pre-registered ─────────
        return GetComplex<T>();
    }

    /// <summary>
    /// Retrieves the registered formatter for a complex struct or class type.
    /// </summary>
    [DebuggerStepThrough]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static IFormatter<T> GetComplex<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties |
            DynamicallyAccessedMemberTypes.NonPublicProperties)] T>()
    {
        Type type = typeof(T);

        if (Nullable.GetUnderlyingType(type) is not null)
        {
            throw new SerializationFailureException(
                $"Cannot call GetComplex<T>() on Nullable<T>: {type}");
        }

        if (type.IsValueType && !type.IsEnum)
        {
            IFormatter<T> existing = ComplexTypeCache<T>.Struct;
            if (existing is not null)
            {
                return existing;
            }

            throw MissingGeneratedFormatter(type, "struct");
        }

        if (type.IsClass)
        {
            IFormatter<T> existing = ComplexTypeCache<T>.Class;
            if (existing is not null)
            {
                return existing;
            }

            throw MissingGeneratedFormatter(type, "class");
        }

        throw new SerializationFailureException($"No formatter registered for type {type}.");
    }

    #endregion APIs

    #region Private Methods

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static IFormatter<T> CacheOrGetExisting<
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties |
            DynamicallyAccessedMemberTypes.NonPublicProperties)] T>(IFormatter<T> created)
    {
        IFormatter<T>? existing = Interlocked.CompareExchange(
            ref FormatterCache<T>.Instance, created, null);
        return existing ?? created;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static SerializationFailureException MissingGeneratedFormatter(Type type, string kind)
        => new(
            $"No formatter registered for {kind} '{type.FullName}'. " +
            "Mark the type with [GenerateFormatter], call FormatterProvider.RegisterAllFormatters<T>(), " +
            "or register a custom IFormatter<T> manually.");

    #endregion Private Methods
}
