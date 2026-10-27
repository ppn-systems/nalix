// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Contracts;
using System.Runtime.CompilerServices;
using System.Threading;
using Nalix.Abstractions;
using Nalix.Abstractions.Exceptions;
using Nalix.Framework.Injection.DI;

namespace Nalix.Framework.Injection;

/// <summary>
/// High-performance manager that maintains single instances of different types,
/// optimized for real-time server applications with thread safety and caching.
/// </summary>
[DebuggerNonUserCode]
[SkipLocalsInit]
[ExcludeFromCodeCoverage]
[DebuggerDisplay("CachedInstanceCount = {CachedInstanceCount}")]
[DynamicallyAccessedMembers(
    DynamicallyAccessedMemberTypes.NonPublicMethods |
    DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
public sealed partial class InstanceManager : SingletonBase<InstanceManager>, IReportable
{
    #region Constants

    private const int MaxCachedInstances = 4096;
    private const int MaxSignatureInstances = 4096;

    #endregion Constants

    #region Fields

    /// <summary>
    /// Track disposables uniquely to avoid duplicate dispose calls.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<IDisposable, byte> _disposables = new();

    /// <summary>
    /// Use RuntimeTypeHandle as key to reduce hashing overhead.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<RuntimeTypeHandle, object> _instanceCache = new();

    /// <summary>
    /// Activator cache is keyed by (Type, ctor signature) to support overloads.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ActivatorKey, Func<object?[], object>> _activatorCache = new();

    private readonly System.Collections.Concurrent.ConcurrentDictionary<ActivatorKey, object> _signatureInstanceCache = new();

    [ThreadStatic] private static int s_tsSlotsInvalidated;
    [ThreadStatic] private static RuntimeTypeHandle s_tsKey0;
    [ThreadStatic] private static object? s_tsVal0;
    [ThreadStatic] private static InstanceManager? s_tsMgr0;

    [ThreadStatic] private static RuntimeTypeHandle s_tsKey1;
    [ThreadStatic] private static object? s_tsVal1;
    [ThreadStatic] private static InstanceManager? s_tsMgr1;

    /// <summary>
    /// Near fields
    /// </summary>
    private static int s_slotsInvalidated = 1; // Monotonic counter for L1/Slot invalidation

    private long _instanceCreationCount;
    private long _instanceCacheHitCount;

    private int _isDisposed;
    private int _isLocked;

    /// <summary>
    /// Serializes mutating operations (Register / RemoveInstance / Clear) so that the
    /// "is-previous-still-referenced then dispose" decision is atomic and cannot dispose an
    /// instance that a concurrent registration has just re-stored under another handle.
    /// Read paths (GetOrCreateInstance / GetExistingInstance) stay lock-free.
    /// </summary>
    private readonly Lock _mutationLock = new();

    #endregion Fields

    #region Properties

    /// <summary>
    /// Gets the number of cached instances.
    /// </summary>
    [Pure]
    public int CachedInstanceCount => _instanceCache.Count + _signatureInstanceCache.Count;

    #endregion Properties

    #region Constructors

    /// <inheritdoc/>
    public InstanceManager()
    {
    }

    #endregion Constructors

    #region Public API

    /// <summary>
    /// Locks the InstanceManager, preventing any further registrations, removals, or reloads.
    /// This should be called after application initialization is complete to prevent service hijacking.
    /// Once locked, <see cref="Register{T}(T)"/>, <see cref="RemoveInstance(Type)"/>, and
    /// <see cref="Clear(bool)"/> throw <see cref="InvalidOperationException"/>.
    /// </summary>
    public void Lockdown()
    {
        _ = Interlocked.Exchange(ref _isLocked, 1);
        this.Emit(DiagnosticsEvents.Injection.Registered, "FW.InstanceManager:Lockdown", "lockdown");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void THROW_IF_LOCKED()
    {
        if (Interlocked.CompareExchange(ref _isLocked, 0, 0) != 0)
        {
            throw new InvalidOperationException("InstanceManager is locked. Registration and removal are not permitted.");
        }
    }

    /// <summary>
    /// Registers an instance of the specified type in the instance cache.
    /// If the instance implements <see cref="IDisposable"/>, it will be tracked for disposal.
    /// </summary>
    /// <typeparam name="T">The type of the instance to register.</typeparam>
    /// <param name="instance">The instance to register.</param>
    /// <exception cref="ObjectDisposedException">Thrown when the manager has already been disposed.</exception>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Register<T>(T instance) where T : class
    {
        this.THROW_IF_LOCKED();

        ObjectDisposedException.ThrowIf(Interlocked.CompareExchange(ref _isDisposed, 0, 0) != 0, nameof(InstanceManager));

        RuntimeTypeHandle key = typeof(T).TypeHandle;

        // Collect distinct previous objects encountered during atomic replace so we dispose each once.
        HashSet<object> prevsToDispose = new(ReferenceEqualityComparer.Instance);

        // Serialize with RemoveInstance/Clear so the "still-referenced then dispose" decision below
        // cannot race a concurrent registration that re-stores a previous instance under another handle.
        lock (_mutationLock)
        {
            // Atomic add/replace for concrete type.
            TRY_ADD_OR_REPLACE_ATOMIC_COLLECT(key, instance, typeof(T).Name, prevsToDispose);

            // Invalidate L1 cache (increment monotonic version counter)
            _ = Interlocked.Increment(ref s_slotsInvalidated);

            // Interface registration is fully explicit. We check compile-time service mappings
            // populated by the generated code for any attributes mapped on this type.
            if (s_preRegisteredServiceMappings.TryGetValue(typeof(T), out System.Collections.Generic.List<Type>? serviceTypes))
            {
                lock (serviceTypes)
                {
                    for (int i = 0; i < serviceTypes.Count; i++)
                    {
                        Type itf = serviceTypes[i];
                        RuntimeTypeHandle itfKey = itf.TypeHandle;
                        TRY_ADD_OR_REPLACE_ATOMIC_COLLECT(itfKey, instance, itf.Name, prevsToDispose);
                    }
                }
            }

            // Track disposable AFTER instance successfully stored, BEFORE disposing previous,
            // so a previous instance re-stored under another handle is still tracked.
            if (instance is IDisposable disp)
            {
                _ = _disposables.TryAdd(disp, 0);
            }

            // After finishing all replacements, dispose each distinct previous object exactly once.
            foreach (object prev in prevsToDispose)
            {
                if (!_instanceCache.Values.Contains(prev))
                {
                    SAFE_DISPOSE_PREVIOUS(prev, "register-replaced");
                }
            }
        }

        if (instance is IReportable reportable)
        {
            TRY_AUTO_REGISTER_REPORTABLE(reportable);
        }

        this.Emit(DiagnosticsEvents.Injection.Registered, "FW.InstanceManager:Register", $"registered type={typeof(T).Name}");

        // Local helpers

        void TRY_ADD_OR_REPLACE_ATOMIC_COLLECT(RuntimeTypeHandle handleKey, object instanceObj, string humanName, HashSet<object> collectSet)
        {
            while (true)
            {
                if (_instanceCache.TryGetValue(handleKey, out object? existing))
                {
                    // If same reference, nothing to do.
                    if (ReferenceEquals(existing, instanceObj))
                    {
                        return;
                    }

                    // Try to atomically replace existing with our instance.
                    if (_instanceCache.TryUpdate(handleKey, instanceObj, existing))
                    {
                        // We succeeded in replacing: schedule previous for disposal (unique set).
                        if (existing is not null)
                        {
                            _ = collectSet.Add(existing);
                        }
                        return;
                    }

                    // Another thread changed the value; retry.
                    continue;
                }
                // No existing value; try to add.
                this.THROW_IF_CACHE_LIMIT_REACHED(
                    _instanceCache.Count,
                    MaxCachedInstances,
                    nameof(_instanceCache));

                if (_instanceCache.TryAdd(handleKey, instanceObj))
                {
                    return;
                }

                // Add failed due to race; retry loop.
            }
        }

        void SAFE_DISPOSE_PREVIOUS(object previous, string context)
        {
            if (previous is not IDisposable prevDisp)
            {
                return;
            }

            // Remove from disposables tracking before calling Dispose to avoid double-dispose later.
            _ = _disposables.TryRemove(prevDisp, out _);

            try
            {
                prevDisp.Dispose();
                this.Emit(DiagnosticsEvents.Injection.Registered, "FW.InstanceManager:Register", $"disposed context={context}");
            }
            catch (ObjectDisposedException)
            {
                // Previously disposed: benign. Log as Trace to reduce noise.
                this.Emit(DiagnosticsEvents.Injection.Registered, "FW.InstanceManager:Register", $"disposed-already context={context}");
            }
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
            {
                // Unexpected disposal error: keep Error level.
                this.Emit(DiagnosticsEvents.Injection.Failure, "FW.InstanceManager:Register", $"dispose-failed context={context}", ex);
            }
        }
    }

    /// <summary>
    /// Registers an instance of the specified type in the instance cache,
    /// but only for the concrete class type (ignores interfaces).
    /// </summary>
    /// <typeparam name="T">The type of the instance to register.</typeparam>
    /// <param name="instance">The instance to register.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public void RegisterForClassOnly<T>(T instance) where T : class => this.Register(instance);

    /// <summary>
    /// Gets or creates an instance of the specified type with high performance.
    /// </summary>
    /// <typeparam name="T">The type of instance to get or create.</typeparam>
    /// <param name="args">The arguments to pass to the constructor if a new instance is created.</param>
    /// <returns>The existing or newly created instance.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the manager has already been disposed.</exception>
    /// <exception cref="InternalErrorException">Thrown when the requested instance cannot be created.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    [SuppressMessage("Style", "IDE0301:Simplify collection initialization", Justification = "<Pending>")]
    public T GetOrCreateInstance<T>([MaybeNull] params object?[] args) where T : class
    {
        ObjectDisposedException.ThrowIf(Interlocked.CompareExchange(ref _isDisposed, 0, 0) != 0, nameof(InstanceManager));

        args ??= Array.Empty<object?>();

        // Fast-path checking cache directly when no constructor arguments are provided.
        if (args.Length == 0)
        {
            RuntimeTypeHandle key = typeof(T).TypeHandle;
            if (_instanceCache.TryGetValue(key, out object? existing))
            {
                _ = Interlocked.Increment(ref _instanceCacheHitCount);
                return Unsafe.As<T>(existing);
            }

            // Cached instances stay readable after Lockdown, but creating new ones is not permitted.
            if (Interlocked.CompareExchange(ref _isLocked, 0, 0) != 0)
            {
                throw new InvalidOperationException("InstanceManager is locked. Dynamic instance creation is not permitted.");
            }

            _ = Interlocked.Increment(ref _instanceCreationCount);
            return Unsafe.As<T>(this.GET_OR_CREATE_INSTANCE_SLOW(typeof(T), args));
        }
        // Use signature cache for generic type when args provided.
        object obj = this.GetOrCreateInstance(typeof(T), args);

        return Unsafe.As<T>(obj);
    }

    /// <summary>
    /// Gets or creates an instance of the specified type with optimized constructor caching.
    /// </summary>
    /// <param name="type">The type of the instance to get or create.</param>
    /// <param name="args">The arguments to pass to the constructor if a new instance is created.</param>
    /// <returns>The existing or newly created instance.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown if the specified type does not have a suitable constructor or
    /// if the instance manager has been disposed.
    /// </exception>
    /// <exception cref="ObjectDisposedException">Thrown when the manager has already been disposed.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="type"/> is null.</exception>
    /// <exception cref="InternalErrorException">Thrown when instance creation fails after constructor resolution.</exception>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public object GetOrCreateInstance(Type type, [MaybeNull] params object?[] args)
    {
        if (Interlocked.CompareExchange(ref _isLocked, 0, 0) != 0)
        {
            throw new InvalidOperationException("InstanceManager is locked. Dynamic instance creation is not permitted.");
        }

        ObjectDisposedException.ThrowIf(Interlocked.CompareExchange(ref _isDisposed, 0, 0) != 0, nameof(InstanceManager));

        ArgumentNullException.ThrowIfNull(type, nameof(type));

        args ??= [];

        // If no args provided, preserve existing behavior: cache by Type handle.
        if (args.Length == 0)
        {
            RuntimeTypeHandle key = type.TypeHandle;
            if (_instanceCache.TryGetValue(key, out object? existing))
            {
                return existing;
            }

            return this.GET_OR_CREATE_INSTANCE_SLOW(type, args);
        }

        // For args (signature) use signature cache keyed by ActivatorKey.
        ActivatorKey sigKey = new(type, args);

        if (_signatureInstanceCache.TryGetValue(sigKey, out object? sigExisting))
        {
            return sigExisting;
        }

        this.THROW_IF_CACHE_LIMIT_REACHED(
            _signatureInstanceCache.Count,
            MaxSignatureInstances,
            nameof(_signatureInstanceCache));

        // Create then insert into signature cache (avoid losing created instance or double-dispose)
        return this.CREATE_OR_GET_SIGNATURE_INSTANCE(type, args, sigKey);
    }

    /// <summary>
    /// Creates a new instance without caching it.
    /// </summary>
    /// <param name="type">The type of instance to create.</param>
    /// <param name="args">Constructor arguments.</param>
    /// <returns>A new instance of the specified type.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the manager has already been disposed.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="type"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when no suitable constructor can be resolved.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public object CreateInstance(Type type, [MaybeNull] params object?[] args)
    {
        ObjectDisposedException.ThrowIf(Interlocked.CompareExchange(ref _isDisposed, 0, 0) != 0, nameof(InstanceManager));
        ArgumentNullException.ThrowIfNull(type, nameof(type));

        return this.CREATE_VIA_ACTIVATOR(type, args);
    }

    /// <summary>
    /// Removes the instance of the specified type from the cache.
    /// </summary>
    /// <param name="type">The type of the instance to remove.</param>
    /// <returns><c>true</c> if the instance was successfully removed; otherwise, <c>false</c>.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the manager has already been disposed.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="type"/> is null.</exception>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public bool RemoveInstance(Type type)
    {
        ObjectDisposedException.ThrowIf(Interlocked.CompareExchange(ref _isDisposed, 0, 0) != 0, nameof(InstanceManager));
        this.THROW_IF_LOCKED();

        ArgumentNullException.ThrowIfNull(type, nameof(type));

        RuntimeTypeHandle key = type.TypeHandle;
        bool removedAny = false;

        // Serialize with Register/Clear so the "still-referenced then dispose" checks below are atomic.
        lock (_mutationLock)
        {
            // Remove the type-keyed instance (if any)
            if (_instanceCache.TryRemove(key, out object? instance))
            {
                removedAny = true;

                // Remove mapped interface entries from _instanceCache if any exist
                if (s_preRegisteredServiceMappings.TryGetValue(type, out System.Collections.Generic.List<Type>? serviceTypes))
                {
                    lock (serviceTypes)
                    {
                        for (int i = 0; i < serviceTypes.Count; i++)
                        {
                            _ = _instanceCache.TryRemove(serviceTypes[i].TypeHandle, out _);
                        }
                    }
                }

                if (instance is IDisposable d && !_instanceCache.Values.Contains(d))
                {
                    _ = _disposables.TryRemove(d, out _);
                    try { d.Dispose(); }
                    catch (ObjectDisposedException)
                    {
                        this.Emit(DiagnosticsEvents.Injection.Registered, "FW.InstanceManager:RemoveInstance", $"disposed-already type={type.Name}");
                    }
                    catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
                    {
                        this.Emit(DiagnosticsEvents.Injection.Failure, "FW.InstanceManager:RemoveInstance", $"dispose-failed type={type.Name}", ex);
                    }

                    this.Emit(DiagnosticsEvents.Injection.Registered, "FW.InstanceManager:RemoveInstance", $"disposed type={type.Name}");
                }

                this.Emit(DiagnosticsEvents.Injection.Registered, "FW.InstanceManager:RemoveInstance", $"removed type={type.Name}");
            }

            // Also remove any signature instances whose target type matches
            List<ActivatorKey> sigKeys = [];
            foreach (ActivatorKey k in _signatureInstanceCache.Keys)
            {
                if (k.Target.Equals(key))
                {
                    sigKeys.Add(k);
                }
            }

            foreach (ActivatorKey sk in sigKeys)
            {
                if (_signatureInstanceCache.TryRemove(sk, out object? sinst))
                {
                    removedAny = true;
                    if (sinst is IDisposable sd && !_instanceCache.Values.Contains(sd) && !_signatureInstanceCache.Values.Contains(sinst))
                    {
                        _ = _disposables.TryRemove(sd, out _);
                        try { sd.Dispose(); }
                        catch (ObjectDisposedException)
                        {
                            this.Emit(DiagnosticsEvents.Injection.Registered, "FW.InstanceManager:RemoveInstance", $"disposed-already type={type.Name}");
                        }
                        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
                        {
                            this.Emit(DiagnosticsEvents.Injection.Failure, "FW.InstanceManager:RemoveInstance", $"dispose-failed type={type.Name}", ex);
                        }
                    }
                }
            }

            // Invalidate L1 thread-static slots after the cache entries are gone so readers cannot
            // keep serving the removed instance (mirrors Register/ClearInternal).
            _ = Interlocked.Increment(ref s_slotsInvalidated);
        } // _mutationLock

        if (!removedAny)
        {
            this.Emit(DiagnosticsEvents.Injection.Registered, "FW.InstanceManager:RemoveInstance", $"not-found type={type.Name}");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Determines whether an instance of the specified type is cached.
    /// </summary>
    /// <typeparam name="T">The type to check.</typeparam>
    /// <returns><c>true</c> if an instance of the specified type is cached; otherwise, <c>false</c>.</returns>
    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public bool HasInstance<T>() => _instanceCache.ContainsKey(typeof(T).TypeHandle);

    /// <summary>
    /// Gets an existing instance of the specified type without creating a new one if it doesn't exist.
    /// </summary>
    /// <typeparam name="T">The type of the instance to get.</typeparam>
    /// <returns>The existing instance, or <c>null</c> if no instance exists.</returns>
    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    [return: MaybeNull]
    public T? GetExistingInstance<T>() where T : class
    {
        ObjectDisposedException.ThrowIf(Interlocked.CompareExchange(ref _isDisposed, 0, 0) != 0, nameof(InstanceManager));

        // 1) Thread L1 (2-slot MRU with isolation)
        int globalInvalidated = Volatile.Read(ref s_slotsInvalidated);
        RuntimeTypeHandle key = typeof(T).TypeHandle;

        if (s_tsSlotsInvalidated != globalInvalidated)
        {
            s_tsSlotsInvalidated = globalInvalidated;
            s_tsKey0 = default; s_tsVal0 = null; s_tsMgr0 = null;
            s_tsKey1 = default; s_tsVal1 = null; s_tsMgr1 = null;
        }
        else
        {
            if (ReferenceEquals(s_tsMgr0, this) && s_tsKey0.Equals(key))
            {
                return (T?)s_tsVal0;
            }
            if (ReferenceEquals(s_tsMgr1, this) && s_tsKey1.Equals(key))
            {
                // Swap to Slot 0 (MRU)
                object? v = s_tsVal1;
                s_tsKey1 = s_tsKey0; s_tsVal1 = s_tsVal0; s_tsMgr1 = s_tsMgr0;
                s_tsKey0 = key; s_tsVal0 = v; s_tsMgr0 = this;
                return (T?)v;
            }
        }

        // 2) Dictionary fallback
        if (!_instanceCache.TryGetValue(key, out object? instance))
        {
            return null;
        }

        _ = Interlocked.Increment(ref _instanceCacheHitCount);

        // Publish to L1 (Slot 0)
        s_tsKey1 = s_tsKey0; s_tsVal1 = s_tsVal0; s_tsMgr1 = s_tsMgr0;
        s_tsKey0 = key; s_tsVal0 = instance; s_tsMgr0 = this;

        return (T)instance;
    }

    /// <summary>
    /// Clears all cached instances, optionally disposing them.
    /// </summary>
    /// <param name="dispose">If <c>true</c>, disposes any instances that implement <see cref="IDisposable"/>.</param>
    /// <exception cref="ObjectDisposedException">Thrown when the manager has already been disposed.</exception>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Clear(bool dispose = true)
    {
        ObjectDisposedException.ThrowIf(Interlocked.CompareExchange(ref _isDisposed, 0, 0) != 0, nameof(InstanceManager));
        this.THROW_IF_LOCKED();
        this.ClearInternal(dispose);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void ClearInternal(bool dispose)
    {
        lock (_mutationLock)
        {
            if (dispose)
            {
                // Snapshot keys to avoid modifying collection during enumeration.
                foreach (IDisposable? d in (IDisposable[])[.. _disposables.Keys])
                {
                    try
                    {
                        // Try to remove from tracking first to avoid double-dispose later.
                        _ = _disposables.TryRemove(d, out _);
                        d.Dispose();
                        this.Emit(DiagnosticsEvents.Injection.Registered, "FW.InstanceManager:Clear", "disposed");
                    }
                    catch (ObjectDisposedException)
                    {
                        this.Emit(DiagnosticsEvents.Injection.Registered, "FW.InstanceManager:Clear", "disposed-already");
                    }
                    catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
                    {
                        this.Emit(DiagnosticsEvents.Injection.Failure, "FW.InstanceManager:Clear", "dispose-failed", ex);
                    }
                }
            }

            _instanceCache.Clear();
            _signatureInstanceCache.Clear();
            _activatorCache.Clear();
            _disposables.Clear();

            // Invalidate L1 thread-static caches (no need to enumerate)
            _ = Interlocked.Increment(ref s_slotsInvalidated);

            // Optional: clear thread L1 (best-effort for current thread)
            s_tsKey0 = default; s_tsVal0 = null; s_tsMgr0 = null;
            s_tsKey1 = default; s_tsVal1 = null; s_tsMgr1 = null;
        } // _mutationLock

        this.Emit(DiagnosticsEvents.Injection.Registered, "FW.InstanceManager:Clear", "cleared");
    }

    #endregion Public API

    #region IDisposable

    /// <summary>
    /// Disposes of all instances in the cache that implement <see cref="IDisposable"/>.
    /// </summary>
    protected override void DisposeManaged()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
        {
            return;
        }

        // Snapshot keys to avoid modifying collection while disposing.
        foreach (IDisposable? d in (IDisposable[])[.. _disposables.Keys])
        {
            try
            {
                // Remove from tracking to avoid double-dispose later.
                _ = _disposables.TryRemove(d, out _);
                d.Dispose();
                this.Emit(DiagnosticsEvents.Injection.Registered, "FW.InstanceManager:DisposeManaged", "disposed");
            }
            catch (ObjectDisposedException)
            {
                this.Emit(DiagnosticsEvents.Injection.Registered, "FW.InstanceManager:DisposeManaged", "disposed-already");
            }
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
            {
                this.Emit(DiagnosticsEvents.Injection.Failure, "FW.InstanceManager:DisposeManaged", "dispose-failed", ex);
            }
        }

        // Clear caches without disposing again.
        this.ClearInternal(dispose: false);

        this.Emit(DiagnosticsEvents.Injection.Registered, "FW.InstanceManager:DisposeManaged", "disposed");
    }

    #endregion IDisposable
}
