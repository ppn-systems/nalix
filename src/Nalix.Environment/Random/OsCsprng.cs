// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Nalix.Abstractions.Exceptions;

namespace Nalix.Environment.Random;

/// <summary>
/// Provides cryptographically secure random number generation using the operating system's CSPRNG facilities.
/// </summary>
[StackTraceHidden]
[DebuggerStepThrough]
[EditorBrowsable(EditorBrowsableState.Never)]
internal static partial class OsCsprng
{
    #region Fields

    // Cached platform dispatcher (obfuscated)
    private static Action<Span<byte>> s_f;

    // Lazy /dev/urandom handle for Unix fallback
    private static readonly Lock s_devUrandomLock = new();
    private static Microsoft.Win32.SafeHandles.SafeFileHandle? s_devUrandomHandle;

    // Linux error codes (architecture-aware)
    private static readonly int s_eINTR = 4;
    private static readonly int s_eNOSYS = RuntimeInformation.ProcessArchitecture switch
    {
        // On MIPS, ENOSYS is 89. Most other Linux architectures use 38.
        (Architecture)9 => 89, // Mips64 (if not in enum, use cast)
        Architecture.X86 or
        Architecture.X64 or
        Architecture.Arm or
        Architecture.Arm64 or
        Architecture.Wasm or
        Architecture.S390x or
        Architecture.LoongArch64 or
        Architecture.Armv6 or
        Architecture.Ppc64le or
        _ => 38
    };

    // Windows CNG flag
    private const uint C = 0x00000002;

    #endregion Fields

    #region Constructor

    static OsCsprng()
    {
        // On published Apple binaries, the bare "Security" name does not resolve via dlopen because the
        // framework lives at /System/Library/Frameworks/Security.framework/Security. Map it explicitly.
        if (OperatingSystem.IsMacOS() ||
            OperatingSystem.IsIOS() ||
            OperatingSystem.IsTvOS() ||
            OperatingSystem.IsWatchOS())
        {
            NativeLibrary.SetDllImportResolver(typeof(OsCsprng).Assembly, static (name, _, _) =>
                name == "Security" &&
                NativeLibrary.TryLoad("/System/Library/Frameworks/Security.framework/Security", out nint h)
                    ? h
                    : nint.Zero);
        }

        s_f = OperatingSystem.IsWindows()
            ? W
            : OperatingSystem.IsBrowser()
                ? B
                : OperatingSystem.IsLinux() ||
                  OperatingSystem.IsAndroid()
                    ? L
                    : OperatingSystem.IsMacOS() ||
                      OperatingSystem.IsIOS() ||
                      OperatingSystem.IsTvOS() ||
                      OperatingSystem.IsWatchOS()
                        ? A
                        : D;
    }

    #endregion Constructor

    #region APIs

    /// <summary>
    /// Fills the specified buffer with cryptographically secure random bytes using the operating system's CSPRNG facilities.
    /// </summary>
    /// <param name="buffer">The buffer to fill with random bytes.</param>
    /// <remarks>
    /// Thread-safe. Uses platform-specific implementations:
    /// - Windows: BCryptGenRandom (CNG)
    /// - Linux: getrandom() syscall with /dev/urandom fallback
    /// - macOS/iOS/tvOS/watchOS: SecRandomCopyBytes
    /// - Other platforms: /dev/urandom
    /// Falls back to OsRandom (non-cryptographic) if OS CSPRNG is unavailable.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown when OS CSPRNG is unavailable and fallback fails.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Fill(Span<byte> buffer)
    {
        if (buffer.Length == 0)
        {
            return;
        }

        s_f(buffer);
    }

    /// <summary>
    /// Shuts down the OS CSPRNG subsystem and releases any cached file handles.
    /// </summary>
    public static void Shutdown()
    {
        lock (s_devUrandomLock)
        {
            s_devUrandomHandle?.Dispose();
            s_devUrandomHandle = null;
        }
    }

    #endregion APIs

    #region Private

    // -------------------- Windows (CNG) --------------------

    /// <summary>
    /// P/Invoke declaration for Windows BCryptGenRandom function.
    /// </summary>
    [LibraryImport("Bcrypt.dll")]
    private static partial int BCryptGenRandom(nint hAlgorithm, Span<byte> pbBuffer, int cbBuffer, uint dwFlags);

    /// <summary>
    /// Windows-specific CSPRNG implementation using BCryptGenRandom (CNG).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void W(Span<byte> b)
    {
        int status = BCryptGenRandom(nint.Zero, b, b.Length, C);
        if (status != 0)
        {
            throw new CipherException($"BCryptGenRandom failed: status=0x{status:X}.");
        }
    }

    // -------------------- Linux: getrandom --------------------

