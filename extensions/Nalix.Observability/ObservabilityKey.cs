// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Security.Cryptography;
using Nalix.Abstractions.Primitives;
using Nalix.Environment.IO;

namespace Nalix.Observability;

/// <summary>
/// Holds the 32-byte administrative key that grants observability access.
/// </summary>
/// <remarks>
/// The key file contains one 64-character hex key; blank lines and lines starting with <c>#</c> are ignored.
/// A missing or unreadable key file is replaced by a freshly generated key so the server never runs keyless.
/// </remarks>
public static class ObservabilityKey
{
    private static readonly Lock s_lock = new();
    private static Bytes32 s_key;
    private static bool s_loaded;

    /// <summary>
    /// Gets the default key file path inside the Nalix configuration directory.
    /// </summary>
    public static string DefaultPath => Path.Combine(Directories.ConfigurationDirectory, "observability.private");

    /// <summary>
    /// Gets the current administrative key, loading it from <see cref="DefaultPath"/> on first use.
    /// </summary>
    public static Bytes32 Current
    {
        get
        {
            lock (s_lock)
            {
                if (!s_loaded)
                {
                    s_key = READ_OR_CREATE(DefaultPath);
                    s_loaded = true;
                }

                return s_key;
            }
        }
    }

    /// <summary>
    /// Loads the administrative key from <paramref name="path"/>, creating the file when it does not exist.
    /// </summary>
    /// <param name="path">The key file path.</param>
    public static void Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        Bytes32 key = READ_OR_CREATE(path);
        lock (s_lock)
        {
            s_key = key;
            s_loaded = true;
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design", "CA1031:Do not catch general exception types",
        Justification = "Key loading is best-effort; any failure falls back to a generated key.")]
    private static Bytes32 READ_OR_CREATE(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                foreach (string line in File.ReadLines(path))
                {
                    ReadOnlySpan<char> value = line.AsSpan().Trim();
                    if (value.Length == 0 || value[0] == '#')
                    {
                        continue;
                    }

                    Span<byte> raw = stackalloc byte[Bytes32.Size];
                    if (value.Length == Bytes32.Size * 2 &&
                        Convert.FromHexString(value, raw, out _, out int written) == System.Buffers.OperationStatus.Done &&
                        written == Bytes32.Size)
                    {
                        return new Bytes32(raw);
                    }

                    break;
                }
            }

            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                _ = Directory.CreateDirectory(directory);
            }
        }
        catch
        {
            // Fall through to key generation.
        }

        Span<byte> generated = stackalloc byte[Bytes32.Size];
        RandomNumberGenerator.Fill(generated);
        _ = Directories.TryWriteNewFile(path, Convert.ToHexStringLower(generated), isPrivate: true);
        return new Bytes32(generated);
    }
}
