// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using Nalix.Abstractions;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Serialization;
using Nalix.Codec.Serialization;

namespace Nalix.Codec.DataFrames;

/// <summary>
/// Base class for all packets with automatic serialization and pooling.
/// Eliminates boilerplate code for Length, Serialize, Deserialize, and ResetForPool.
/// <para>
/// <b>OpCode</b> is extracted automatically from <typeparamref name="TSelf"/>'s
/// <c>[PacketOpcode]</c> attribute.
/// </para>
/// </summary>
/// <typeparam name="TSelf">The concrete packet type.</typeparam>
[Packet]
public abstract class PacketBase<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TSelf> :
    FrameBase,
    IPoolable,
    IDisposable,
    IPoolRentable,
    IPacketDeserializer<TSelf> where TSelf : PacketBase<TSelf>, IPacketStaticOpcode, new()
{
    #region Fields

    /// <summary>
    /// Indicates whether this packet was rented from a pool and should be returned on disposal.
    /// 1 = Rented, 0 = Available/Returned.
    /// </summary>
    [SerializeIgnore]
    private int _isRented;

    #endregion Fields

    #region Constructor

    /// <summary>
    /// Assigns the automatically derived <see cref="FrameBase.Header"/>.OpCode
    /// so that every packet is self-identifying on the wire.
    /// </summary>
    protected PacketBase()
    {
        PacketHeader header = this.Header;
        header.OpCode = TSelf.StaticOpCode;
        this.Header = header;
    }

    #endregion Constructor

    #region APIs

    /// <inheritdoc/>
    /// <exception cref="SerializationFailureException">Thrown when the packet cannot be serialized by the configured formatter.</exception>
    /// <exception cref="InvalidOperationException">Thrown when no formatter is available for the packet type.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override byte[] Serialize() => LiteSerializer.Serialize((TSelf)this);

    /// <inheritdoc/>
    /// <exception cref="ArgumentException">Thrown when <paramref name="buffer"/> is too small, which can indicate an insufficiently sized buffer or a concurrent mutation (Use-After-Free).</exception>
    /// <exception cref="SerializationFailureException">Thrown when the packet cannot be serialized by the configured formatter.</exception>
    /// <exception cref="InvalidOperationException">Thrown when no formatter is available for the packet type.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int Serialize(Span<byte> buffer)
    {
        int required = this.Length;
        if (buffer.Length < required)
        {
            THROW_BUFFER_TOO_SMALL(buffer.Length, required);
        }

        return this.SERIALIZE_INTERNAL(buffer, required);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private int SERIALIZE_INTERNAL(Span<byte> buffer, int required)
    {
        try
        {
            return LiteSerializer.Serialize((TSelf)this, buffer);
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex) && (
            ex is InvalidOperationException ||
            ex is InternalErrorException ||
            ex is ArgumentOutOfRangeException))
        {
            if (buffer.Length < required)
            {
                THROW_BUFFER_TOO_SMALL_INNER(buffer.Length, required, ex);
            }

            throw;
        }
    }

    /// <summary>
    /// Creates or rents an instance of <typeparamref name="TSelf"/>.
    /// </summary>
    /// <returns>A new or rented <typeparamref name="TSelf"/> instance.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    [SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "Factory pattern")]
    public static TSelf Create() => PacketRegistry.Manager == null ? new TSelf() : PacketRegistry.Manager.Get<TSelf>();

    /// <summary>
    /// Deserializes a <typeparamref name="TSelf"/> packet from <paramref name="buffer"/>
    /// using object pooling to avoid heap allocation.
    /// </summary>
    /// <param name="buffer">The raw wire bytes to deserialize from.</param>
    /// <returns>A <typeparamref name="TSelf"/> instance populated from the buffer.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="buffer"/> is empty.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when deserialization reads zero bytes or no formatter is available for the packet type.
    /// </exception>
    /// <exception cref="SerializationFailureException">
    /// Thrown when the payload is malformed or does not contain enough data to deserialize the packet.
    /// </exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    [SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "<Pending>")]
    public static TSelf Deserialize(ReadOnlySpan<byte> buffer)
    {
        if (!TRY_VALIDATE_BUFFER_HEADER(buffer))
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Packet.Malformed))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Packet.Malformed, typeof(TSelf).FullName);
            }
            return null!;
        }

        TSelf packet = Create();
        int bytesRead;
        try
        {
            if (!LiteSerializer.TryDeserialize(buffer, ref packet, out bytesRead))
            {
                packet.Dispose();

                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Packet.Malformed))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.Packet.Malformed, typeof(TSelf).FullName);
                }

                return null!;
            }
        }
        catch
        {
            packet.Dispose();
            throw;
        }

        if (bytesRead < buffer.Length)
        {
            packet.Dispose();

            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Packet.Malformed))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Packet.Malformed, typeof(TSelf).FullName);
            }

            return null!;
        }

        return packet;
    }

    /// <inheritdoc/>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public override void ResetForPool()
    {
        // Reset all FrameBase header fields to well-known defaults.
        this.SequenceId = 0;
        this.Flags = PacketFlags.NONE;
        this.Priority = PacketPriority.NONE;

        // Restore type identity.
        PacketHeader header = this.Header;
        header.OpCode = TSelf.StaticOpCode;
        this.Header = header;
    }

    /// <inheritdoc/>
    [EditorBrowsable(EditorBrowsableState.Never)]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void OnRent() => Volatile.Write(ref _isRented, 1);

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    [SuppressMessage("Design", "CA1063:Implement IDisposable Correctly", Justification = "<Pending>")]
    [SuppressMessage("Usage", "CA1816:Dispose methods should call SuppressFinalize", Justification = "<Pending>")]
    public void Dispose() => this.Dispose(true);

    /// <summary>
    /// Returns rented packets to their pool once; non-rented packet instances are left alone.
    /// </summary>
    /// <param name="disposing"><see langword="true"/> when called from <see cref="Dispose()"/>.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected virtual void Dispose(bool disposing)
    {
        if (!disposing)
        {
            return;
        }

        // Atomically check and clear the rented flag to ensure we only return to pool once.
        if (Interlocked.Exchange(ref _isRented, 0) == 1)
        {
            // Reset state before returning to pool
            // this.ResetForPool(); - Pool managers typically call ResetForPool after renting, but if needed, it can be called here before returning.

            // Use the concrete type TSelf to call the fast generic Return path.
            PacketRegistry.Manager?.Return((TSelf)this);
        }
    }

    #endregion APIs

    #region Diagnostics

    /// <inheritdoc/>
    public override string ToString() =>
        $"{typeof(TSelf).Name}(OpCode={this.Header.OpCode}, Flags={this.Header.Flags}, Priority={this.Header.Priority}, SequenceId={this.Header.SequenceId})";

    #endregion Diagnostics

    #region Private Methods

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void THROW_BUFFER_TOO_SMALL(int length, int required)
        => throw new ArgumentException(
            $"Buffer too small: length={length}, required>={required}, type={typeof(TSelf).FullName}. " +
            $"If you did not manually pass a small buffer, this indicates a Use-After-Free concurrency failure: the packet size was mutated by another thread. " +
            $"This is typically caused by an orphaned I/O task missing a CancellationToken.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void THROW_BUFFER_TOO_SMALL_INNER(int length, int required, Exception ex)
        => throw new ArgumentException(
            $"Buffer too small: length={length}, required>={required}, type={typeof(TSelf).FullName}. " +
            $"If you did not manually pass a small buffer, this indicates a Use-After-Free concurrency failure: the packet size was mutated by another thread. " +
            $"This is typically caused by an orphaned I/O task missing a CancellationToken.", ex);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TRY_VALIDATE_BUFFER_HEADER(ReadOnlySpan<byte> buffer)
    {
        if (buffer.IsEmpty)
        {
            return false;
        }

        if (buffer.Length < PacketSchema<TSelf>.StaticSize)
        {
            return false;
        }

        return true;
    }

    #endregion Private Methods
}
