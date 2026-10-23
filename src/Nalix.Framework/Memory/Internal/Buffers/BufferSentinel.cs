// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics;
using System.Threading;

namespace Nalix.Framework.Memory.Internal.Buffers;

internal sealed class BufferSentinel
{
    private static long s_totalLeaked;

    private readonly WeakReference<byte[]> _weakTarget;
    private readonly int _size;
    private readonly long _rentTimestamp;
    private readonly string? _stackTrace;
    private bool _returned;

    public static long TotalLeaked => Interlocked.Read(ref s_totalLeaked);

    public long RentTimestamp => _rentTimestamp;
    public string? StackTrace => _stackTrace;
    public int Size => _size;
    public bool IsReturned => _returned;
    public bool IsAlive => _weakTarget.TryGetTarget(out _);

    public BufferSentinel(byte[] target, bool captureStackTrace)
    {
        _weakTarget = new WeakReference<byte[]>(target);
        _size = target.Length;
        _rentTimestamp = Stopwatch.GetTimestamp();

        if (captureStackTrace)
        {
            _stackTrace = System.Environment.StackTrace;
        }
    }

    public void MarkReturned() => _returned = true;

    ~BufferSentinel()
    {
        if (!_returned)
        {
            _ = Interlocked.Increment(ref s_totalLeaked);

            Console.WriteLine($"\n[FW.Memory] LEAK DETECTED: Buffer of size {_size} was GC'd without being returned.");
            if (!string.IsNullOrEmpty(_stackTrace))
            {
                Console.WriteLine($"Allocation StackTrace:\n{_stackTrace}\n");
            }
        }
    }
}
