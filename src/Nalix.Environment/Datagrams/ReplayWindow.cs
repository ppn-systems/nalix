// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;

namespace Nalix.Environment.Datagrams;

/// <summary>
/// Bitmap of the last <see cref="Size"/> received packet numbers. <see cref="IsNew"/> is checked before
/// decryption and does not change state; <see cref="Mark"/> is called only after the packet authenticated,
/// so forged datagrams cannot move the window.
/// </summary>
internal sealed class ReplayWindow
{
    public const int Size = 2048;

    private readonly ulong[] _bits = new ulong[Size / 64];

    /// <summary>Gets the largest packet number marked, or -1 when none.</summary>
    public long Largest { get; private set; } = -1;

    public bool IsNew(ulong packetNumber)
    {
        if (this.Largest < 0 || packetNumber > (ulong)this.Largest)
        {
            return true;
        }

        ulong age = (ulong)this.Largest - packetNumber;
        if (age >= Size)
        {
            return false;
        }

        return (_bits[(int)(packetNumber % Size) >> 6] & (1UL << (int)(packetNumber & 63))) == 0;
    }

    public void Mark(ulong packetNumber)
    {
        if (this.Largest < 0 || packetNumber > (ulong)this.Largest)
        {
            ulong start = this.Largest < 0 ? packetNumber : (ulong)this.Largest + 1;
            if (packetNumber - start >= Size)
            {
                Array.Clear(_bits);
            }
            else
            {
                for (ulong pn = start; pn < packetNumber; pn++)
                {
                    this.Clear(pn);
                }
            }

            this.Largest = (long)packetNumber;
        }

        _bits[(int)(packetNumber % Size) >> 6] |= 1UL << (int)(packetNumber & 63);
    }

    private void Clear(ulong packetNumber) => _bits[(int)(packetNumber % Size) >> 6] &= ~(1UL << (int)(packetNumber & 63));
}
