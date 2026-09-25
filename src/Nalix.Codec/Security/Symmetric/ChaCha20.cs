// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Exceptions;
using Nalix.Codec.Internal;
using Nalix.Codec.Security.Primitives;

namespace Nalix.Codec.Security.Symmetric;

/// <summary>
/// Provides ChaCha20 stream cipher encryption and decryption (RFC 7539).
/// </summary>
/// <remarks>
/// <para>
/// This implementation uses <see cref="System.Runtime.CompilerServices.InlineArrayAttribute"/>
/// to embed all working buffers (state, working copy, keystream) directly inside the struct
/// layout of the class instance, eliminating the three managed-array heap allocations
/// (<c>UInt32[16]</c>, <c>UInt32[16]</c>, <c>Byte[64]</c>) that the previous version required.
/// </para>
/// <para>
/// <strong>Thread-safety:</strong> This instance is <b>NOT</b> thread-safe because it mutates
/// shared inline buffers during encryption. For concurrent use, create separate instances or
/// implement instance pooling.
/// </para>
/// <para>
/// See <see href="https://tools.ietf.org/html/rfc7539">RFC 7539</see> for the full specification.
/// </para>
/// </remarks>
[System.Runtime.CompilerServices.SkipLocalsInit]
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public struct ChaCha20
{
    #region Constants

    /// <summary>Required key length in bytes (256-bit).</summary>
    public const byte KeySize = 32;

    /// <summary>Required nonce length in bytes (96-bit).</summary>
    public const byte NonceSize = 12;

    /// <summary>Size of a single keystream block in bytes.</summary>
    public const byte BlockSize = 64;

    /// <summary>Number of 32-bit words in the ChaCha20 state matrix.</summary>
    public const byte StateLength = 16;

    #endregion Constants

    #region Inline Array Definitions

    [System.Runtime.CompilerServices.InlineArray(StateLength)]
    private struct StateBuffer
    {
        private uint _e0;
    }

    [System.Runtime.CompilerServices.InlineArray(2 * BlockSize)]
    private struct KeystreamBuffer
    {
        private byte _e0;
    }

    #endregion Inline Array Definitions

    #region Fields

    private bool _cleared;
    private StateBuffer _state;
    private KeystreamBuffer _keystream;

    #endregion Fields

    #region Constructors

    /// <summary>
    /// Initializes a new <see cref="ChaCha20"/> instance with the specified key, nonce, and
    /// initial block counter.
    /// </summary>
    /// <param name="key"></param>
    /// <param name="nonce"></param>
    /// <param name="counter"></param>
    /// <exception cref="System.ArgumentNullException">Thrown when <paramref name="key"/> or <paramref name="nonce"/> is null.</exception>
    /// <exception cref="Abstractions.Exceptions.CipherException">Thrown when the key or nonce length is invalid.</exception>
    public ChaCha20(byte[] key, byte[] nonce, uint counter)
    {
        System.ArgumentNullException.ThrowIfNull(key);
        System.ArgumentNullException.ThrowIfNull(nonce);

        this.InitializeKey(new System.ReadOnlySpan<byte>(key));
        this.InitializeNonce(new System.ReadOnlySpan<byte>(nonce), counter);
    }

    /// <summary>
    /// Initializes a new <see cref="ChaCha20"/> instance with the specified key, nonce, and
    /// initial block counter using spans (zero-copy).
    /// </summary>
    /// <param name="key"></param>
    /// <param name="nonce"></param>
    /// <param name="counter"></param>
    /// <exception cref="Abstractions.Exceptions.CipherException">Thrown when the key or nonce length is invalid.</exception>
    public ChaCha20(
        System.ReadOnlySpan<byte> key,
        System.ReadOnlySpan<byte> nonce,
        uint counter)
    {
        this.InitializeKey(key);
        this.InitializeNonce(nonce, counter);
    }

    #endregion Constructors

    #region Public — Generate Key Block

    /// <summary>
    /// Generates one 64-byte keystream block into <paramref name="dst"/> at the current counter,
    /// then advances the internal counter by 1 (per RFC 7539).
    /// </summary>
    /// <param name="dst"></param>
    /// <exception cref="System.ObjectDisposedException">Thrown when this instance has been cleared.</exception>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public void GenerateKeyBlock(scoped System.Span<byte> dst)
    {
        this.ThrowIfCleared();

        System.Span<uint> stateSpan = _state;
        System.Span<byte> keystreamSpan = _keystream;

        GenerateBlock(stateSpan, keystreamSpan);

        int n = dst.Length < BlockSize ? dst.Length : BlockSize;
        keystreamSpan[..n].CopyTo(dst);
    }

    #endregion Public — Generate Key Block

    #region Encryption Methods

    /// <summary>
    /// Encrypts <paramref name="src"/> into <paramref name="dst"/>.
    /// Returns number of bytes written.
    /// </summary>
    /// <param name="src"></param>
    /// <param name="dst"></param>
    /// <exception cref="System.ObjectDisposedException">Thrown when this instance has been cleared.</exception>
    /// <exception cref="Abstractions.Exceptions.CipherException">Thrown when the destination length does not match the source length.</exception>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public int Encrypt(
        System.ReadOnlySpan<byte> src,
        System.Span<byte> dst)
    {
        this.ThrowIfCleared();

        if (dst.Length < src.Length)
        {
            Throw.OutputLengthMismatch();
        }

        this.EncryptSpanInternal(src, dst, src.Length);
        return src.Length;
    }

    #endregion Encryption Methods

    #region Decryption Methods

    /// <summary>
    /// Decrypts <paramref name="src"/> into <paramref name="dst"/>.
    /// Identical to <see cref="Encrypt(System.ReadOnlySpan{byte}, System.Span{byte})"/>.
    /// </summary>
    /// <param name="src"></param>
    /// <param name="dst"></param>
    /// <exception cref="System.ObjectDisposedException">Thrown when this instance has been cleared.</exception>
    /// <exception cref="Abstractions.Exceptions.CipherException">Thrown when the destination length does not match the source length.</exception>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public int Decrypt(System.ReadOnlySpan<byte> src, System.Span<byte> dst) => this.Encrypt(src, dst);

    #endregion Decryption Methods

    #region Public — Reset

    /// <summary>
    /// Securely zeroes all sensitive key material and internal state.
    /// </summary>
    /// <remarks>This method is idempotent.</remarks>
    [System.Diagnostics.DebuggerNonUserCode]
    public void Clear()
    {
        if (!_cleared)
        {
            MemorySecurity.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes((System.Span<uint>)_state));
            MemorySecurity.ZeroMemory(_keystream);
            _cleared = true;
        }
    }

    #endregion Public — Reset

    #region Private — Guard

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private readonly void ThrowIfCleared()
    {
        if (_cleared)
        {
            throw new System.ObjectDisposedException(nameof(ChaCha20), $"This {nameof(ChaCha20)} instance has been cleared.");
        }
    }

    #endregion Private — Guard

    #region Private — Initialization

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static uint LoadLittleEndian32(System.ReadOnlySpan<byte> source, int offset)
        => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(source[offset..]);

    private void InitializeKey(System.ReadOnlySpan<byte> key)
    {
        if (key.Length != KeySize)
        {
            throw new CipherException($"Key length must be {KeySize}. Actual: {key.Length}");
        }

        System.Span<uint> s = _state;
        s[0] = 0x61707865;
        s[1] = 0x3320646e;
        s[2] = 0x79622d32;
        s[3] = 0x6b206574;

        for (int i = 0; i < 8; i++)
        {
            s[4 + i] = LoadLittleEndian32(key, i * 4);
        }
    }

    private void InitializeNonce(System.ReadOnlySpan<byte> nonce, uint counter)
    {
        if (nonce.Length != NonceSize)
        {
            throw new CipherException($"Nonce length must be {NonceSize}. Actual: {nonce.Length}");
        }

        System.Span<uint> s = _state;
        s[12] = counter;

        for (int i = 0; i < 3; i++)
        {
            s[13 + i] = LoadLittleEndian32(nonce, i * 4);
        }
    }

    #endregion Private — Initialization

    #region Private — Core Block Function

    /// <summary>
    /// Writes one 64-byte keystream block for the current counter and advances the counter by one.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static void GenerateBlock(
        System.Span<uint> state,
        System.Span<byte> keystream)
    {
        if (System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated)
        {
            ChaCha20Core.KeyStreamOneBlock(state, keystream);
        }
        else
        {
            ChaCha20Core.KeyStreamScalar(state, keystream);
        }

        AdvanceCounter(state, 1);
    }

    /// <summary>
    /// Advances the block counter by <paramref name="blocks"/>, rejecting the counter wrap that
    /// RFC 8439 §2.4 forbids.
    /// </summary>
    /// <exception cref="Abstractions.Exceptions.CipherException">Thrown when the counter wraps.</exception>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static void AdvanceCounter(System.Span<uint> state, uint blocks)
    {
        uint next = state[12] + blocks;
        state[12] = next;

        if (next < blocks)
        {
            // Counter overflow: MUST NOT reuse key/nonce (RFC 8439 §2.4)
            throw new Abstractions.Exceptions.CipherException("ChaCha20 block counter overflow. Maximum data limit (256 GiB) reached for a single key/nonce pair.");
        }
    }

    #endregion Private — Core Block Function

    #region Private — Span-Based Encrypt Core

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private void EncryptSpanInternal(
        System.ReadOnlySpan<byte> src,
        System.Span<byte> dst,
        int numBytes)
    {
        if (numBytes < 0 || numBytes > src.Length || numBytes > dst.Length)
        {
            throw new System.ArgumentOutOfRangeException(nameof(numBytes));
        }

        System.Span<uint> stateSpan = _state;
        System.Span<byte> keystreamSpan = _keystream;

        int offset = 0;
        int remaining = numBytes;

        // Two blocks per pass while the payload is long enough to fill both 128-bit lanes.
        if (ChaCha20Core.TwoBlockPathSupported)
        {
            const int pairSize = 2 * BlockSize;

            while (remaining >= pairSize)
            {
                ChaCha20Core.KeyStreamTwoBlocks(stateSpan, keystreamSpan);
                AdvanceCounter(stateSpan, 2);
                ChaCha20Core.Xor(src[offset..], keystreamSpan, dst[offset..], pairSize);

                offset += pairSize;
                remaining -= pairSize;
            }
        }

        while (remaining > 0)
        {
            GenerateBlock(stateSpan, keystreamSpan);

            int n = remaining < BlockSize ? remaining : BlockSize;
            ChaCha20Core.Xor(src[offset..], keystreamSpan, dst[offset..], n);

            offset += n;
            remaining -= n;
        }
    }

    #endregion Private — Span-Based Encrypt Core
}
