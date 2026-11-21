// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Security.AccessControl;
using System.Security.Principal;
using Nalix.Abstractions.Diagnostics;
using Nalix.Abstractions.Exceptions;

namespace Nalix.Environment.IO;

public static partial class Directories
{
    private static DiagnosticListener Listener => DiagnosticsEvents.Source;

    /// <summary>
    /// Recommended Unix directory permissions for Abstractions directory types.
    /// </summary>
    private enum UnixDirPerms : byte
    {
        Default755 = 0x01,
        Private700 = 0x02,
        Shared750 = 0x03,
        WorldReadable = 0x04
    }

    /// <summary>
    /// Ensures a directory exists and applies platform-appropriate permissions. Returns the normalized full path.
    /// </summary>
    /// <param name="path">
    /// The directory path to ensure exists and harden.
    /// </param>
    /// <param name="perms">
    /// The Unix permission profile to apply when possible.
    /// </param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string ENSURE_AND_HARDEN([DisallowNull] string path, UnixDirPerms perms = UnixDirPerms.Default755)
    {
        ENSURE_DIRECTORY_EXISTS(path);
        HARDEN_PERMISSIONS(path, perms);
        return path;
    }

    /// <summary>
    /// Creates a directory if it does not already exist. Thread safe.
    /// </summary>
    /// <param name="path">
    /// The directory path to create or validate.
    /// </param>
    /// <param name="callerMemberName">
    /// The caller member name captured for diagnostics.
    /// </param>
    /// <param name="callerLineNumber">
    /// The caller source line captured for diagnostics.
    /// </param>
    /// <exception cref="ArgumentNullException"></exception>
    /// <exception cref="IOException"></exception>
    private static void ENSURE_DIRECTORY_EXISTS([DisallowNull] string path, [CallerMemberName] string callerMemberName = "", [CallerLineNumber] int callerLineNumber = 0)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentNullException(nameof(path));
        }

        path = Path.GetFullPath(path);

        s_directoryLock.EnterReadLock();
        try
        {
            if (Directory.Exists(path))
            {
                return;
            }
        }
        finally
        {
            s_directoryLock.ExitReadLock();
        }

        s_directoryLock.EnterWriteLock();
        try
        {
            if (!Directory.Exists(path))
            {
                _ = Directory.CreateDirectory(path);

                if (Listener.IsEnabled(DiagnosticsEvents.IO.Directory))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.IO.Directory, new DiagnosticLog("ENV.Directories:Internal", $"directory-created path={path} caller={callerMemberName}"));
                }
            }
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            string msg =
                "Failed to create directory: " + path + ". ERROR: " + ex.Message +
                " (Caller: " + callerMemberName + " at " + nameof(Directories) + ":" + callerLineNumber + ")";
            throw new IOException(msg, ex);
        }
        finally
        {
            s_directoryLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Attempts to set secure permissions on the specified directory depending on the platform. Fails silently if cannot be set.
    /// </summary>
    /// <param name="path">
    /// The directory path whose permissions should be hardened.
    /// </param>
    /// <param name="perms">
    /// The Unix permission profile to apply when possible.
    /// </param>
    private static void HARDEN_PERMISSIONS(string path, UnixDirPerms perms)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                DirectoryInfo di = new(path);
                DirectorySecurity ds = di.GetAccessControl();

                // Disable inheritance to ensure we have full control over the ACL
                ds.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

                SecurityIdentifier admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
                SecurityIdentifier system = new(WellKnownSidType.LocalSystemSid, null);
                SecurityIdentifier owner = new(WellKnownSidType.CreatorOwnerSid, null);
                SecurityIdentifier users = new(WellKnownSidType.BuiltinUsersSid, null);

                // Always allow Admins and System full control
                ds.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                ds.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                ds.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));

                // Only grant rights to the 'Users' group if the permission level is higher than Private700.
                // For Private700, the inheritance is already disabled and only Admin/System/Owner have access.
                if (perms != UnixDirPerms.Private700)
                {
                    FileSystemRights userRights = perms switch
                    {
                        UnixDirPerms.Shared750 => FileSystemRights.ReadAndExecute,
                        UnixDirPerms.WorldReadable => FileSystemRights.ReadAndExecute | FileSystemRights.ListDirectory,
                        UnixDirPerms.Default755 => FileSystemRights.ReadAndExecute | FileSystemRights.ListDirectory,
                        UnixDirPerms.Private700 or _ => FileSystemRights.ReadAndExecute
                    };

                    ds.AddAccessRule(new FileSystemAccessRule(users, userRights, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                }

                di.SetAccessControl(ds);
            }
            else
            {
                UnixFileMode mode = perms switch
                {
                    UnixDirPerms.Private700 =>
                        UnixFileMode.UserRead |
                        UnixFileMode.UserWrite |
                        UnixFileMode.UserExecute,
                    UnixDirPerms.Shared750 =>
                        UnixFileMode.UserRead |
                        UnixFileMode.UserWrite |
                        UnixFileMode.UserExecute |
                        UnixFileMode.GroupRead |
                        UnixFileMode.GroupExecute,
                    UnixDirPerms.WorldReadable =>
                        UnixFileMode.UserRead |
                        UnixFileMode.UserWrite |
                        UnixFileMode.UserExecute |
                        UnixFileMode.GroupRead |
                        UnixFileMode.GroupExecute |
                        UnixFileMode.OtherRead |
                        UnixFileMode.OtherExecute,
                    UnixDirPerms.Default755 =>
                        UnixFileMode.UserRead |
                        UnixFileMode.UserWrite |
                        UnixFileMode.UserExecute |
                        UnixFileMode.GroupRead |
                        UnixFileMode.GroupExecute |
                        UnixFileMode.OtherRead |
                        UnixFileMode.OtherExecute,
                    _ => throw new ArgumentOutOfRangeException(nameof(perms), perms, "Unknown UnixDirPerms value")
                };

                _ = SET_UNIX_FILE_MODE_COMPAT(path, mode);
            }
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            Debug.WriteLine($"[Directories] HARDEN_PERMISSIONS failed for '{path}': {ex}");
        }
    }

    /// <summary>
    /// Checks if the current process has write access to the specified directory.
    /// </summary>
    private static bool HAS_WRITE_ACCESS(string path)
    {
        if (!Directory.Exists(path))
        {
            return false;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                // On Windows, we check the Access Control List (ACL)
                DirectoryInfo di = new(path);
                if (di.Attributes.HasFlag(FileAttributes.ReadOnly))
                {
                    return false;
                }

                DirectorySecurity ds = di.GetAccessControl();
                AuthorizationRuleCollection rules = ds.GetAccessRules(true, true, typeof(SecurityIdentifier));
                WindowsIdentity currentIdentity = WindowsIdentity.GetCurrent();

                bool hasWrite = false;
                foreach (FileSystemAccessRule rule in rules)
                {
                    if (currentIdentity.User?.Equals(rule.IdentityReference) == true ||
                        (currentIdentity.Groups != null && currentIdentity.Groups.Contains(rule.IdentityReference)))
                    {
                        if (rule.AccessControlType == AccessControlType.Deny)
                        {
                            if ((rule.FileSystemRights & (FileSystemRights.Write | FileSystemRights.CreateFiles)) != 0)
                            {
                                return false; // Explicit Deny
                            }
                        }
                        else if (rule.AccessControlType == AccessControlType.Allow)
                        {
                            if ((rule.FileSystemRights & (FileSystemRights.Write | FileSystemRights.CreateFiles)) != 0)
                            {
                                hasWrite = true;
                            }
                        }
                    }
                }
                return hasWrite;
            }
            else
            {
                // On Unix-like systems, we use the libc access() syscall (W_OK = 2)
                // This is the most reliable way as it respects UID, GID, and Read-Only mounts.
                return ACCESS(path, 2) == 0;
            }
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            Debug.WriteLine($"[Directories] HAS_WRITE_ACCESS failed for '{path}': {ex}");
            return false;
        }
    }

    /// <summary>
    /// Combines and normalizes a child path under a base directory,
    /// preventing directory traversal outside of the base directory.
    /// </summary>
    /// <param name="baseDir">
    /// The trusted base directory.
    /// </param>
    /// <param name="name">
    /// The child path segment to combine under the base directory.
    /// </param>
    /// <exception cref="UnauthorizedAccessException"></exception>
    [EditorBrowsable(
        EditorBrowsableState.Never)]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string COMBINE_SAFE([DisallowNull] string baseDir, [DisallowNull] string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (Path.IsPathRooted(name))
        {
            THROW_ABSOLUTE_PATH_NOT_ALLOWED(name);
        }

        string baseFull = Path.GetFullPath(baseDir);

        if (!baseFull.AsSpan().EndsWith(Path.DirectorySeparatorChar))
        {
            baseFull += Path.DirectorySeparatorChar;
        }

        string full = Path.GetFullPath(Path.Join(baseFull, name));
        string rel = Path.GetRelativePath(baseFull, full);

        char sep = Path.DirectorySeparatorChar;

        StringComparison comp = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (Path.IsPathRooted(rel) ||
            rel.Equals("..", comp) ||
            rel.StartsWith(".." + sep, comp))
        {
            THROW_PATH_ESCAPES_BASE_DIR(name);
        }

        return full;
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void THROW_ABSOLUTE_PATH_NOT_ALLOWED(string name)
        => throw new UnauthorizedAccessException($"Absolute path '{name}' is not allowed.");

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void THROW_PATH_ESCAPES_BASE_DIR(string name)
        => throw new UnauthorizedAccessException($"Path '{name}' escapes base directory.");

    [EditorBrowsable(EditorBrowsableState.Never)]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool SET_UNIX_FILE_MODE_COMPAT([DisallowNull] string path, UnixFileMode mode)
    {
        // Use libc chmod via P/Invoke on Unix platforms.
        // This is a direct native call — fully AOT-safe, no reflection required.
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                uint native = TO_NATIVE_CHMOD_MODE(mode);
                int rc = CHMOD(path, native);
                return rc == 0;
            }
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            Debug.WriteLine($"[Directories] chmod failed for '{path}': {ex}");
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint TO_NATIVE_CHMOD_MODE(UnixFileMode mode)
    {
        uint m = 0;

        // Special bits
        if ((mode & UnixFileMode.SetUser) != 0)
        {
            m |= 0x800; // 04000
        }

        if ((mode & UnixFileMode.SetGroup) != 0)
        {
            m |= 0x400; // 02000
        }

        if ((mode & UnixFileMode.StickyBit) != 0)
        {
            m |= 0x200; // 01000
        }

        // USER
        if ((mode & UnixFileMode.UserRead) != 0)
        {
            m |= 0x100; // 0400
        }

        if ((mode & UnixFileMode.UserWrite) != 0)
        {
            m |= 0x080; // 0200
        }

        if ((mode & UnixFileMode.UserExecute) != 0)
        {
            m |= 0x040; // 0100
        }

        // Group
        if ((mode & UnixFileMode.GroupRead) != 0)
        {
            m |= 0x020; // 0040
        }

        if ((mode & UnixFileMode.GroupWrite) != 0)
        {
            m |= 0x010; // 0020
        }

        if ((mode & UnixFileMode.GroupExecute) != 0)
        {
            m |= 0x008; // 0010
        }

        // Others
        if ((mode & UnixFileMode.OtherRead) != 0)
        {
            m |= 0x004; // 0004
        }

        if ((mode & UnixFileMode.OtherWrite) != 0)
        {
            m |= 0x002; // 0002
        }

        if ((mode & UnixFileMode.OtherExecute) != 0)
        {
            m |= 0x001; // 0001
        }

        return m;
    }

    /// <summary>
    /// P/Invoke libc chmod (fallback for Unix)
    /// </summary>
    /// <param name="pathname">
    /// The Unix path that will be passed to <c>chmod</c>.
    /// </param>
    /// <param name="mode">
    /// The native mode bits to apply.
    /// </param>
    [SkipLocalsInit]
    private static unsafe int CHMOD([DisallowNull] string pathname, uint mode)
    {
        byte* __pathname_native = default;
        int __retVal = 0;
        Utf8StringMarshaller.ManagedToUnmanagedIn __pathname_native__marshaller = default;
        int __lastError;

        try
        {
            Span<byte> buffer = stackalloc byte[Utf8StringMarshaller.ManagedToUnmanagedIn.BufferSize];
#pragma warning disable CS9080
            __pathname_native__marshaller.FromManaged(pathname, buffer);
#pragma warning restore CS9080
            __pathname_native = __pathname_native__marshaller.ToUnmanaged();
            Marshal.SetLastSystemError(0);
            __retVal = __PInvoke(__pathname_native, mode);
            __lastError = Marshal.GetLastSystemError();
        }
        finally
        {
            __pathname_native__marshaller.Free();
        }

        Marshal.SetLastPInvokeError(__lastError);
        return __retVal;

        [DllImport("libc", EntryPoint = "chmod", ExactSpelling = true)]
        static extern int __PInvoke(byte* __pathname_native, uint __mode_native);
    }

    /// <summary>
    /// P/Invoke libc access (W_OK = 2)
    /// </summary>
    [SkipLocalsInit]
    private static unsafe int ACCESS([DisallowNull] string pathname, int mode)
    {
        byte* __pathname_native = default;
        int __retVal = 0;
        Utf8StringMarshaller.ManagedToUnmanagedIn __pathname_native__marshaller = default;

        try
        {
            Span<byte> buffer = stackalloc byte[Utf8StringMarshaller.ManagedToUnmanagedIn.BufferSize];
#pragma warning disable CS9080
            __pathname_native__marshaller.FromManaged(pathname, buffer);
#pragma warning restore CS9080
            __pathname_native = __pathname_native__marshaller.ToUnmanaged();
            __retVal = __PInvoke(__pathname_native, mode);
        }
        finally
        {
            __pathname_native__marshaller.Free();
        }
        return __retVal;

        [DllImport("libc", EntryPoint = "access", ExactSpelling = true)]
        static extern int __PInvoke(byte* __pathname_native, int __mode_native);
    }
}
