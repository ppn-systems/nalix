// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics;
using System.Threading;

namespace Nalix.Framework.Memory.Internal.PoolTypes;

/// <summary>
/// A diagnostic sentinel attached to rented objects to track their lifetime
/// and detect leaks via finalization.
/// </summary>
internal sealed class PoolSentinel
{
    #region Fields

    private static long s_totalLeaked;

    private readonly WeakReference<object> _weakTarget;
    private readonly Type _objectType;
    private readonly long _rentTimestamp;
    private readonly string? _stackTrace;

    private bool _returned;

    #endregion Fields

    #region Properties

    /// <summary>
    /// Gets the total number of objects that were leaked (GC'd without return).
    /// </summary>
    public static long TotalLeaked => Interlocked.Read(ref s_totalLeaked);

    /// <summary>
    /// Gets the timestamp when the object was rented.
    /// </summary>
    public long RentTimestamp => _rentTimestamp;

    /// <summary>
    /// Gets the stack trace captured during the rent operation.
    /// </summary>
    public string? StackTrace => _stackTrace;

    /// <summary>
    /// Gets the type of the pooled object.
    /// </summary>
    public Type ObjectType => _objectType;

    /// <summary>
    /// Gets if the target is still alive.
    /// </summary>
    public bool IsAlive => _weakTarget.TryGetTarget(out _);

    /// <summary>
    /// Gets if the associated object has been returned to the pool.
    /// </summary>
    public bool IsReturned => _returned;

    #endregion Properties

    #region Constructor

    /// <summary>
    /// Initializes a new instance of the <see cref="PoolSentinel"/> class.
    /// </summary>
    public PoolSentinel(object target, bool captureStackTrace)
    {
        _weakTarget = new WeakReference<object>(target);
        _objectType = target.GetType();
        _rentTimestamp = Stopwatch.GetTimestamp();

        if (captureStackTrace)
        {
            _stackTrace = System.Environment.StackTrace;
        }
    }

    #endregion Constructor

    /// <summary>
    /// Marks the associated object as returned to the pool.
    /// </summary>
    public void MarkReturned() => _returned = true;

    /// <summary>
    /// Finalizer to detect leaks.
    /// </summary>
    ~PoolSentinel()
    {
        if (!_returned)
        {
            _ = Interlocked.Increment(ref s_totalLeaked);

            // Note: We cannot safely log to ILogger here as the logger itself 
            // might be finalized or out of scope. We print to console as a last resort
            // or rely on the analytics report to show s_totalLeaked.
            Console.WriteLine($"\n[FW.Pool] LEAK DETECTED: Object of type {_objectType.Name} was GC'd without being returned to the pool.");
            if (!string.IsNullOrEmpty(_stackTrace))
            {
                Console.WriteLine($"Allocation StackTrace:\n{_stackTrace}\n");
            }
        }
    }
}
