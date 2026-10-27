// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Threading;
using Nalix.Abstractions.Exceptions;

namespace Nalix.Environment.IO;

/// <summary>
/// Provides application-wide directories with environment-aware defaults,
/// safe creation, and optional environment variable overrides.
/// </summary>
[DebuggerNonUserCode]
[ExcludeFromCodeCoverage]
[UnsupportedOSPlatform("browser")]
[SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "<Pending>")]
public static partial class Directories
{
    #region Private Properties

    // ---------- Locks & Events ----------

    /// <summary>Global lock for thread-safe directory creation.</summary>
    private static readonly ReaderWriterLockSlim s_directoryLock = new(LockRecursionPolicy.SupportsRecursion);

    /// <summary>
    /// Lock object for synchronizing access to the handlers list.
    /// </summary>
    private static readonly System.Threading.Lock s_handlerLock = new();

    // ---------- Configuration ----------

    /// <summary>Optional base path override (intended for tests).</summary>
    private static string? s_basePathOverride;

    /// <summary>
    /// Returns an environment variable value or <c>null</c> if empty/whitespace.
    /// </summary>
    /// <param name="name">Environment variable name.</param>
    /// <returns>Value or <c>null</c>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    [return: MaybeNull]
    private static string GET_ENV([DisallowNull] string name)
    {
        string? value = System.Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    // ---------- Environment/Platform Detection ----------

    /// <summary>Whether the process is running inside a container (Docker/Kubernetes).</summary>
    private static readonly Lazy<bool> IsContainerLazy = new(() =>
    {
        try
        {
            string? dotnetInContainer = System.Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER");
            if (string.Equals(dotnetInContainer, "true", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string? kubeHost = System.Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST");
            if (!string.IsNullOrEmpty(kubeHost))
            {
                return true;
            }

            if (File.Exists("/.dockerenv"))
            {
                return true;
            }

            if (File.Exists("/proc/1/cgroup"))
            {
                string cg = File.ReadAllText("/proc/1/cgroup");
                if (cg.Contains("docker", StringComparison.OrdinalIgnoreCase) ||
                    cg.Contains("kubepods", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            Debug.WriteLine($"[Directories] container detection failed: {ex}");
        }

        return false;
    });

    // ---------- Path Resolution ----------

    /// <summary>Base path resolution with ENV and platform-aware fallbacks.</summary>
    private static Lazy<string> BasePathLazy = new(() =>
    {
        // 1) Explicit test override
        if (!string.IsNullOrEmpty(s_basePathOverride))
        {
            return Path.GetFullPath(s_basePathOverride);
        }

        // 2) Environment override
        string? env = GET_ENV("NALIX_BASE_PATH");
        if (!string.IsNullOrEmpty(env))
        {
            return Path.GetFullPath(env);
        }

        // 3) Container defaults
        if (IsContainerLazy.Value)
        {
            return "/data";
        }

        // 4) Non-container, OS-aware
        if (OperatingSystem.IsWindows())
        {
            string root = System.Environment.GetFolderPath(System.Environment.SpecialFolder.CommonApplicationData);
            return Path.Join(root, "Nalix");
        }
        // XDG base dir or ~/.local/share
        string? xdg = GET_ENV("XDG_DATA_HOME");
        string dataHome = !string.IsNullOrEmpty(xdg)
            ? xdg
                : Path.Join(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".local", "share");

        return Path.Join(dataHome, "Nalix");
    });

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string RESOLVE_OR_ENV(
        [DisallowNull] string envName,
        [DisallowNull] string containerPath,
        [DisallowNull] string relative)
    {
        string? env = GET_ENV(envName);
        if (!string.IsNullOrEmpty(env))
        {
            return Path.GetFullPath(env);
        }
        else if (IsContainerLazy.Value && Directory.Exists(containerPath))
        {
            return containerPath;
        }
        else
        {
            return Path.Join(BasePathLazy.Value, relative);
        }
    }

    /// <summary>
    /// Resolves the temporary directory. The shared system temp root (for example <c>/tmp</c> inside a container) is
    /// never used directly: the directory is hardened to private permissions and purged of old files, which must
    /// only ever happen to a directory owned by Nalix, so a <c>nalix</c> subdirectory is used instead.
    /// </summary>
    private static string RESOLVE_TEMP_PATH()
    {
        string path = RESOLVE_OR_ENV("NALIX_TEMP_PATH", "/tmp", Path.Join("data", "tmp"));
        string trimmed = Path.TrimEndingDirectorySeparator(path);

        return string.Equals(trimmed, "/tmp", StringComparison.Ordinal) ||
               string.Equals(trimmed, Path.TrimEndingDirectorySeparator(Path.GetTempPath()), StringComparison.Ordinal)
            ? Path.Join(trimmed, "nalix")
            : path;
    }

    // ---------- Lazies for each directory ----------

    private static Lazy<string> DataPathLazy = new(() => ENSURE_AND_HARDEN(RESOLVE_OR_ENV("NALIX_DATA_PATH", "/data", "data")));

    private static Lazy<string> LogsPathLazy = new(() => ENSURE_AND_HARDEN(RESOLVE_OR_ENV("NALIX_LOGS_PATH", "/logs", "logs"), UnixDirPerms.WorldReadable));

    private static Lazy<string> ConfigPathLazy = new(() => ENSURE_AND_HARDEN(RESOLVE_OR_ENV("NALIX_CONFIG_PATH", "/config", "config"), UnixDirPerms.Private700));

    private static Lazy<string> StoragePathLazy = new(() => ENSURE_AND_HARDEN(RESOLVE_OR_ENV("NALIX_STORAGE_PATH", "/storage", "storage"), UnixDirPerms.Shared750));

    private static Lazy<string> DatabasePathLazy = new(() => ENSURE_AND_HARDEN(RESOLVE_OR_ENV("NALIX_DB_PATH", "/db", "db"), UnixDirPerms.Private700));

    private static Lazy<string> CachesPathLazy = new(() => ENSURE_AND_HARDEN(Path.Join(DataPathLazy.Value, "caches")));

    private static Lazy<string> UploadsPathLazy = new(() => ENSURE_AND_HARDEN(Path.Join(DataPathLazy.Value, "uploads"), UnixDirPerms.Shared750));

    private static Lazy<string> BackupsPathLazy = new(() => ENSURE_AND_HARDEN(Path.Join(DataPathLazy.Value, "backups"), UnixDirPerms.Private700));

    private static Lazy<string> TempPathLazy = new(() =>
    {
        string path = RESOLVE_TEMP_PATH();
        _ = ENSURE_AND_HARDEN(path, UnixDirPerms.Private700);

        int days = 7;
        string? envDaysStr = GET_ENV("NALIX_TEMP_RETENTION_DAYS");
        if (!string.IsNullOrEmpty(envDaysStr) && int.TryParse(envDaysStr, out int envDaysParsed) && envDaysParsed > 0)
        {
            days = envDaysParsed;
        }

        _ = DeleteOldFiles(path, TimeSpan.FromDays(days));
        return path ?? string.Empty;
    });

    /// <summary>
    /// Re-initialises all internal <see cref="Lazy{T}"/> instances.
    /// Used when the base path is overridden to ensure already-evaluated paths are updated.
    /// </summary>
    private static void RESET_LAZIES()
    {
        s_directoryLock.EnterWriteLock();
        try
        {
            BasePathLazy = new(() =>
            {
                // 1. Override
                if (!string.IsNullOrEmpty(s_basePathOverride))
                {
                    return Path.GetFullPath(s_basePathOverride);
                }

                // 2. ENV
                string? env = GET_ENV("NALIX_BASE_PATH");
                if (!string.IsNullOrEmpty(env))
                {
                    return Path.GetFullPath(env);
                }

                // 3. Container
                if (IsContainerLazy.Value)
                {
                    return "/data";
                }

                // 4. OS-specific
                if (OperatingSystem.IsWindows())
                {
                    string root = System.Environment.GetFolderPath(System.Environment.SpecialFolder.CommonApplicationData);
                    return Path.Join(root, "Nalix");
                }

                // 5. Linux / XDG
                string? xdg = GET_ENV("XDG_DATA_HOME");
                string dataHome = !string.IsNullOrEmpty(xdg) ? xdg : Path.Join(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".local", "share");
                return Path.Join(dataHome, "Nalix");
            });

            DataPathLazy = new(() => ENSURE_AND_HARDEN(RESOLVE_OR_ENV("NALIX_DATA_PATH", "/data", "data")));
            LogsPathLazy = new(() => ENSURE_AND_HARDEN(RESOLVE_OR_ENV("NALIX_LOGS_PATH", "/logs", "logs"), UnixDirPerms.WorldReadable));
            ConfigPathLazy = new(() => ENSURE_AND_HARDEN(RESOLVE_OR_ENV("NALIX_CONFIG_PATH", "/config", "config"), UnixDirPerms.Private700));
            StoragePathLazy = new(() => ENSURE_AND_HARDEN(RESOLVE_OR_ENV("NALIX_STORAGE_PATH", "/storage", "storage"), UnixDirPerms.Shared750));
            DatabasePathLazy = new(() => ENSURE_AND_HARDEN(RESOLVE_OR_ENV("NALIX_DB_PATH", "/db", "db"), UnixDirPerms.Private700));

            CachesPathLazy = new(() => ENSURE_AND_HARDEN(Path.Join(DataPathLazy.Value, "caches")));
            UploadsPathLazy = new(() => ENSURE_AND_HARDEN(Path.Join(DataPathLazy.Value, "uploads"), UnixDirPerms.Shared750));
            BackupsPathLazy = new(() => ENSURE_AND_HARDEN(Path.Join(DataPathLazy.Value, "backups"), UnixDirPerms.Private700));

            TempPathLazy = new(() =>
            {
                string path = RESOLVE_TEMP_PATH();
                _ = ENSURE_AND_HARDEN(path, UnixDirPerms.Private700);
                int days = 7;
                string? envDaysStr = GET_ENV("NALIX_TEMP_RETENTION_DAYS");
                if (!string.IsNullOrEmpty(envDaysStr) && int.TryParse(envDaysStr, out int envDaysParsed) && envDaysParsed > 0)
                {
                    days = envDaysParsed;
                }
                _ = DeleteOldFiles(path, TimeSpan.FromDays(days));
                return path ?? string.Empty;
            });
        }
        finally
        {
            s_directoryLock.ExitWriteLock();
        }
    }

    #endregion Private Properties
}