    /// <summary>
    /// P/Invoke declaration for Linux getrandom syscall.
    /// </summary>
    [LibraryImport("libc", SetLastError = true)]
    private static partial nint getrandom(nint buf, nint buflen, uint flags);

    /// <summary>
    /// Linux-specific CSPRNG implementation using getrandom() syscall.
    /// Falls back to /dev/urandom if getrandom is not supported (ENOSYS).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    [System.Runtime.Versioning.SupportedOSPlatform("android")]
    private static unsafe void L(Span<byte> b)
    {
        fixed (byte* p = b)
        {
            nuint t = nuint.Zero;
            nuint n = (nuint)b.Length;

            while (t < n)
            {
                nint r0 = getrandom((nint)(p + (nint)t), (nint)(n - t), 0);
                long r = r0.ToInt64();
                if (r < 0)
                {
                    int errno = Marshal.GetLastPInvokeError();

                    // EINTR: retry
                    if (errno == s_eINTR)
                    {
                        continue;
                    }

                    // ENOSYS: kernel does not support getrandom -> permanently switch to /dev/urandom fallback
                    if (errno == s_eNOSYS)
                    {
                        // switch dispatcher once so future calls skip getrandom
                        Volatile.Write(ref s_f, D);

                        // fill the remaining part via fallback
                        int offset = (int)t;
                        if (offset < b.Length)
                        {
                            D(b[offset..]);
                        }
                        return;
                    }

                    throw new CipherException($"OS CSPRNG unavailable (getrandom failed with errno={errno}).");
                }

                if (r == 0)
                {
                    // Defensive: zero progress from getrandom should not loop forever
                    throw new CipherException("OS CSPRNG unavailable (getrandom returned 0 bytes).");
                }

                t += (nuint)r;
            }
        }
    }

    // -------------------- Apple: SecRandomCopyBytes --------------------

    /// <summary>
    /// P/Invoke declaration for Apple SecRandomCopyBytes function.
    /// </summary>
    [LibraryImport("Security", EntryPoint = "SecRandomCopyBytes")]
    private static partial int SecRandomCopyBytes(nint rnd, nint count, nint bytes);

    /// <summary>
    /// Apple platform CSPRNG implementation using SecRandomCopyBytes.
    /// Falls back to /dev/urandom if SecRandomCopyBytes fails.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    [System.Runtime.Versioning.SupportedOSPlatform("ios")]
    [System.Runtime.Versioning.SupportedOSPlatform("tvos")]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    [System.Runtime.Versioning.SupportedOSPlatform("watchos")]
    private static unsafe void A(Span<byte> b)
    {
        int s;
        try
        {
            fixed (byte* p = b)
            {
                s = SecRandomCopyBytes(nint.Zero, b.Length, (nint)p);
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // Security framework could not be resolved (e.g. published osx-x64) -> permanently fall back.
            Volatile.Write(ref s_f, D);
            D(b);
            return;
        }

        if (s != 0)
        {
            Volatile.Write(ref s_f, D);
            D(b);
        }
    }

    // -------------------- Fallback: /dev/urandom --------------------

    /// <summary>
    /// Fallback CSPRNG implementation using /dev/urandom.
    /// Used on platforms without native CSPRNG support or when native APIs fail.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void D(Span<byte> b)
    {
#pragma warning disable CA2000 // GetDevUrandomHandle returns a shared static handle that should not be disposed here.
        SafeFileHandle handle = GET_DEV_URANDOM_HANDLE();
#pragma warning restore CA2000

        // SafeFileHandle with RandomAccess is thread-safe on Unix for character devices.
        // We don't need the global lock here anymore.
        int total = 0;
        while (total < b.Length)
        {
            int r = RandomAccess.Read(handle, b[total..], 0); // RandomAccess rejects negative offsets; the offset is ignored for character devices
            if (r <= 0)
            {
                throw new CipherException("OS CSPRNG unavailable (/dev/urandom returned 0 bytes).");
            }

            total += r;
        }
    }

    /// <summary>
    /// Gets a cached SafeFileHandle for /dev/urandom (created lazily).
    /// Thread-safe double-checked locking pattern.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static SafeFileHandle GET_DEV_URANDOM_HANDLE()
    {
        SafeFileHandle? handle = Volatile.Read(ref s_devUrandomHandle);

        if (handle is { IsInvalid: false })
        {
            return handle;
        }

        lock (s_devUrandomLock)
        {
            handle = s_devUrandomHandle;
            if (handle is { IsInvalid: false })
            {
                return handle;
            }

            handle = File.OpenHandle(
                "/dev/urandom",
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                FileOptions.SequentialScan);

            Volatile.Write(ref s_devUrandomHandle, handle);
            return handle;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [System.Runtime.Versioning.SupportedOSPlatform("browser")]
    private static void B(Span<byte> b) => System.Security.Cryptography.RandomNumberGenerator.Fill(b);

    #endregion Private
}
