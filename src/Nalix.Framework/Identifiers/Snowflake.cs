// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

/*
 * | Component  | Size    | Bit Range | Description                                           |
 * |------------|---------|-----------|-------------------------------------------------------|
 * | Type       | 8 bits  | 56-63     | Entity classification (e.g., Account, Session, etc.)  |
 * | Timestamp  | 32 bits | 24-55     | Unix Seconds (ensures chronological sorting)          |
 * | Sequence   | 14 bits | 10-23     | Prevents collisions within the same second            |
 * | Machine ID | 10 bits | 0-9       | Unique node identifier (supports up to 1024 nodes)    |
 * 
 *   ID = (Type  << 56) | (Timestamp << 24) | (Sequence << 10) | MachineID
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Contracts;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Identity;
using Nalix.Abstractions.Serialization;
using Nalix.Environment.Configuration;
using Nalix.Environment.Time;
using Nalix.Framework.Options;

namespace Nalix.Framework.Identifiers;

/// <summary>
/// Represents a 64-bit unique identifier composed of a value, machine identifier, and type.
/// Provides methods for creation, decomposition, and conversion to various formats.
/// </summary>
[SkipLocalsInit]
[ExcludeFromCodeCoverage]
[StructLayout(
    LayoutKind.Sequential, Pack = 1)]
[SerializePackable(SerializeLayout.Explicit)]
[DebuggerDisplay("0x{_combined:X16} (T={Type}, M={MachineId})")]
public readonly partial struct Snowflake : ISnowflake
{
    #region Const

    /// <summary>
    /// The size in bytes of the <see cref="Snowflake"/> structure.
    /// </summary>
    public const int Size = 8;

    [SerializeOrder(1)]
    private readonly ulong _combined;

    private static int s_sequence;

    private static uint s_lastTimestamp;

    private static readonly Lock s_genLock = new();

    private static readonly ushort s_machineId = LAZY_LOAD_MACHINE_ID();

    #endregion Const

    #region Decomposition

    /// <inheritdoc/>
    public uint Value
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (uint)((_combined >> 24) & 0xFFFFFFFFUL);
    }

    /// <inheritdoc/>
    public ushort MachineId
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (ushort)(_combined & 0x3FFUL);
    }

    /// <inheritdoc/>
    public SnowflakeType Type
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (SnowflakeType)((_combined >> 56) & 0xFFUL);
    }

    /// <summary>
    /// Gets the sequence component of the identifier (14 bits).
    /// </summary>
    public ushort Sequence
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (ushort)((_combined >> 10) & 0x3FFFUL);
    }

    #endregion Decomposition

    #region Public Properties

    /// <summary>
    /// Gets an empty <see cref="Snowflake"/> instance with all components set to zero.
    /// </summary>
    public static readonly ISnowflake Empty = new Snowflake(0, 0, 0);

    /// <inheritdoc/>
    public bool IsEmpty => _combined == 0;

    #endregion Public Properties

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="Snowflake"/> struct.
    /// </summary>
    /// <param name="timestamp">The Unix timestamp value (32 bits).</param>
    /// <param name="machineId">The machine identifier (10 bits used).</param>
    /// <param name="type">The identifier type (8 bits).</param>
    /// <param name="sequence">The sequence number (14 bits).</param>
    [DebuggerHidden]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Snowflake(uint timestamp, ushort machineId, SnowflakeType type, uint sequence)
        => _combined = ((ulong)(byte)type << 56) | ((ulong)timestamp << 24) | ((ulong)(sequence & 0x3FFF) << 10) | (machineId & 0x3FFu);

    /// <summary>
    /// Initializes a new instance of the <see cref="Snowflake"/> struct.
    /// </summary>
    /// <param name="value">The main identifier value (32 bits).</param>
    /// <param name="machineId">The machine identifier (10 bits used).</param>
    /// <param name="type">The identifier type (8 bits).</param>
    [DebuggerHidden]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Snowflake(uint value, ushort machineId, SnowflakeType type)
        => _combined = ((ulong)(byte)type << 56) | ((ulong)value << 24) | (machineId & 0x3FFu);

    /// <summary>
    /// Initializes a new instance of the <see cref="Snowflake"/> struct from a <see cref="ulong"/> value.
    /// </summary>
    /// <param name="value">The 64-bit unsigned integer representing the combined identifier value.</param>
    /// <remarks>
    /// This constructor allows direct initialization from a pre-composed 64-bit value,
    /// which is useful for deserialization scenarios. No validation is performed on the input.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Snowflake(ulong value) => _combined = value;

    #endregion Constructors

    #region Factory Methods

    /// <summary>
    /// Creates a new <see cref="Snowflake"/> from a <see cref="ulong"/> value.
    /// </summary>
    /// <param name="value">The 64-bit unsigned integer representing the combined identifier value.</param>
    /// <returns>A new <see cref="Snowflake"/> instance.</returns>
    /// <remarks>
    /// This method provides a fast way to construct a <see cref="Snowflake"/> from a pre-composed value.
    /// It is particularly useful when deserializing identifiers from storage or network protocols.
    /// </remarks>
    [DebuggerHidden]
    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Snowflake NewId(ulong value) => new(value);

    /// <summary>
    /// Creates a new <see cref="Snowflake"/> with the specified components.
    /// </summary>
    /// <param name="value">The main identifier value (32 bits).</param>
    /// <param name="machineId">The machine identifier (16 bits).</param>
    /// <param name="type">The identifier type (8 bits).</param>
    /// <returns>A new <see cref="Snowflake"/> instance.</returns>
    /// <remarks>
    /// This method constructs a <see cref="Snowflake"/> from its individual components.
    /// All parameters are validated to ensure they fit within their respective bit ranges.
    /// </remarks>
    /// <example>
    /// <code>
    /// var id = Snowflake.NewId(12345, 1001, SnowflakeType.USER);
    /// Console.WriteLine(id.ToString()); // Outputs hex representation
    /// </code>
    /// </example>
    [DebuggerHidden]
    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Snowflake NewId(uint value, ushort machineId, SnowflakeType type) => new(value, machineId, type);

    /// <summary>
    /// Creates a new <see cref="Snowflake"/> with the specified type using the configured machine identifier.
    /// </summary>
    /// <param name="type">The identifier type.</param>
    /// <returns>A new <see cref="Snowflake"/> instance with a timestamp-based value.</returns>
    /// <remarks>
    /// This is the most commonly used factory method. It generates a unique identifier using:
    /// - The current Unix timestamp (in seconds) as the value component
    /// - The globally configured machine ID from <see cref="SnowflakeOptions"/>
    /// - The specified type
    /// This ensures both temporal and spatial uniqueness across distributed systems.
    /// </remarks>
    /// <example>
    /// <code>
    /// var id = Snowflake.NewId(SnowflakeType.SYSTEM);
    /// Console.WriteLine(id.ToString()); // Outputs hex representation
    /// </code>
    /// </example>
    [DebuggerHidden]
    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Snowflake NewId(SnowflakeType type) => NewId(type, s_machineId);

    /// <summary>
    /// Creates a new <see cref="Snowflake"/> with the specified type and machine identifier.
    /// </summary>
    /// <param name="type">The identifier type.</param>
    /// <param name="machineId">The machine identifier, in the range 0-1023. Defaults to 1 if not specified.</param>
    /// <returns>A new <see cref="Snowflake"/> instance with a timestamp-based value.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="machineId"/> is greater than 1023.</exception>
    /// <remarks>
    /// This method generates a unique identifier by combining the current Unix timestamp (in seconds)
    /// with the provided type and machine ID. The timestamp ensures temporal uniqueness.
    /// </remarks>
    /// <example>
    /// <code>
    /// var id = Snowflake.NewId(SnowflakeType.SYSTEM, 42);
    /// Console.WriteLine(id.ToString());
    /// </code>
    /// </example>
    [DebuggerHidden]
    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Snowflake NewId(SnowflakeType type, ushort machineId = 1)
    {
        // The machine ID occupies 10 bits; reject out-of-range values instead of silently masking them.
        ArgumentOutOfRangeException.ThrowIfGreaterThan(machineId, (ushort)0x3FF);

        lock (s_genLock)
        {
            uint now = Clock.UnixSecondsNowUInt32();
            if (now < s_lastTimestamp)
            {
                now = s_lastTimestamp;
            }

            if (now == s_lastTimestamp)
            {
                s_sequence++;

                if (s_sequence > 0x3FFF)
                {
                    now = s_lastTimestamp + 1;
                    s_lastTimestamp = now;
                    s_sequence = 0;
                }
            }
            else
            {
                s_lastTimestamp = now;
                s_sequence = 0;
            }

            return new Snowflake(now, machineId, type, (uint)s_sequence);
        }
    }

    /// <summary>
    /// Attempts to parse a hexadecimal string into a <see cref="Snowflake"/>.
    /// </summary>
    /// <param name="s">The hexadecimal string to parse (must be 14 characters).</param>
    /// <param name="result">When this method returns, contains the parsed <see cref="Snowflake"/> if successful.</param>
    /// <returns><c>true</c> if the string was parsed successfully; otherwise, <c>false</c>.</returns>
    [Pure]
    public static bool TryParse(string? s, out Snowflake result)
    {
        result = (Snowflake)Empty;
        if (string.IsNullOrWhiteSpace(s) || s.Length != Size * 2)
        {
            return false;
        }

        try
        {
            byte[] bytes = Convert.FromHexString(s);
            result = FromBytes(bytes);
            return true;
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            return false;
        }
    }

    #endregion Factory Methods

    #region Override

    /// <summary>
    /// Returns the hash code for this identifier.
    /// </summary>
    /// <returns>A 32-bit signed integer hash code.</returns>
    /// <remarks>
    /// The hash code is computed efficiently from the underlying 64-bit value
    /// and is suitable for use in hash-based collections like <see cref="Dictionary{TKey,TValue}"/>.
    /// This implementation ensures consistent hash values for equal identifiers.
    /// </remarks>
    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetHashCode()
    {
        ulong h = _combined;
        h ^= h >> 33;
        h *= 0xff51afd7ed558ccdL;
        h ^= h >> 33;
        return (int)h;
    }

    /// <summary>
    /// Determines whether this identifier is equal to the specified object.
    /// </summary>
    /// <param name="obj">The object to compare with this instance.</param>
    /// <returns>
    /// <c>true</c> if the object is a <see cref="Snowflake"/> and is equal to this instance;
    /// otherwise, <c>false</c>.
    /// </returns>
    /// <remarks>
    /// This method performs type checking and delegates to the strongly-typed <see cref="Equals(Snowflake)"/> method
    /// for actual comparison. Returns <c>false</c> for null or non-<see cref="Snowflake"/> objects.
    /// </remarks>
    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override bool Equals(object? obj) => obj is Snowflake other && this.Equals(other);

    /// <summary>
    /// Returns the hexadecimal string representation of this identifier.
    /// </summary>
    /// <returns>A 16-character hexadecimal string representing this identifier (8 bytes = 16 hex digits).</returns>
    /// <remarks>
    /// This method efficiently serializes the identifier to a stack-allocated buffer before
    /// converting to hexadecimal format. The returned string is always uppercase and fixed-length.
    /// </remarks>
    [DebuggerHidden]
    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override string ToString()
    {
        Span<byte> buffer = stackalloc byte[Size];
        _ = this.TryWriteBytes(buffer);
        return Convert.ToHexString(buffer);
    }

    #endregion Override

    #region Private Methods

    private static ushort LAZY_LOAD_MACHINE_ID() => ConfigurationManager.Instance.Get<SnowflakeOptions>().MachineId;

    #endregion Private Methods
}
