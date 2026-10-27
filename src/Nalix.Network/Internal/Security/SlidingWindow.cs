// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Nalix.Network.Internal.Security;

/// <summary>
/// Implements a sliding window for sequence number validation to prevent replay attacks.
/// Thread-safe using atomic operations.
/// </summary>
internal sealed class SlidingWindow
{
    private const int SequenceHalfRange = (ushort.MaxValue + 1) / 2;

    private readonly long[] _bitmap;
    private readonly int _arraySize;
    private readonly int _windowSize;

    private ushort _maxSeen;
    private bool _isInitialized;
    private readonly Lock _syncObj;

    /// <summary>
    /// Initializes a new instance of the <see cref="SlidingWindow"/> class.
    /// </summary>
    /// <param name="windowSize">The replay-protection window size in sequence numbers.</param>
    public SlidingWindow(int windowSize = 1024)
    {
        _syncObj = new();
        _windowSize = windowSize;
        _arraySize = (windowSize + 63) / 64;

        _bitmap = new long[_arraySize];
    }

    /// <summary>
    /// Attempts to check and mark a sequence number.
    /// </summary>
    /// <param name="seq">The 32-bit (or 16-bit) sequence number to check.</param>
    /// <returns>True if the sequence number is new and within the window; false if replayed or too old.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryCheck(ushort seq)
    {
        lock (_syncObj)
        {
            if (!_isInitialized)
            {
                _isInitialized = true;
                _maxSeen = seq;
                this.MARK_BIT(0);
                return true;
            }

            ushort currentMax = _maxSeen;
            int forward = unchecked((ushort)(seq - currentMax));

            // 16-bit modular comparison:
            // A forward jump may exceed the window size (burst loss); otherwise the window would never
            // advance and every later datagram would be rejected as too old until the sequence wraps.
            // The jump is still bounded to a quarter of the ring so that a delayed/replayed packet
            // after >32768 newer packets cannot be misinterpreted as a forward jump.
            int maxForwardJump = SequenceHalfRange / 2;
            if (forward != 0 && forward <= maxForwardJump)
            {
                // Advance window
                long shift = forward;
                if (shift >= _windowSize)
                {
                    Array.Clear(_bitmap);
                }
                else
                {
                    this.SHIFT_BITMAP(shift);
                }
                _maxSeen = seq;
                this.MARK_BIT(0);
                return true;
            }

            int diff = unchecked((ushort)(currentMax - seq));
            if (diff >= _windowSize)
            {
                return false; // Too old
            }

            if (this.IS_BIT_SET(diff))
            {
                return false; // Replayed
            }

            this.MARK_BIT(diff);
            return true;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SHIFT_BITMAP(long shift)
    {
        int wholeWords = (int)(shift / 64);
        int bits = (int)(shift % 64);

        if (wholeWords > 0)
        {
            for (int i = _arraySize - 1; i >= wholeWords; i--)
            {
                _bitmap[i] = _bitmap[i - wholeWords];
            }
            for (int i = 0; i < wholeWords; i++)
            {
                _bitmap[i] = 0;
            }
        }

        if (bits > 0)
        {
            for (int i = _arraySize - 1; i >= 0; i--)
            {
                _bitmap[i] <<= bits;
                if (i > 0)
                {
                    _bitmap[i] |= (long)((ulong)_bitmap[i - 1] >> (64 - bits));
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IS_BIT_SET(long diff)
    {
        int word = (int)(diff / 64);
        int bit = (int)(diff % 64);
        return (_bitmap[word] & (1L << bit)) != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void MARK_BIT(long diff)
    {
        int word = (int)(diff / 64);
        int bit = (int)(diff % 64);
        _bitmap[word] |= 1L << bit;
    }
}
