// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Codec.Security.Primitives;

namespace Nalix.Codec.Security.Hashing;

/// <summary>
/// High-performance, zero-allocation implementation of the Poly1305 message authentication code
/// (MAC) algorithm as a <see langword="ref struct"/>.
/// </summary>
/// <remarks>
/// <para>
/// Poly1305 is a cryptographically strong MAC algorithm designed by Daniel J. Bernstein.
/// It is used in various cryptographic protocols including the ChaCha20-Poly1305 AEAD
/// cipher suite in TLS 1.3.
/// </para>
/// <para>
/// This implementation follows RFC 8439 and provides constant-time operations for
/// enhanced security. All internal buffers use <c>InlineArray</c> structs, so the
/// entire instance lives on the stack with <b>zero heap allocations</b>.
/// </para>
/// <para>
/// Because this is a <see langword="ref struct"/>, it cannot be boxed, captured by async
/// methods, or stored in fields of reference types. Use the static one-shot APIs
/// (<see cref="Compute(System.ReadOnlySpan{byte}, System.ReadOnlySpan{byte}, System.Span{byte})"/>
/// and <see cref="Verify"/>) when possible; use the incremental
/// (<see cref="Update"/>/<see cref="FinalizeTag(System.Span{byte})"/>) API when
/// message data arrives in chunks.
/// </para>
/// <para>
/// <strong>Lifetime:</strong> Call <see cref="Clear"/> when finished to securely zero all
/// sensitive key material. Since <c>ref struct</c> cannot implement
/// <see cref="System.IDisposable"/>, <c>using</c> statements are not available; prefer
/// a <c>try/finally</c> block instead.
/// </para>
/// </remarks>
[System.Diagnostics.StackTraceHidden]
[System.Diagnostics.DebuggerNonUserCode]
[System.Runtime.CompilerServices.SkipLocalsInit]
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public ref struct Poly1305
{
    #region Constants

    /// <summary>
    /// The size, in bytes, of the Poly1305 key (32 bytes = 256 bits).
    /// </summary>
    public const byte KeySize = 32;

    /// <summary>
    /// The size, in bytes, of the authentication tag produced by Poly1305 (16 bytes = 128 bits).
    /// </summary>
    public const byte TagSize = 16;

    /// <summary>
    /// Block size in bytes for Poly1305 message processing.
    /// </summary>
    private const byte BlockBytes = 16;

    /// <summary>
    /// Bits carried by each of the five limbs that represent a 130-bit value.
    /// </summary>
    private const int LimbBits = 26;

    /// <summary>
    /// Mask of one limb: <c>2²⁶ − 1</c>.
    /// </summary>
    private const uint LimbMask = (1u << LimbBits) - 1u;

    /// <summary>
    /// The implicit high bit appended to every full 16-byte block per RFC 8439 §2.5.1.
    /// </summary>
    private const uint BlockHighBit = 1u << 24;

    #endregion Constants

    #region Inline Array Definitions

    /// <summary>
    /// Inline buffer: 16 × <see cref="byte"/> = 16 bytes.
    /// Used to hold a pending partial message block (0–16 bytes).
    /// </summary>
    [System.Runtime.CompilerServices.InlineArray(BlockBytes)]
    private struct ByteBlock16
    {
        private byte _e0;
    }

    #endregion Inline Array Definitions



    #region Fields

    /// <summary>The clamped key <c>r</c>, as five 26-bit limbs.</summary>
    private Limbs _r;

    /// <summary>
    /// <c>r[1..4] × 5</c>, precomputed. The reduction folds the high limbs back in multiplied by
    /// five (since 2¹³⁰ ≡ 5 mod p), so keeping these out of the inner loop saves four multiplies
    /// per block.
    /// </summary>
    private Limbs _r5;

    /// <summary>The <c>s</c> part of the key, as four little-endian 32-bit words.</summary>
    private uint _pad0;

    /// <summary>The second word of <c>s</c>.</summary>
    private uint _pad1;

    /// <summary>The third word of <c>s</c>.</summary>
    private uint _pad2;

    /// <summary>The fourth word of <c>s</c>.</summary>
    private uint _pad3;

    /// <summary>The running accumulator <c>h</c>, as five 26-bit limbs.</summary>
    private Limbs _acc;

    /// <summary>
    /// Buffer holding a partial (not-yet-full) message block between <see cref="Update"/> calls.
    /// </summary>
    private ByteBlock16 _pending;

    /// <summary>
    /// Number of valid bytes in <see cref="_pending"/> (0–16).
    /// </summary>
    private int _pendingLen;

    /// <summary>
    /// Whether <see cref="FinalizeTag(System.Span{byte})"/> has already been called.
    /// </summary>
    private bool _finalized;

    /// <summary>
    /// Whether <see cref="Clear"/> has been called (analogous to disposed).
    /// </summary>
    private bool _cleared;

    #endregion Fields

    #region Constructors

    /// <summary>
    /// Initializes a new <see cref="Poly1305"/> instance using a 32-byte key.
    /// </summary>
    /// <param name="key">
    /// A 32-byte key. The first 16 bytes are clamped and used as <c>r</c>;
    /// the last 16 bytes are used as <c>s</c>.
    /// </param>
    /// <exception cref="System.ArgumentException">
    /// <paramref name="key"/> length is not <see cref="KeySize"/> (32) bytes.
    /// </exception>
    public Poly1305(System.ReadOnlySpan<byte> key)
    {
        if (key.Length != KeySize)
        {
            throw new System.ArgumentException(
                $"Key must be {KeySize} bytes.", nameof(key));
        }

        _acc = default;
        _pending = default;
        _pendingLen = 0;
        _finalized = false;
        _cleared = false;

        ClampR(key[..16], ref _r, ref _r5);

        System.ReadOnlySpan<byte> sBytes = key.Slice(16, 16);
        _pad0 = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(sBytes[..4]);
        _pad1 = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(sBytes.Slice(4, 4));
        _pad2 = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(sBytes.Slice(8, 4));
        _pad3 = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(sBytes.Slice(12, 4));
    }

    #endregion Constructors

    #region Public — Static One-Shot API

    /// <summary>
    /// Computes the Poly1305 MAC for <paramref name="message"/> using <paramref name="key"/>
    /// and writes the 16-byte tag into <paramref name="destination"/>.
    /// </summary>
    /// <param name="key">A 32-byte key.</param>
    /// <param name="message">The message to authenticate.</param>
    /// <param name="destination">
    /// Destination span; must be at least <see cref="TagSize"/> (16) bytes.
    /// </param>
    /// <exception cref="System.ArgumentException">
    /// <paramref name="key"/> is not 32 bytes or <paramref name="destination"/> is too small.
    /// </exception>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static void Compute(
        System.ReadOnlySpan<byte> key,
        System.ReadOnlySpan<byte> message,
        System.Span<byte> destination)
    {
        if (key.Length != KeySize)
        {
            throw new System.ArgumentException(
                $"Key must be {KeySize} bytes.", nameof(key));
        }

        if (destination.Length < TagSize)
        {
            throw new System.ArgumentException(
                $"Destination buffer must be at least {TagSize} bytes.", nameof(destination));
        }

        Poly1305 poly = new(key);

        try
        {
            poly.ComputeTag(message, destination);
        }
        finally
        {
            poly.Clear();
        }
    }

    /// <summary>
    /// Computes the Poly1305 MAC and returns a new 16-byte array.
    /// </summary>
    /// <param name="key">A 32-byte key.</param>
    /// <param name="message">The message to authenticate.</param>
    /// <returns>A 16-byte authentication tag.</returns>
    /// <exception cref="System.ArgumentException">Thrown when <paramref name="key"/> is not <see cref="KeySize"/> bytes.</exception>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static byte[] Compute(
        System.ReadOnlySpan<byte> key,
        System.ReadOnlySpan<byte> message)
    {
        byte[] tag = new byte[TagSize];
        Compute(key, message, tag);
        return tag;
    }

    /// <summary>
    /// Computes the Poly1305 MAC and returns a new 16-byte array (array overload).
    /// </summary>
    /// <param name="key">A 32-byte key.</param>
    /// <param name="message">The message to authenticate.</param>
    /// <returns>A 16-byte authentication tag.</returns>
    /// <exception cref="System.ArgumentNullException">
    /// <paramref name="key"/> or <paramref name="message"/> is <see langword="null"/>.
    /// </exception>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static byte[] Compute(byte[] key, byte[] message)
    {
        System.ArgumentNullException.ThrowIfNull(key);
        System.ArgumentNullException.ThrowIfNull(message);

        return Compute(
            System.MemoryExtensions.AsSpan(key),
            System.MemoryExtensions.AsSpan(message));
    }

    /// <summary>
    /// Verifies a Poly1305 MAC against a message using the specified key.
    /// Uses constant-time comparison to prevent timing side-channel attacks.
    /// </summary>
    /// <param name="key">A 32-byte key.</param>
    /// <param name="message">The message to verify.</param>
    /// <param name="tag">The 16-byte authentication tag to verify against.</param>
    /// <returns>
    /// <see langword="true"/> if <paramref name="tag"/> is valid; otherwise <see langword="false"/>.
    /// </returns>
    /// <exception cref="System.ArgumentException">
    /// <paramref name="tag"/> is not <see cref="TagSize"/> (16) bytes, or <paramref name="key"/> is not <see cref="KeySize"/> bytes.
    /// </exception>
    public static bool Verify(
        System.ReadOnlySpan<byte> key,
        System.ReadOnlySpan<byte> message,
        System.ReadOnlySpan<byte> tag)
    {
        if (tag.Length != TagSize)
        {
            throw new System.ArgumentException(
                $"Tag must be {TagSize} bytes.", nameof(tag));
        }

        System.Span<byte> computedTag = stackalloc byte[TagSize];
        Compute(key, message, computedTag);

        return BitwiseOperations.FixedTimeEquals(tag, computedTag);
    }

    #endregion Public — Static One-Shot API

    #region Public — Instance One-Shot

    /// <summary>
    /// Computes the Poly1305 MAC for <paramref name="message"/> and writes the 16-byte tag
    /// into <paramref name="destination"/>.
    /// </summary>
    /// <param name="message">The message to authenticate.</param>
    /// <param name="destination">
    /// Destination span; must be at least <see cref="TagSize"/> (16) bytes.
    /// </param>
    /// <exception cref="System.ObjectDisposedException">This instance has been cleared.</exception>
    /// <exception cref="System.ArgumentException">
    /// <paramref name="destination"/> is too small.
    /// </exception>
    public void ComputeTag(
        System.ReadOnlySpan<byte> message,
        System.Span<byte> destination)
    {
        this.ThrowIfCleared();

        if (destination.Length < TagSize)
        {
            throw new System.ArgumentException(
                $"Destination buffer must be at least {TagSize} bytes.", nameof(destination));
        }

        // The one-shot path ignores whatever Update may have accumulated and starts from zero.
        Limbs h = default;

        int fullBlocks = message.Length / BlockBytes;
        if (fullBlocks > 0)
        {
            this.AbsorbFullBlocks(ref h, message[..(fullBlocks * BlockBytes)]);
        }

        System.ReadOnlySpan<byte> tail = message[(fullBlocks * BlockBytes)..];
        if (!tail.IsEmpty)
        {
            this.AbsorbFinalBlock(ref h, tail);
        }

        this.FinalizeTagCore(ref h, destination);

        // Securely zero all sensitive key material after one-shot use
        this.Clear();
    }

    #endregion Public — Instance One-Shot

    #region Public — Incremental API

    /// <summary>
    /// Incrementally absorbs message data. May be called multiple times before
    /// <see cref="FinalizeTag(System.Span{byte})"/>.
    /// </summary>
    /// <param name="data">Next chunk of the message.</param>
    /// <exception cref="System.ObjectDisposedException">This instance has been cleared.</exception>
    /// <exception cref="System.InvalidOperationException">
    /// Called after <see cref="FinalizeTag(System.Span{byte})"/>.
    /// </exception>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public void Update(scoped System.ReadOnlySpan<byte> data)
    {
        this.ThrowIfCleared();

        if (_finalized)
        {
            throw new System.InvalidOperationException(
                "Poly1305 has already been finalized.");
        }

        System.Span<byte> pendingSpan = _pending;

        // ── Top up the pending buffer to a full 16-byte block first ──
        if (_pendingLen > 0)
        {
            int need = BlockBytes - _pendingLen;
            int take = data.Length < need ? data.Length : need;

            if (take > 0)
            {
                data[..take].CopyTo(pendingSpan[_pendingLen..]);
                _pendingLen += take;
                data = data[take..];
            }

            if (_pendingLen is BlockBytes)
            {
                this.AbsorbFullBlocks(ref _acc, pendingSpan);
                _pendingLen = 0;
            }
        }

        // ── Absorb whole blocks straight from the caller's buffer ──
        int fullBlocks = data.Length / BlockBytes;
        if (fullBlocks > 0)
        {
            this.AbsorbFullBlocks(ref _acc, data[..(fullBlocks * BlockBytes)]);
            data = data[(fullBlocks * BlockBytes)..];
        }

        // ── Stash the remaining tail (< 16 bytes) ──
        if (!data.IsEmpty)
        {
            data.CopyTo(pendingSpan[_pendingLen..]);
            _pendingLen += data.Length;
        }
    }

    /// <summary>
    /// Finalizes the MAC computation and writes the 16-byte tag into <paramref name="tag16"/>.
    /// After finalization, further <see cref="Update"/> calls will throw.
    /// </summary>
    /// <param name="tag16">
    /// Destination span; must be at least <see cref="TagSize"/> (16) bytes.
    /// </param>
    /// <exception cref="System.ObjectDisposedException">This instance has been cleared.</exception>
    /// <exception cref="System.ArgumentException">
    /// <paramref name="tag16"/> is shorter than 16 bytes.
    /// </exception>
    /// <exception cref="System.InvalidOperationException">Already finalized.</exception>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public void FinalizeTag(System.Span<byte> tag16)
    {
        this.ThrowIfCleared();

        if (tag16.Length < TagSize)
        {
            throw new System.ArgumentException(
                $"Tag buffer must be {TagSize} bytes.", nameof(tag16));
        }

        if (_finalized)
        {
            throw new System.InvalidOperationException(
                "Poly1305 has already been finalized.");
        }

        if (_pendingLen > 0)
        {
            this.AbsorbFinalBlock(ref _acc, ((System.ReadOnlySpan<byte>)_pending)[.._pendingLen]);

            ((System.Span<byte>)_pending).Clear();
            _pendingLen = 0;
        }

        this.FinalizeTagCore(ref _acc, tag16);

        _finalized = true;

        // Securely zero all sensitive key material and internal state
        this.Clear();
    }

    /// <summary>
    /// Finalizes the MAC computation and returns a new 16-byte array containing the tag.
    /// </summary>
    /// <returns>A 16-byte authentication tag.</returns>
    /// <exception cref="System.ObjectDisposedException">This instance has been cleared.</exception>
    /// <exception cref="System.InvalidOperationException">Already finalized.</exception>
    public byte[] FinalizeTag()
    {
        byte[] tag = new byte[TagSize];
        this.FinalizeTag(tag);
        return tag;
    }

    /// <summary>
    /// Convenience method: absorbs <paramref name="message"/> via <see cref="Update"/> and then
    /// finalizes via <see cref="FinalizeTag(System.Span{byte})"/>.
    /// </summary>
    /// <param name="message">The full message to authenticate.</param>
    /// <param name="destination">
    /// Destination span; must be at least <see cref="TagSize"/> (16) bytes.
    /// </param>
    /// <exception cref="System.ObjectDisposedException">This instance has been cleared.</exception>
    /// <exception cref="System.ArgumentException"><paramref name="destination"/> is too small.</exception>
    /// <exception cref="System.InvalidOperationException">Thrown if the instance has already been finalized.</exception>
    public void ComputeTagIncremental(
        System.ReadOnlySpan<byte> message,
        System.Span<byte> destination)
    {
        this.Update(message);
        this.FinalizeTag(destination);
    }

    #endregion Public — Incremental API

    #region Public — Clear (replaces Dispose)

    /// <summary>
    /// Securely zeroes all sensitive key material and internal state.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Because <see langword="ref struct"/> cannot implement <see cref="System.IDisposable"/>,
    /// call this method explicitly (preferably in a <c>finally</c> block) when done.
    /// </para>
    /// <para>
    /// Uses <see cref="MemorySecurity.ZeroMemory(System.Span{byte})"/>
    /// to guarantee the JIT will not elide the zeroing.
    /// </para>
    /// </remarks>
    [System.Diagnostics.DebuggerNonUserCode]
    public void Clear()
    {
        if (!_cleared)
        {
            MemorySecurity.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(
                System.Runtime.InteropServices.MemoryMarshal.CreateSpan(ref _r, 0x01)));
            MemorySecurity.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(
                System.Runtime.InteropServices.MemoryMarshal.CreateSpan(ref _r5, 0x01)));
            MemorySecurity.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(
                System.Runtime.InteropServices.MemoryMarshal.CreateSpan(ref _acc, 0x01)));
            MemorySecurity.ZeroMemory(_pending);

            _pad0 = 0;
            _pad1 = 0;
            _pad2 = 0;
            _pad3 = 0;

            _pendingLen = 0;
            _cleared = true;
        }
    }

    #endregion Public — Clear (replaces Dispose)

    #region Private — Initialization

    /// <summary>
    /// Clamps <c>r</c> per RFC 8439 §2.5 and splits it into five 26-bit limbs, together with the
    /// <c>r × 5</c> values the reduction needs.
    /// </summary>
    /// <param name="rBytes">The first 16 bytes of the key.</param>
    /// <param name="r">Destination for the clamped limbs.</param>
    /// <param name="r5">Destination for <c>r[1..4] × 5</c>; element 0 is unused.</param>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private static void ClampR(
        System.ReadOnlySpan<byte> rBytes,
        ref Limbs r,
        ref Limbs r5)
    {
        System.Diagnostics.Debug.Assert(rBytes.Length >= 16);

        uint t0 = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(rBytes[..4]);
        uint t1 = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(rBytes.Slice(4, 4));
        uint t2 = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(rBytes.Slice(8, 4));
        uint t3 = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(rBytes.Slice(12, 4));

        // The clamp masks are the 26-bit-limb form of clearing the top four bits of each 32-bit
        // word of r and the low two bits of the upper three, which is what §2.5 prescribes.
        r.L0 = t0 & 0x03FF_FFFFu;
        r.L1 = ((t0 >> 26) | (t1 << 6)) & 0x03FF_FF03u;
        r.L2 = ((t1 >> 20) | (t2 << 12)) & 0x03FF_C0FFu;
        r.L3 = ((t2 >> 14) | (t3 << 18)) & 0x03F0_3FFFu;
        r.L4 = (t3 >> 8) & 0x000F_FFFFu;

        r5.L0 = 0u;
        r5.L1 = r.L1 * 5u;
        r5.L2 = r.L2 * 5u;
        r5.L3 = r.L3 * 5u;
        r5.L4 = r.L4 * 5u;
    }

    #endregion Private — Initialization

    #region Private — Guard

    /// <summary>
    /// Throws <see cref="System.ObjectDisposedException"/> if <see cref="Clear"/> has been called.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private readonly void ThrowIfCleared()
    {
        if (_cleared)
        {
            throw new System.ObjectDisposedException(
                nameof(Poly1305),
                $"This {nameof(Poly1305)} instance has been cleared.");
        }
    }

    #endregion Private — Guard

    #region Private — Block Processing

    /// <summary>
    /// A 130-bit value held as five 26-bit limbs, little-endian.
    /// </summary>
    /// <remarks>
    /// Splitting at 26 bits rather than 32 is what makes the inner loop cheap: the five partial
    /// products of <c>h × r</c> each fit in a <see cref="ulong"/> with room to spare, so the whole
    /// multiply-and-reduce runs without a single carry chain over 32-bit words.
    /// </remarks>
    private struct Limbs
    {
        /// <summary>Bits 0–25.</summary>
        public uint L0;

        /// <summary>Bits 26–51.</summary>
        public uint L1;

        /// <summary>Bits 52–77.</summary>
        public uint L2;

        /// <summary>Bits 78–103.</summary>
        public uint L3;

        /// <summary>Bits 104–129.</summary>
        public uint L4;
    }

    /// <summary>
    /// Absorbs one or more whole 16-byte blocks into <paramref name="h"/>.
    /// </summary>
    /// <param name="h">The accumulator, updated in place.</param>
    /// <param name="blocks">A whole number of 16-byte blocks.</param>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private readonly void AbsorbFullBlocks(
        ref Limbs h,
        scoped System.ReadOnlySpan<byte> blocks)
    {
        System.Diagnostics.Debug.Assert(blocks.Length % BlockBytes == 0);

        for (int offset = 0; offset < blocks.Length; offset += BlockBytes)
        {
            this.AbsorbBlock(ref h, blocks.Slice(offset, BlockBytes), BlockHighBit);
        }
    }

    /// <summary>
    /// Absorbs the last, short block: the 0x01 sentinel goes inside the block instead of the
    /// implicit high bit a full block carries.
    /// </summary>
    /// <param name="h">The accumulator, updated in place.</param>
    /// <param name="tail">Between 1 and 15 bytes.</param>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private readonly void AbsorbFinalBlock(
        ref Limbs h,
        scoped System.ReadOnlySpan<byte> tail)
    {
        System.Diagnostics.Debug.Assert(tail.Length is > 0 and < BlockBytes);

        System.Span<byte> padded = stackalloc byte[BlockBytes];
        padded.Clear();

        tail.CopyTo(padded);
        padded[tail.Length] = 0x01;

        this.AbsorbBlock(ref h, padded, 0u);

        MemorySecurity.ZeroMemory(padded);
    }

    /// <summary>
    /// Computes <c>h = (h + block) × r mod 2¹³⁰ − 5</c> for one block.
    /// </summary>
    /// <param name="h">The accumulator, updated in place.</param>
    /// <param name="block">Exactly 16 bytes.</param>
    /// <param name="highBit">
    /// <see cref="BlockHighBit"/> for a full block, or zero for the padded final block.
    /// </param>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private readonly void AbsorbBlock(
        ref Limbs h,
        scoped System.ReadOnlySpan<byte> block,
        uint highBit)
    {
        uint t0 = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(block[..4]);
        uint t1 = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(4, 4));
        uint t2 = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(8, 4));
        uint t3 = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(12, 4));

        // h += block
        uint h0 = h.L0 + (t0 & LimbMask);
        uint h1 = h.L1 + (((t0 >> 26) | (t1 << 6)) & LimbMask);
        uint h2 = h.L2 + (((t1 >> 20) | (t2 << 12)) & LimbMask);
        uint h3 = h.L3 + (((t2 >> 14) | (t3 << 18)) & LimbMask);
        uint h4 = h.L4 + ((t3 >> 8) | highBit);

        uint r0 = _r.L0, r1 = _r.L1, r2 = _r.L2, r3 = _r.L3, r4 = _r.L4;
        uint s1 = _r5.L1, s2 = _r5.L2, s3 = _r5.L3, s4 = _r5.L4;

        // h *= r, with the limbs above 2¹³⁰ folded back in times five.
        ulong d0 = ((ulong)h0 * r0) + ((ulong)h1 * s4) + ((ulong)h2 * s3) + ((ulong)h3 * s2) + ((ulong)h4 * s1);
        ulong d1 = ((ulong)h0 * r1) + ((ulong)h1 * r0) + ((ulong)h2 * s4) + ((ulong)h3 * s3) + ((ulong)h4 * s2);
        ulong d2 = ((ulong)h0 * r2) + ((ulong)h1 * r1) + ((ulong)h2 * r0) + ((ulong)h3 * s4) + ((ulong)h4 * s3);
        ulong d3 = ((ulong)h0 * r3) + ((ulong)h1 * r2) + ((ulong)h2 * r1) + ((ulong)h3 * r0) + ((ulong)h4 * s4);
        ulong d4 = ((ulong)h0 * r4) + ((ulong)h1 * r3) + ((ulong)h2 * r2) + ((ulong)h3 * r1) + ((ulong)h4 * r0);

        // Single carry pass; the accumulator is left partially reduced on purpose, which is what
        // lets the next block start without a full modular reduction.
        ulong carry = d0 >> LimbBits;
        h0 = (uint)d0 & LimbMask;

        d1 += carry;
        carry = d1 >> LimbBits;
        h1 = (uint)d1 & LimbMask;

        d2 += carry;
        carry = d2 >> LimbBits;
        h2 = (uint)d2 & LimbMask;

        d3 += carry;
        carry = d3 >> LimbBits;
        h3 = (uint)d3 & LimbMask;

        d4 += carry;
        carry = d4 >> LimbBits;
        h4 = (uint)d4 & LimbMask;

        h0 += (uint)carry * 5u;
        h1 += h0 >> LimbBits;
        h0 &= LimbMask;

        h.L0 = h0;
        h.L1 = h1;
        h.L2 = h2;
        h.L3 = h3;
        h.L4 = h4;
    }

    #endregion Private — Block Processing

    #region Private — Tag Finalization

    /// <summary>
    /// Produces the final 16-byte tag: <c>tag = (h mod p) + s</c>, serialized little-endian.
    /// </summary>
    /// <param name="h">The partially reduced accumulator. Consumed, not preserved.</param>
    /// <param name="tag">Destination for the 16-byte tag.</param>
    /// <remarks>
    /// The final reduction is a masked select rather than a branch on <c>h ≥ p</c>, so the time it
    /// takes does not depend on the accumulator — the previous implementation compared and
    /// conditionally subtracted, which leaked that comparison through timing.
    /// </remarks>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private readonly void FinalizeTagCore(
        ref Limbs h,
        System.Span<byte> tag)
    {
        System.Diagnostics.Debug.Assert(tag.Length >= TagSize);

        uint h0 = h.L0, h1 = h.L1, h2 = h.L2, h3 = h.L3, h4 = h.L4;

        // Fully carry the accumulator.
        uint c = h1 >> LimbBits;
        h1 &= LimbMask;

        h2 += c;
        c = h2 >> LimbBits;
        h2 &= LimbMask;

        h3 += c;
        c = h3 >> LimbBits;
        h3 &= LimbMask;

        h4 += c;
        c = h4 >> LimbBits;
        h4 &= LimbMask;

        h0 += c * 5u;
        c = h0 >> LimbBits;
        h0 &= LimbMask;

        h1 += c;

        // Compute h + (-p) = h + 5 - 2¹³⁰ and keep it only if it did not go negative.
        uint g0 = h0 + 5u;
        c = g0 >> LimbBits;
        g0 &= LimbMask;

        uint g1 = h1 + c;
        c = g1 >> LimbBits;
        g1 &= LimbMask;

        uint g2 = h2 + c;
        c = g2 >> LimbBits;
        g2 &= LimbMask;

        uint g3 = h3 + c;
        c = g3 >> LimbBits;
        g3 &= LimbMask;

        uint g4 = unchecked(h4 + c - (1u << LimbBits));

        // g4's borrow bit selects between h and g without branching: the subtraction above
        // borrowed exactly when h < p, and then mask is 0 so h is kept unchanged.
        uint mask = unchecked((g4 >> 31) - 1u);

        g0 &= mask;
        g1 &= mask;
        g2 &= mask;
        g3 &= mask;
        g4 &= mask;

        mask = ~mask;

        h0 = (h0 & mask) | g0;
        h1 = (h1 & mask) | g1;
        h2 = (h2 & mask) | g2;
        h3 = (h3 & mask) | g3;
        h4 = (h4 & mask) | g4;

        // Repack the limbs into four 32-bit words.
        h0 = h0 | (h1 << 26);
        h1 = (h1 >> 6) | (h2 << 20);
        h2 = (h2 >> 12) | (h3 << 14);
        h3 = (h3 >> 18) | (h4 << 8);

        // tag = h + s, 128-bit addition.
        ulong f = (ulong)h0 + _pad0;
        h0 = (uint)f;

        f = (ulong)h1 + _pad1 + (f >> 32);
        h1 = (uint)f;

        f = (ulong)h2 + _pad2 + (f >> 32);
        h2 = (uint)f;

        f = (ulong)h3 + _pad3 + (f >> 32);
        h3 = (uint)f;

        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(tag[..4], h0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(tag.Slice(4, 4), h1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(tag.Slice(8, 4), h2);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(tag.Slice(12, 4), h3);
    }

    #endregion Private — Tag Finalization
}
