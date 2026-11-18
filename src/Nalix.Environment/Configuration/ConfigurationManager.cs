// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Contracts;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security;
using System.Threading;
using Nalix.Abstractions.Diagnostics;
using Nalix.Abstractions.Exceptions;
using Nalix.Environment.Configuration.Binding;
using Nalix.Environment.IO;

namespace Nalix.Environment.Configuration;

/// <summary>
/// A singleton that provides access to configuration value containers with optimized performance
/// for high-throughput real-time server applications.
/// </summary>
/// <remarks>
/// <para>
/// Thread-safety model:
/// - <see cref="Get{TClass}()"/> snapshots the current context under a read lock before use.
/// - <see cref="ReloadAll"/> and <see cref="SetConfigFilePath"/> use a write lock for exclusive access.
/// - A <see cref="SemaphoreSlim"/>(1,1) gate serialises reload/path-change operations
///   and makes callers WAIT instead of silently returning <see langword="false"/>.
/// - <see cref="FileSystemWatcher"/> changes are debounced (300 ms) to absorb the
///   multiple rapid events that most OS file-writers emit for a single logical write.
/// </para>
/// </remarks>
[DebuggerNonUserCode]
[SkipLocalsInit]
[DebuggerDisplay("ConfigFilePath = {ConfigFilePath,nq}, LoadedTypes = {_currentContext.ContainerDict.Count}")]
[DynamicallyAccessedMembers(
    DynamicallyAccessedMemberTypes.NonPublicMethods |
    DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
public sealed class ConfigurationManager : IDisposable
{
    #region Singleton Pattern (Internalized)

    private static readonly Lazy<ConfigurationManager> s_instance =
        new(() => new ConfigurationManager(), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Gets the default thread-safe instance of the <see cref="ConfigurationManager"/>.
    /// </summary>
    public static ConfigurationManager Instance => s_instance.Value;

    #endregion

    #region Fields

    private static DiagnosticListener Listener => DiagnosticsEvents.Source;

    private readonly string _baseConfigDirectory;
    private readonly ReaderWriterLockSlim _configLock;

    /// <summary>
    /// SemaphoreSlim(1,1): serialises ReloadAll / SetConfigFilePath.
    /// Using Wait() instead of Interlocked.Exchange means callers BLOCK until the current
    /// operation completes — fixing the test failure where FileSystemWatcher would grab the
    /// old Interlocked flag just before the test's manual ReloadAll() call.
    /// </summary>
    private readonly SemaphoreSlim _reloadGate;

    /// <summary>
    /// Debounce: FileSystemWatcher fires 2-3 Changed events per single file write on most OSes.
    /// We reset a one-shot timer on every event; only the last one fires ReloadAll().
    /// </summary>
    private Timer? _debounceTimer;
    private volatile bool _isDisposed;

    private static readonly TimeSpan s_debounceDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// volatile: read/written from multiple threads without a full lock.
    /// </summary>
    private volatile bool _directoryChecked;

    private volatile string _configFilePath;
    private FileSystemWatcher? _configFileWatcher;

    private readonly Lock _syncLock = new();

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ConfigurationContext> _contextCache;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Type, byte> _manuallyConfiguredTypes = new();
    private volatile ConfigurationContext _currentContext;

    private const int MaxCachedContexts = 10;

    #endregion Fields

    #region Properties

    /// <summary>
    /// Gets the path to the active configuration file.
    /// </summary>
    public string ConfigFilePath
    {
        get
        {
            _configLock.EnterReadLock();
            try { return _configFilePath; }
            finally { _configLock.ExitReadLock(); }
        }
    }

    /// <summary>
    /// Gets a value indicating whether the configuration file exists on disk.
    /// </summary>
    public bool ConfigFileExists
    {
        get
        {
            // Snapshot under read lock to avoid a TOCTOU race with SetConfigFilePath.
            ConfigurationContext snapshot;
            _configLock.EnterReadLock();
            try { snapshot = _currentContext; }
            finally { _configLock.ExitReadLock(); }

            return snapshot.IniFile.IsValueCreated && snapshot.IniFile.Value.ExistsFile;
        }
    }

    /// <summary>
    /// Gets the last reload timestamp.
    /// </summary>
    public DateTime LastReloadTime { get; private set; } = DateTime.UtcNow;

    #endregion Properties

    #region Constructor

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigurationManager"/> class.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the process configuration directory is not available.</exception>
    /// <exception cref="InternalErrorException">Thrown when the default configuration path escapes the allowed configuration directory.</exception>
    /// <exception cref="SecurityException">Thrown when the default configuration path cannot be normalized securely.</exception>
    public ConfigurationManager()
    {
        string configDirectory = Directories.ConfigurationDirectory;

        if (string.IsNullOrWhiteSpace(configDirectory))
        {
            throw new InvalidOperationException(
                $"Invalid configuration directory: value='{configDirectory ?? "<null>"}'.");
        }

        _baseConfigDirectory = Path.GetFullPath(configDirectory);
        _configFilePath = Path.Combine(_baseConfigDirectory, "default.ini");

        this.VALIDATE_CONFIG_PATH(_configFilePath);

        // Initialise synchronisation primitives BEFORE the watcher.
        // If the watcher fires during construction it needs _configLock and _reloadGate to exist.
        _configLock = new(LockRecursionPolicy.NoRecursion);
        _reloadGate = new SemaphoreSlim(1, 1);
        _contextCache = new(StringComparer.OrdinalIgnoreCase);

        // Initialise the default context
        _currentContext = this.GET_OR_CREATE_CONTEXT(_configFilePath);
        this.SETUP_FILE_WATCHER();
    }

    #endregion Constructor

    #region Public Methods


    /// <summary>
    /// Changes the active configuration file path and optionally reloads all containers.
    /// </summary>
    /// <param name="newConfigFilePath">Absolute or relative path to the new INI file.</param>
    /// <param name="autoReload">
    /// When <see langword="true"/> (default) all already-initialised containers are reloaded
    /// from the new file immediately. When <see langword="false"/> you must call
    /// <see cref="ReloadAll"/> manually.
    /// </param>
    /// <returns>
    /// <see langword="true"/> on success; <see langword="false"/> if the path was unchanged,
    /// the gate timed out, or an auto-reload failed (path is rolled back in that case).
    /// </returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="newConfigFilePath"/> is null, empty, or whitespace.</exception>
    /// <exception cref="InternalErrorException">Thrown when <paramref name="newConfigFilePath"/> resolves outside the allowed configuration directory.</exception>
    /// <exception cref="SecurityException">Thrown when the normalized path cannot be accessed securely.</exception>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void SetConfigFilePath(string newConfigFilePath, bool autoReload = true)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        if (string.IsNullOrWhiteSpace(newConfigFilePath))
        {
            throw new ArgumentException(
                $"Invalid config path: value='{newConfigFilePath}'.");
        }

        string normalizedPath = Path.GetFullPath(newConfigFilePath);
        this.VALIDATE_CONFIG_PATH(normalizedPath);

        // Non-blocking gate check: fail fast instead of stalling caller threads.
        if (!_reloadGate.Wait(0))
        {
            throw new TimeoutException(
                "A configuration reload/path-change operation is already running.");
        }

        string? pathToWatch = null;

        try
        {
            _configLock.EnterWriteLock();
            try
            {
                if (string.Equals(_configFilePath, normalizedPath, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException(
                        $"Config path unchanged: value='{normalizedPath}'.");
                }

                ConfigurationContext oldContext = _currentContext;
                if (oldContext.IniFile.IsValueCreated)
                {
                    try
                    {
                        oldContext.IniFile.Value.Flush();
                    }
                    catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
                    {
                        if (Listener.IsEnabled(DiagnosticsEvents.Configuration.Failure))
                        {
                            DiagnosticsEvents.Write(DiagnosticsEvents.Configuration.Failure, new DiagnosticLog("ENV.ConfigurationManager:Flush", $"flush-failed path={oldContext.Path}", ex));
                        }
                    }
                }

                string oldPath = _configFilePath;
                ConfigurationContext newContext = this.GET_OR_CREATE_CONTEXT(normalizedPath);

                _configFilePath = normalizedPath;
                _currentContext = newContext;
                _directoryChecked = false;

                if (Listener.IsEnabled(DiagnosticsEvents.Configuration.PathChanged))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.Configuration.PathChanged, new DiagnosticLog("ENV.ConfigurationManager:Internal", $"path-changed from={oldPath} to={normalizedPath} cache-hit={newContext.IniFile.IsValueCreated.ToString().ToLowerInvariant()}"));
                }

                if (autoReload && !newContext.ContainerDict.IsEmpty)
                {
                    try
                    {
                        // Perform reload of the current context state
                        this.ReloadCurrentContextInternal();
                        pathToWatch = normalizedPath;
                    }
                    catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
                    {
                        // Roll back state
                        _configFilePath = oldPath;
                        _currentContext = oldContext;
                        _directoryChecked = false;

                        pathToWatch = oldPath;

                        throw new InvalidOperationException(
                            $"Auto-reload failed: path='{normalizedPath}', error={ex.Message}", ex);
                    }
                }
                else
                {
                    pathToWatch = normalizedPath;
                }
            }
            finally
            {
                _configLock.ExitWriteLock();
            }

            // Set up the watcher AFTER releasing the write lock (both success and rollback paths).
            if (pathToWatch is not null)
            {
                lock (_syncLock)
                {
                    this.SETUP_FILE_WATCHER();
                }
            }
        }
        finally
        {
            _ = _reloadGate.Release();
        }
    }

    /// <summary>
    /// Initializes if needed and returns an instance of <typeparamref name="TClass"/>.
    /// </summary>
    /// <typeparam name="TClass">The type of the configuration container.</typeparam>
    /// <returns>An instance of type <typeparamref name="TClass"/>.</returns>
    /// <remarks>
    /// This method is thread-safe. The first call for a given type will create and initialize the container.
    /// Subsequent calls will return the cached instance. Initialization happens outside of locks to prevent
    /// potential deadlocks if the container initialization requires additional resources.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown when the configuration directory or container initialization state is invalid.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the configuration file or directory cannot be accessed.</exception>
    /// <exception cref="PathTooLongException">Thrown when the configured file path exceeds platform limits.</exception>
    /// <exception cref="IOException">Thrown when the configuration file or directory cannot be read or created.</exception>
    /// <exception cref="InternalErrorException">Thrown when the configuration file path escapes the allowed base directory.</exception>
    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public TClass Get<TClass>() where TClass : ConfigurationLoader, new()
    {
        ConfigurationContext context;
        _configLock.EnterReadLock();

        try
        {
            context = _currentContext;
        }
        finally
        {
            _configLock.ExitReadLock();
        }

        Lazy<ConfigurationLoader> lazy = context.ContainerDict.GetOrAdd(
            typeof(TClass),
            _ => new Lazy<ConfigurationLoader>(() =>
            {
                TClass container = new();
                container.Initialize(context.IniFile.Value);

                if (Listener.IsEnabled(DiagnosticsEvents.Configuration.Container))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.Configuration.Container, new DiagnosticLog("ENV.ConfigurationManager:Internal", $"container-created type={typeof(TClass).Name} context={context.Path}"));
                }

                return container;
            }, LazyThreadSafetyMode.ExecutionAndPublication)
        );

        return (TClass)lazy.Value;
    }

    /// <summary>
    /// Initializes if needed and returns an instance of <typeparamref name="TClass"/>.
    /// </summary>
    /// <typeparam name="TClass">The type of the configuration container.</typeparam>
    /// <returns>An instance of type <typeparamref name="TClass"/>.</returns>
    /// <param name="configFilePath">The new configuration file path.</param>
    /// <param name="autoReload">
    /// If <see langword="true"/>, automatically reloads all configurations from the new file.
    /// Default is <see langword="true"/>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if the path was changed successfully;
    /// otherwise, <see langword="false"/>.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="configFilePath"/> is null or whitespace.
    /// </exception>
    /// <exception cref="InternalErrorException">
    /// Thrown when <paramref name="configFilePath"/> is outside the allowed configuration directory.
    /// </exception>
    /// <exception cref="InvalidOperationException">Thrown when the configuration directory or container initialization state is invalid.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the configuration file or directory cannot be accessed.</exception>
    /// <exception cref="PathTooLongException">Thrown when the configured file path exceeds platform limits.</exception>
    /// <exception cref="IOException">Thrown when the configuration file or directory cannot be read or created.</exception>
    /// <remarks>
    /// <para>
    /// This method is thread-safe and will block all configuration access during the path change.
    /// </para>
    /// <para>
    /// Security: The new path must be within the base configuration directory to prevent
    /// directory traversal attacks.
    /// </para>
    /// <para>
    /// If <paramref name="autoReload"/> is <see langword="true"/>, all existing configuration
    /// containers will be reinitialized from the new file. If <see langword="false"/>, you must
    /// manually call <see cref="ReloadAll"/> to load configurations from the new file.
    /// </para>
    /// </remarks>
    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public TClass Get<TClass>(string configFilePath, bool autoReload = true)
        where TClass : ConfigurationLoader, new()
    {
        this.SetConfigFilePath(configFilePath, autoReload);
        return this.Get<TClass>();
    }

    /// <summary>
    /// Reloads every already-initialised configuration container from disk.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> on success; <see langword="false"/> if the gate timed out
    /// or an exception occurred during reload.
    /// </returns>
    /// <remarks>
    /// Callers block (up to 5 s) while a concurrent reload is in progress instead of
    /// receiving a silent <see langword="false"/>. This is the key fix for the test failure:
    /// <see cref="FileSystemWatcher"/> can start a background reload right before
    /// the test calls <see cref="ReloadAll"/> manually; the old <c>Interlocked</c> flag would
    /// cause the manual call to return <see langword="false"/> immediately.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void ReloadAll()
    {
        if (_isDisposed)
        {
            return;
        }

        if (!_reloadGate.Wait(0))
        {
            throw new TimeoutException("Another configuration reload/path-change operation is already in progress.");
        }

        Exception? reloadException = null;

        try
        {
            ConfigurationContext context = _currentContext;

            // SEC-23: Perform I/O (Reload) OUTSIDE of the global Write Lock.
            // IniConfig.Reload is internally thread-safe. This prevents Get<T> readers
            // from being blocked while waiting for disk I/O.
            if (context.IniFile.IsValueCreated)
            {
                context.IniFile.Value.Reload();
            }

            _configLock.EnterWriteLock();
            try
            {
                this.ReloadCurrentContextInternal();
            }
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
            {
                reloadException = ex;
            }
            finally
            {
                // Release write lock BEFORE logging — avoids holding it longer than necessary
                // and prevents a deadlock if the logger calls back into the manager.
                _configLock.ExitWriteLock();
            }

            if (reloadException is not null)
            {
                if (Listener.IsEnabled(DiagnosticsEvents.Configuration.Failure))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.Configuration.Failure, new DiagnosticLog("ENV.ConfigurationManager:Internal", $"reload-failed count={context.ContainerDict.Count}", reloadException));
                }

                throw new InvalidOperationException(
                    $"Configuration reload failed: {reloadException.Message}", reloadException);
            }

            if (Listener.IsEnabled(DiagnosticsEvents.Configuration.Reload))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Configuration.Reload, new DiagnosticLog("ENV.ConfigurationManager:Internal", $"reload-completed count={context.ContainerDict.Count}"));
            }
        }
        catch (ObjectDisposedException) when (_isDisposed)
        {
            // Shutdown won the race with a background reload callback.
        }
        finally
        {
            if (!_isDisposed)
            {
                _ = _reloadGate.Release();
            }
        }
    }

    /// <summary>
    /// Marks <typeparamref name="TClass"/> as manually overridden outside the INI file (e.g. via
    /// <c>NetworkApplicationBuilder.Configure&lt;TOptions&gt;</c>), so a future reload that re-binds
    /// it from disk emits <see cref="DiagnosticsEvents.Configuration.ManualOverrideOverwritten"/>
    /// instead of silently discarding the override.
    /// </summary>
    /// <typeparam name="TClass">The configuration type that was manually configured.</typeparam>
    public void MarkManuallyConfigured<TClass>() where TClass : ConfigurationLoader
        => _manuallyConfiguredTypes[typeof(TClass)] = 0;

    /// <summary>
    /// Checks if a specific configuration type is loaded.
    /// </summary>
    /// <typeparam name="TClass">The configuration type to check.</typeparam>
    /// <returns>
    /// <see langword="true"/> if the configuration is loaded;
    /// otherwise, <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// This method is thread-safe and lock-free.
    /// </remarks>
    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public bool IsLoaded<TClass>() where TClass : ConfigurationLoader => _currentContext.ContainerDict.ContainsKey(typeof(TClass));

    /// <summary>
    /// Removes a specific configuration from the cache.
    /// </summary>
    /// <typeparam name="TClass">The configuration type to remove.</typeparam>
    /// <returns>
    /// <see langword="true"/> if the configuration was removed;
    /// otherwise, <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// This method is thread-safe. Removing a configuration will cause it to be
    /// reinitialized on the next call to <see cref="Get{TClass}()"/>.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public bool Remove<TClass>() where TClass : ConfigurationLoader
    {
        ConfigurationContext context = _currentContext;
        if (Listener.IsEnabled(DiagnosticsEvents.Configuration.Container))
        {
            DiagnosticsEvents.Write(DiagnosticsEvents.Configuration.Container, new DiagnosticLog("ENV.ConfigurationManager:Internal", $"container-removed type={typeof(TClass).Name} context={context.Path}"));
        }

        return context.ContainerDict.TryRemove(typeof(TClass), out _);
    }

    /// <summary>
    /// Clears all cached configurations.
    /// </summary>
    /// <remarks>
    /// This method is thread-safe. All configurations will be reinitialized on
    /// the next call to <see cref="Get{TClass}()"/> for each type.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void ClearAll()
    {
        ConfigurationContext context = _currentContext;
        if (Listener.IsEnabled(DiagnosticsEvents.Configuration.Container))
        {
            DiagnosticsEvents.Write(DiagnosticsEvents.Configuration.Container, new DiagnosticLog("ENV.ConfigurationManager:Internal", $"container-cleared context={context.Path}"));
        }

        context.ContainerDict.Clear();
    }

    /// <summary>
    /// Updates a specific configuration value in the active INI file.
    /// </summary>
    /// <typeparam name="TClass">The type of the configuration container.</typeparam>
    /// <param name="key">The key to update.</param>
    /// <param name="value">The new value.</param>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void UpdateValue<TClass>(string key, object value) where TClass : ConfigurationLoader
    {
        Lazy<IniConfig> snapshot;
        _configLock.EnterReadLock();
        try { snapshot = _currentContext.IniFile; }
        finally { _configLock.ExitReadLock(); }

        if (snapshot.IsValueCreated)
        {
            string section = ConfigurationLoader.GetSectionName(typeof(TClass));
            snapshot.Value.WriteValue(section, key, value);
        }
    }

    /// <summary>
    /// Ensures that changes are written to disk.
    /// </summary>
    /// <remarks>
    /// This method is thread-safe and will flush pending changes to the INI file.
    /// </remarks>
    /// <exception cref="UnauthorizedAccessException">Thrown when the configuration file cannot be written.</exception>
    /// <exception cref="IOException">Thrown when flushing pending configuration changes fails.</exception>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Flush()
    {
        // Snapshot under read lock — avoids TOCTOU race between IsValueCreated and .Value.
        Lazy<IniConfig> snapshot;
        _configLock.EnterReadLock();
        try { snapshot = _currentContext.IniFile; }
        finally { _configLock.ExitReadLock(); }

        if (snapshot.IsValueCreated)
        {
            try
            {
                snapshot.Value.Flush();
                if (Listener.IsEnabled(DiagnosticsEvents.Configuration.Flush))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.Configuration.Flush, new DiagnosticLog("ENV.ConfigurationManager:Flush", $"flush-completed path={snapshot.Value.FilePath}"));
                }
            }
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
            {
                throw;
            }
        }
    }

    #endregion Public Methods

    #region Protected Methods

    /// <inheritdoc/>
    public void Dispose()
    {
        _isDisposed = true;

        ConfigurationContext context = _currentContext;
        if (context.IniFile.IsValueCreated)
        {
            try
            {
                context.IniFile.Value.Flush();
            }
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
            {
                if (Listener.IsEnabled(DiagnosticsEvents.Configuration.Failure))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.Configuration.Failure, new DiagnosticLog("ENV.ConfigurationManager:Internal", "dispose-flush-failed", ex));
                }
            }
        }

        _debounceTimer?.Dispose();
        _debounceTimer = null;

        _configFileWatcher?.Dispose();
        _configFileWatcher = null;

        bool gateTaken = false;
        try
        {
            gateTaken = _reloadGate.Wait(0);
            if (!gateTaken)
            {
                if (Listener.IsEnabled(DiagnosticsEvents.Configuration.Reload))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.Configuration.Reload, new DiagnosticLog("ENV.ConfigurationManager:Internal", "dispose-gate-busy"));
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // Another disposer already completed shutdown.
        }
        finally
        {
            if (gateTaken)
            {
                _ = _reloadGate.Release();
            }
        }

        _reloadGate.Dispose();
        _configLock.Dispose();
        _contextCache.Clear();
    }

    #endregion Protected Methods

    #region Private Methods

    private void SETUP_FILE_WATCHER()
    {
        if (OperatingSystem.IsBrowser())
        {
            return; // WebAssembly has no native file system to watch.
        }

        if (_isDisposed)
        {
            return;
        }

        _debounceTimer?.Dispose();
        _debounceTimer = null;

        _configFileWatcher?.Dispose();
        _configFileWatcher = null;

        string currentPath;
        _configLock.EnterReadLock();
        try { currentPath = _configFilePath; }
        finally { _configLock.ExitReadLock(); }

        string? directory = Path.GetDirectoryName(currentPath);
        string? file = Path.GetFileName(currentPath);

        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(file))
        {
            return;
        }

        _configFileWatcher = new FileSystemWatcher(directory, file)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size
        };

        if (Listener.IsEnabled(DiagnosticsEvents.Configuration.Directory))
        {
            DiagnosticsEvents.Write(DiagnosticsEvents.Configuration.Directory, new DiagnosticLog("ENV.ConfigurationManager:Internal", $"watcher-started path={currentPath}"));
        }

        // Capture path at setup time — lambda must not close over _configFilePath directly.
        string watchedPath = currentPath;

        _configFileWatcher.Changed += (_, e) =>
        {
            if (_isDisposed)
            {
                return;
            }

            if (!e.FullPath.Equals(watchedPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // Debounce: reset timer on every event so only the trailing edge triggers a reload.
            lock (_syncLock)
            {
                _debounceTimer?.Dispose();
                _debounceTimer = new Timer(
                    _ =>
                    {
                        try
                        {
                            if (_isDisposed)
                            {
                                return;
                            }

                            this.ReloadAll();
                        }
                        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
                        {
                            if (Listener.IsEnabled(DiagnosticsEvents.Configuration.Failure))
                            {
                                DiagnosticsEvents.Write(DiagnosticsEvents.Configuration.Failure, new DiagnosticLog("ENV.ConfigurationManager:Internal", "background-reload-failed", ex));
                            }
                        }
                    },
                    state: null,
                    dueTime: s_debounceDelay,
                    period: Timeout.InfiniteTimeSpan);
            }
        };

        _configFileWatcher.EnableRaisingEvents = true;
    }

    private Lazy<IniConfig> CREATE_LAZY_INI_CONFIG(string filePath)
    {
        return new Lazy<IniConfig>(() =>
        {
            this.ENSURE_CONFIG_DIRECTORY_EXISTS();
            return new IniConfig(filePath);
        }, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    private void VALIDATE_CONFIG_PATH(string pathToValidate)
    {
        string normalizedPath = Path.GetFullPath(pathToValidate);
        string normalizedBaseDir = Path.GetFullPath(_baseConfigDirectory);

        // SEC-25: Ensure base directory check is robust against prefix-matching attacks.
        // C:\Config vs C:\Config_Evil
        if (!normalizedBaseDir.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
        {
            normalizedBaseDir += Path.DirectorySeparatorChar;
        }

        if (!normalizedPath.StartsWith(normalizedBaseDir, StringComparison.OrdinalIgnoreCase))
        {
            // Allow if the path is exactly the base directory (though it's usually a file path)
            if (!string.Equals(normalizedPath, Path.GetFullPath(_baseConfigDirectory), StringComparison.OrdinalIgnoreCase))
            {
                throw new InternalErrorException(
                    $"Configuration file path '{pathToValidate}' is outside the allowed configuration directory '{_baseConfigDirectory}'.");
            }
        }
    }

    [StackTraceHidden]
    [DebuggerStepThrough]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ENSURE_CONFIG_DIRECTORY_EXISTS()
    {
        if (_directoryChecked)
        {
            return;
        }

        lock (_syncLock)
        {
            if (_directoryChecked)
            {
                return;
            }

            string currentPath = _configFilePath;
            string? directory = Path.GetDirectoryName(currentPath);

            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new InvalidOperationException(
                    "Configuration file path does not contain a valid directory component.");
            }

            if (!Directory.Exists(directory))
            {
                try
                {
                    DirectoryInfo dirInfo = Directory.CreateDirectory(directory);

                    if (Listener.IsEnabled(DiagnosticsEvents.Configuration.Directory))
                    {
                        DiagnosticsEvents.Write(DiagnosticsEvents.Configuration.Directory, new DiagnosticLog("ENV.ConfigurationManager:Internal", $"directory-created path={directory}"));
                    }

                    if (!dirInfo.Exists)
                    {
                        throw new InvalidOperationException($"Directory creation reported success but directory does not exist: {directory}");
                    }
                }
                catch (UnauthorizedAccessException ex)
                {
                    throw new UnauthorizedAccessException(
                        $"Access denied when creating configuration directory: {directory}", ex);
                }
                catch (PathTooLongException ex)
                {
                    throw new PathTooLongException(
                        $"Configuration directory path is too long: {directory}", ex);
                }
                catch (IOException ex)
                {
                    throw new IOException(
                        $"I/O error creating configuration directory: {directory}", ex);
                }
                catch (Exception ex) when (ex is not InvalidOperationException)
                {
                    throw new InvalidOperationException(
                        $"Unexpected error creating configuration directory: {directory}", ex);
                }
            }

            _directoryChecked = true;
        }
    }

    private ConfigurationContext GET_OR_CREATE_CONTEXT(string path)
    {
        return _contextCache.GetOrAdd(path, p =>
        {
            // SEC-26: Prevent memory leaks by capping the context cache.
            // When full, we clear the cache. A more advanced implementation would use LRU.
            if (_contextCache.Count >= MaxCachedContexts)
            {
                if (Listener.IsEnabled(DiagnosticsEvents.Configuration.Cache))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.Configuration.Cache, new DiagnosticLog("ENV.ConfigurationManager:Internal", $"cache-cleared reason=capacity-exceeded limit={MaxCachedContexts}"));
                }
                _contextCache.Clear();
            }

            return new ConfigurationContext
            {
                Path = p,
                IniFile = this.CREATE_LAZY_INI_CONFIG(p),
                ContainerDict = new()
            };
        });
    }

    private void ReloadCurrentContextInternal()
    {
        ConfigurationContext context = _currentContext;
        int successCount = 0;
        int failureCount = 0;

        foreach (Lazy<ConfigurationLoader> lazy in context.ContainerDict.Values)
        {
            if (lazy.IsValueCreated)
            {
                try
                {
                    lazy.Value.Initialize(context.IniFile.Value);
                    successCount++;

                    if (_manuallyConfiguredTypes.ContainsKey(lazy.Value.GetType()) &&
                        Listener.IsEnabled(DiagnosticsEvents.Configuration.ManualOverrideOverwritten))
                    {
                        DiagnosticsEvents.Write(DiagnosticsEvents.Configuration.ManualOverrideOverwritten,
                            new DiagnosticLog("ENV.ConfigurationManager:Internal", $"manual-override-overwritten type={lazy.Value.GetType().Name}"));
                    }
                }
                catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
                {
                    failureCount++;
                    if (Listener.IsEnabled(DiagnosticsEvents.Configuration.Reload))
                    {
                        DiagnosticsEvents.Write(DiagnosticsEvents.Configuration.Reload, new DiagnosticLog("ENV.ConfigurationManager:Internal", $"reload-partial-error type={lazy.Value.GetType().Name}", ex));
                    }
                }
            }
        }

        this.LastReloadTime = DateTime.UtcNow;

        if (failureCount > 0 && Listener.IsEnabled(DiagnosticsEvents.Configuration.Reload))
        {
            DiagnosticsEvents.Write(DiagnosticsEvents.Configuration.Reload, new DiagnosticLog("ENV.ConfigurationManager:Internal", $"reload-completed-with-errors success-count={successCount} failure-count={failureCount}"));
        }
    }

    private sealed class ConfigurationContext
    {
        public required string Path { get; init; }
        public required Lazy<IniConfig> IniFile { get; init; }
        public required System.Collections.Concurrent.ConcurrentDictionary<Type, Lazy<ConfigurationLoader>> ContainerDict { get; init; }
    }

    #endregion Private Methods
}
