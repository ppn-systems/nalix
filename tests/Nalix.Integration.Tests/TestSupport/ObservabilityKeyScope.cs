// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Reflection;
using System.Security.Cryptography;
using Nalix.Abstractions.Primitives;
using Nalix.Observability;

namespace Nalix.Integration.Tests.TestSupport;

/// <summary>
/// Snapshots the process-wide observability key, hands out unique key-file paths and restores the snapshot on dispose.
/// </summary>
internal sealed class ObservabilityKeyScope : IDisposable
{
    private static readonly FieldInfo s_keyField = GetStaticField("s_key");
    private static readonly FieldInfo s_loadedField = GetStaticField("s_loaded");

    private readonly Bytes32 _savedKey;
    private readonly bool _savedLoaded;
    private readonly string _directory;

    public ObservabilityKeyScope()
    {
        _savedKey = (Bytes32)s_keyField.GetValue(null)!;
        _savedLoaded = (bool)s_loadedField.GetValue(null)!;

        _directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        _ = Directory.CreateDirectory(_directory);
    }

    /// <summary>Gets a unique path inside the scope directory. The file itself is not created.</summary>
    public string NewPath() => Path.Combine(_directory, Path.GetRandomFileName());

    /// <summary>Writes a random key to a new file, loads it and returns the key.</summary>
    public Bytes32 WriteAndLoadRandomKey()
    {
        Bytes32 key = new(RandomNumberGenerator.GetBytes(Bytes32.Size));
        string path = NewPath();
        File.WriteAllText(path, ToHex(key) + "\n");
        ObservabilityKey.Load(path);
        return key;
    }

    /// <summary>Forces the next <see cref="ObservabilityKey.Current"/> read to take the lazy-load path.</summary>
    public static void ResetLazyLoad() => s_loadedField.SetValue(null, false);

    /// <summary>Formats a key as the lowercase hex line stored in a key file.</summary>
    public static string ToHex(Bytes32 key) => Convert.ToHexStringLower(key.AsSpan());

    public void Dispose()
    {
        s_keyField.SetValue(null, _savedKey);
        s_loadedField.SetValue(null, _savedLoaded);

        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static FieldInfo GetStaticField(string name) =>
        typeof(ObservabilityKey).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException($"ObservabilityKey.{name} no longer exists.");
}
