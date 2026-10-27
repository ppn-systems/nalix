// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using Nalix.Abstractions.Exceptions;

namespace Nalix.Environment.Configuration.Binding;

/// <summary>
/// A high-performance wrapper class for reading and writing INI files.
/// </summary>
[DebuggerNonUserCode]
[SkipLocalsInit]
[ExcludeFromCodeCoverage]
[EditorBrowsable(EditorBrowsableState.Never)]
[DebuggerDisplay("Path = {_path}, Sections = {_iniData.Count}, Dirty = {_isDirty}")]
public sealed class IniConfig : IDisposable
{
    #region Constants

    // LZ4CompressionConstants for better readability and performance
    private const char SectionStart = '[';

    private const char SectionEnd = ']';
    private const char KeyValueSeparator = '=';
    private const char CommentChar = ';';

    // Standard buffer sizes
    private const int DefaultBufferSize = 4096;

    // Memory leak protection
    private const int MaxCacheSize = 2048;

    #endregion Constants

    #region Fields

    private static readonly string s_sectionSeparator = "; " + new string('-', 78);

    // Thread synchronization for file operations
    private readonly ReaderWriterLockSlim _fileLock;
    private readonly string _path;
    private readonly Dictionary<string, Dictionary<string, string>> _iniData;

    // Caches for frequently accessed values
    private readonly Dictionary<string, object> _valueCache;

    // Track if the file has been modified
    private volatile bool _isDirty;

    // Stores comments to be written above sections and keys.
    // Key format: "section" for section-level comments,
    //             "section:key" for property-level comments.
    // Values are never loaded from file — only populated by WriteComment().
    private readonly Dictionary<string, string> _comments;

    private DateTime _lastFileReadTime;

    #endregion Fields

    #region Properties

    /// <summary>
    /// Checks whether the file exists at the provided path.
    /// </summary>
    public bool ExistsFile => File.Exists(_path);

    /// <summary>
    /// Gets the absolute path to the INI configuration file.
    /// </summary>
    public string FilePath => _path;

    #endregion Properties

    #region Constructor

    /// <summary>
    /// Initializes a new instance of the <see cref="IniConfig"/> class for the specified path.
    /// </summary>
    /// <param name="path">The path to the INI file.</param>
    /// <exception cref="ArgumentNullException">Thrown when path is null.</exception>
    /// <exception cref="ArgumentException">Thrown when path is invalid.</exception>
    /// <exception cref="InternalErrorException">Thrown when path contains path traversal attempts.</exception>
    public IniConfig(string path)
    {
        if (path == null)
        {
            throw new ArgumentNullException(nameof(path), "Configuration file path cannot be null.");
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Configuration file path cannot be empty or whitespace.", nameof(path));
        }

        // Validate path for security - prevent path traversal
        try
        {
            _path = Path.GetFullPath(path);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException($"Invalid configuration file path: {path}", nameof(path), ex);
        }
        catch (InternalErrorException ex)
        {
            throw new InternalErrorException($"Security error accessing path: {path}", ex);
        }
        catch (NotSupportedException ex)
        {
            throw new ArgumentException($"Unsupported path format: {path}", nameof(path), ex);
        }

        // Additional validation - ensure path doesn't contain invalid characters
        if (_path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            throw new ArgumentException(
                $"Configuration file path contains invalid characters: {path}", nameof(path));
        }

        // Use case-insensitive keys for sections
        _iniData = new(StringComparer.Ordinal);
        _valueCache = new(StringComparer.Ordinal);
        _comments = new(StringComparer.Ordinal);
        _fileLock = new(LockRecursionPolicy.NoRecursion);

        // Load the file if it exists
        if (this.ExistsFile)
        {
            this.LoadWithRetry();
        }
    }

    #endregion Constructor

    #region Public API

    /// <summary>
    /// Reloads the INI file from disk, discarding any unsaved changes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public void Reload() => this.Load();

    /// <summary>
    /// Writes a value to the INI file.
    /// </summary>
    /// <param name="section">The section name in the INI file.</param>
    /// <param name="key">The key name in the section.</param>
    /// <param name="value">The value to write.</param>
    /// <exception cref="ArgumentNullException">Thrown when section, key, or value is null.</exception>
    /// <exception cref="ArgumentException">Thrown when section or key contains invalid characters.</exception>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void WriteValue(
        string section,
        string key,
        object value)
    {
        ArgumentNullException.ThrowIfNull(key, nameof(key));
        ArgumentNullException.ThrowIfNull(section, nameof(section));
        ArgumentNullException.ThrowIfNull(value, nameof(value));

        // Validate section and key don't contain special characters that would break INI format
        if (section.IndexOfAny(['\r', '\n', '[', ']']) >= 0)
        {
            throw new ArgumentException(
                "Section name cannot contain newline, '[', or ']' characters.", nameof(section));
        }

        if (key.IndexOfAny(['\r', '\n', '=']) >= 0)
        {
            throw new ArgumentException(
                "Key name cannot contain newline or '=' characters.", nameof(key));
        }

        _fileLock.EnterUpgradeableReadLock();
        try
        {
            // Check for external file changes
            this.CheckFileChanges();

            if (!_iniData.TryGetValue(
                section,
                out Dictionary<string, string>? sectionData))
            {
                _fileLock.EnterWriteLock();
                try
                {
                    sectionData = new Dictionary<
                        string, string>(StringComparer.Ordinal);

                    _iniData[section] = sectionData;
                }
                finally
                {
                    _fileLock.ExitWriteLock();
                }
            }

            _fileLock.EnterWriteLock();
            try
            {
                string stringValue = FormatValue(value);

                // Only update if the value has changed to avoid unnecessary I/O
                if (sectionData.TryGetValue(key, out string? existing) && existing == stringValue)
                {
                    return;
                }

                sectionData[key] = stringValue;

                // Dispose any cached values for this key.
                // Typed getters use "section:key:typeSuffix", so we must clear all keys starting with "section:key:".
                string exactCacheKey = CreateCacheKey(section, key);
                string typedCacheKeyPrefix = exactCacheKey + ":";

                _ = _valueCache.Remove(exactCacheKey);

                // Surgical removal of all typed cache entries
                List<string>? keysToRemove = null;
                foreach (string k in _valueCache.Keys)
                {
                    if (k.StartsWith(typedCacheKeyPrefix, StringComparison.Ordinal))
                    {
                        keysToRemove ??= [];
                        keysToRemove.Add(k);
                    }
                }

                if (keysToRemove != null)
                {
                    foreach (string k in keysToRemove)
                    {
                        _ = _valueCache.Remove(k);
                    }
                }

                _isDirty = true;
            }
            finally
            {
                _fileLock.ExitWriteLock();
            }
        }
        finally
        {
            _fileLock.ExitUpgradeableReadLock();
        }
    }

    /// <summary>
    /// Writes a comment to the INI file above a section header or a key-value pair.
    /// The comment is only recorded when the target does not yet exist in the file,
    /// matching the behaviour of <see cref="WriteValue"/>.
    /// </summary>
    /// <param name="section">The section the comment belongs to.</param>
    /// <param name="key">
    /// The key the comment belongs to, or <c>null</c> / empty to attach the comment
    /// to the <c>[Section]</c> header itself.
    /// </param>
    /// <param name="comment">
    /// The comment text. Multi-line comments are supported via embedded <c>\n</c>.
    /// Pass <c>null</c> or whitespace to skip writing (method becomes a no-op).
    /// </param>
    /// <remarks>
    /// Comments are stored in memory and flushed to disk on the next <see cref="WriteFile"/> call.
    /// They are never read back from disk — re-running the application re-registers them from attributes.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void WriteComment(
        string section,
        string? key,
        string? comment)
    {
        // Silently skip when no comment text is provided — callers do not need to null-check
        if (string.IsNullOrWhiteSpace(comment))
        {
            return;
        }

        ArgumentNullException.ThrowIfNull(section, nameof(section));

        // Build the lookup key: "Section" for section-level, "Section:Key" for property-level
        string commentKey = string.IsNullOrEmpty(key)
            ? section
            : CreateCacheKey(section, key);

        _fileLock.EnterUpgradeableReadLock();
        try
        {
            this.CheckFileChanges();

            // Only register the comment when the target does not yet exist in the file.
            // This mirrors WriteValue's guard — comment and value are always in sync.
            bool targetExists = !string.IsNullOrEmpty(key)
                && _iniData.TryGetValue(section,
                       out Dictionary<string, string>? sd)
                && sd.TryGetValue(key, out string? existing)
                && !string.IsNullOrEmpty(existing);

            if (!targetExists && !_comments.ContainsKey(commentKey))
            {
                _fileLock.EnterWriteLock();
                try
                {
                    _comments[commentKey] = comment;
                    _isDirty = true;
                }
                finally
                {
                    _fileLock.ExitWriteLock();
                }
            }
        }
        finally
        {
            _fileLock.ExitUpgradeableReadLock();
        }
    }


    /// <summary>
    /// Gets the value for the specified key in the specified section as a string.
    /// </summary>
    /// <param name="section">The section name in the INI file.</param>
    /// <param name="key">The key name in the section.</param>
    /// <returns>The string value, or an empty string if not found.</returns>
    /// <remarks>
    /// An environment variable named <c>NALIX_{section}_{key}</c> (uppercased) takes precedence
    /// over the INI file when present, letting deployments override configuration without editing files.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when section or key is null.</exception>
    /// <exception cref="ArgumentException">Thrown when section or key is empty.</exception>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public string GetString(
        string section,
        string key)
    {
        ArgumentNullException.ThrowIfNull(key, nameof(key));
        ArgumentNullException.ThrowIfNull(section, nameof(section));

        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Configuration key cannot be empty or whitespace.", nameof(key));
        }

        if (string.IsNullOrWhiteSpace(section))
        {
            throw new ArgumentException("Configuration section cannot be empty or whitespace.", nameof(section));
        }

        string? envOverride = System.Environment.GetEnvironmentVariable($"NALIX_{section}_{key}".ToUpperInvariant());
        if (envOverride is not null)
        {
            return envOverride;
        }

        // Check for file changes before reading
        this.CheckFileChanges();

        _fileLock.EnterReadLock();
        try
        {
            return _iniData.TryGetValue(section,
                out Dictionary<string, string>? sectionData) &&
                sectionData.TryGetValue(key, out string? value)
                ? value
                : string.Empty;
        }
        finally
        {
            _fileLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Gets the value for the specified key in the specified section as a character.
    /// </summary>
    /// <param name="section">The section name in the INI file.</param>
    /// <param name="key">The key name in the section.</param>
    /// <returns>The character value if the string has exactly one character; otherwise, null.</returns>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    [return: MaybeNull]
    public char? GetChar(
        string section,
        string key)
    {
        string stringValue = this.GetString(section, key);
        return !string.IsNullOrEmpty(stringValue) && stringValue.Length == 1 ? stringValue[0] : null;
    }

    /// <summary>
    /// Gets the value for the specified key in the specified section as a boolean.
    /// </summary>
    /// <param name="section">The section name in the INI file.</param>
    /// <param name="key">The key name in the section.</param>
    /// <returns>The boolean value if parsed successfully, otherwise null.</returns>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    [return: MaybeNull]
    public bool? GetBool(
        string section,
        string key)
    {
        string cacheKey = CreateCacheKey(section, key, "bool");

        if (this.TryGetCache(cacheKey, out object? cachedValue))
        {
            return (bool?)cachedValue;
        }

        string stringValue = this.GetString(section, key);
        bool? result = null;

        if (stringValue.Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        else if (!string.IsNullOrEmpty(stringValue))
        {
            // Optimize Abstractions boolean representations
            if (stringValue.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                stringValue.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                stringValue.Equals("yes", StringComparison.OrdinalIgnoreCase))
            {
                result = true;
            }
            else if (stringValue.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                     stringValue.Equals("0", StringComparison.OrdinalIgnoreCase) ||
                     stringValue.Equals("no", StringComparison.OrdinalIgnoreCase))
            {
                result = false;
            }
            else
            {
                _ = bool.TryParse(stringValue, out bool parsedValue);
                result = parsedValue;
            }

            // Caches the result
            this.AddToCache(cacheKey, result);
        }

        return result;
    }

    /// <summary>
    /// Gets the value for the specified key in the specified section as a decimal.
    /// </summary>
    /// <param name="section">The section name in the INI file.</param>
    /// <param name="key">The key name in the section.</param>
    /// <returns>The decimal value if parsed successfully, otherwise null.</returns>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    [return: MaybeNull]
    public decimal? GetDecimal(
        string section,
        string key)
    {
        string cacheKey = CreateCacheKey(section, key, "decimal");

        if (this.TryGetCache(cacheKey, out object? cachedValue))
        {
            return (decimal?)cachedValue;
        }

        string stringValue = this.GetString(section, key);

        if (stringValue.Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        else if (decimal.TryParse(
            stringValue, NumberStyles.Number,
            CultureInfo.InvariantCulture, out decimal parsedValue))
        {
            this.AddToCache(cacheKey, parsedValue);
            return parsedValue;
        }

        return null;
    }

    /// <summary>
    /// Gets the value for the specified key in the specified section as a byte.
    /// </summary>
    /// <param name="section">The section name in the INI file.</param>
    /// <param name="key">The key name in the section.</param>
    /// <returns>The byte value if parsed successfully, otherwise null.</returns>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    [return: MaybeNull]
    public byte? GetByte(
        string section,
        string key)
    {
        string cacheKey = CreateCacheKey(section, key, "byte");

        if (this.TryGetCache(cacheKey, out object? cachedValue))
        {
            return (byte?)cachedValue;
        }

        string stringValue = this.GetString(section, key);

        if (stringValue.Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        else if (byte.TryParse(stringValue, out byte parsedValue))
        {
            this.AddToCache(cacheKey, parsedValue);
            return parsedValue;
        }

        return null;
    }

    /// <summary>
    /// Gets the value for the specified key in the specified section as an sbyte.
    /// </summary>
    /// <param name="section">The section name in the INI file.</param>
    /// <param name="key">The key name in the section.</param>
    /// <returns>The sbyte value if parsed successfully, otherwise null.</returns>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    [return: MaybeNull]
    public sbyte? GetSByte(
        string section,
        string key)
    {
        string cacheKey = CreateCacheKey(section, key, "sbyte");

        if (this.TryGetCache(cacheKey, out object? cachedValue))
        {
            return (sbyte?)cachedValue;
        }

        string stringValue = this.GetString(section, key);

        if (stringValue.Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        else if (sbyte.TryParse(stringValue, out sbyte parsedValue))
        {
            this.AddToCache(cacheKey, parsedValue);
            return parsedValue;
        }

        return null;
    }

    /// <summary>
    /// Gets the value for the specified key in the specified section as a short.
    /// </summary>
    /// <param name="section">The section name in the INI file.</param>
    /// <param name="key">The key name in the section.</param>
    /// <returns>The short value if parsed successfully, otherwise null.</returns>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    [return: MaybeNull]
    public short? GetInt16(
        string section,
        string key)
    {
        string cacheKey = CreateCacheKey(section, key, "int16");

        if (this.TryGetCache(cacheKey, out object? cachedValue))
        {
            return (short?)cachedValue;
        }

        string stringValue = this.GetString(section, key);

        if (stringValue.Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        else if (short.TryParse(stringValue, out short parsedValue))
        {
            this.AddToCache(cacheKey, parsedValue);
            return parsedValue;
        }

        return null;
    }

    /// <summary>
    /// Gets the value for the specified key in the specified section as an unsigned short.
    /// </summary>
    /// <param name="section">The section name in the INI file.</param>
    /// <param name="key">The key name in the section.</param>
    /// <returns>The unsigned short value if parsed successfully, otherwise null.</returns>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    [return: MaybeNull]
    public ushort? GetUInt16(
        string section,
        string key)
    {
        string cacheKey = CreateCacheKey(section, key, "uint16");

        if (this.TryGetCache(cacheKey, out object? cachedValue))
        {
            return (ushort?)cachedValue;
        }

        string stringValue = this.GetString(section, key);

        if (stringValue.Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        else if (ushort.TryParse(stringValue, out ushort parsedValue))
        {
            this.AddToCache(cacheKey, parsedValue);
            return parsedValue;
        }

        return null;
    }

    /// <summary>
    /// Gets the value for the specified key in the specified section as an integer.
    /// </summary>
    /// <param name="section">The section name in the INI file.</param>
    /// <param name="key">The key name in the section.</param>
    /// <returns>The integer value if parsed successfully, otherwise null.</returns>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    [return: MaybeNull]
    public int? GetInt32(
        string section,
        string key)
    {
        string cacheKey = CreateCacheKey(section, key, "int32");

        if (this.TryGetCache(cacheKey, out object? cachedValue))
        {
            return (int?)cachedValue;
        }

        string stringValue = this.GetString(section, key);

        if (stringValue.Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        else if (int.TryParse(stringValue, out int parsedValue))
        {
            this.AddToCache(cacheKey, parsedValue);
            return parsedValue;
        }

        return null;
    }

    /// <summary>
    /// Gets the value for the specified key in the specified section as an unsigned integer.
    /// </summary>
    /// <param name="section">The section name in the INI file.</param>
    /// <param name="key">The key name in the section.</param>
    /// <returns>The unsigned integer value if parsed successfully, otherwise null.</returns>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    [return: MaybeNull]
    public uint? GetUInt32(
        string section,
        string key)
    {
        string cacheKey = CreateCacheKey(section, key, "uint32");

        if (this.TryGetCache(cacheKey, out object? cachedValue))
        {
            return (uint?)cachedValue;
        }

        string stringValue = this.GetString(section, key);

        if (stringValue.Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        else if (uint.TryParse(stringValue, out uint parsedValue))
        {
            this.AddToCache(cacheKey, parsedValue);
            return parsedValue;
        }

        return null;
    }

    /// <summary>
    /// Gets the value for the specified key in the specified section as a long.
    /// </summary>
    /// <param name="section">The section name in the INI file.</param>
    /// <param name="key">The key name in the section.</param>
    /// <returns>The long value if parsed successfully, otherwise null.</returns>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    [return: MaybeNull]
    public long? GetInt64(string section, string key)
    {
        string cacheKey = CreateCacheKey(section, key, "int64");

        if (this.TryGetCache(cacheKey, out object? cachedValue))
        {
            return (long?)cachedValue;
        }

        string stringValue = this.GetString(section, key);

        if (stringValue.Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        else if (long.TryParse(stringValue, out long parsedValue))
        {
            this.AddToCache(cacheKey, parsedValue);
            return parsedValue;
        }

        return null;
    }

    /// <summary>
    /// Gets the value for the specified key in the specified section as an unsigned long.
    /// </summary>
    /// <param name="section">The section name in the INI file.</param>
    /// <param name="key">The key name in the section.</param>
    /// <returns>The unsigned long value if parsed successfully, otherwise null.</returns>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    [return: MaybeNull]
    public ulong? GetUInt64(
        string section,
        string key)
    {
        string cacheKey = CreateCacheKey(section, key, "uint64");

        if (this.TryGetCache(cacheKey, out object? cachedValue))
        {
            return (ulong?)cachedValue;
        }

        string stringValue = this.GetString(section, key);

        if (stringValue.Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        else if (ulong.TryParse(stringValue, out ulong parsedValue))
        {
            this.AddToCache(cacheKey, parsedValue);
            return parsedValue;
        }

        return null;
    }

    /// <summary>
    /// Gets the value for the specified key in the specified section as a float.
    /// </summary>
    /// <param name="section">The section name in the INI file.</param>
    /// <param name="key">The key name in the section.</param>
    /// <returns>The float value if parsed successfully, otherwise null.</returns>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    [return: MaybeNull]
    public float? GetSingle(
        string section,
        string key)
    {
        string cacheKey = CreateCacheKey(section, key, "single");

        if (this.TryGetCache(cacheKey, out object? cachedValue))
        {
            return (float?)cachedValue;
        }

        string stringValue = this.GetString(section, key);

        if (stringValue.Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        else if (float.TryParse(
            stringValue, NumberStyles.Float,
            CultureInfo.InvariantCulture, out float parsedValue))
        {
            this.AddToCache(cacheKey, parsedValue);
            return parsedValue;
        }

        return null;
    }

    /// <summary>
    /// Gets the value for the specified key in the specified section as a double.
    /// </summary>
    /// <param name="section">The section name in the INI file.</param>
    /// <param name="key">The key name in the section.</param>
    /// <returns>The double value if parsed successfully, otherwise null.</returns>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    [return: MaybeNull]
    public double? GetDouble(string section, string key)
    {
        string cacheKey = CreateCacheKey(section, key, "double");

        if (this.TryGetCache(cacheKey, out object? cachedValue))
        {
            return (double?)cachedValue;
        }

        string stringValue = this.GetString(section, key);

        if (stringValue.Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        else if (double.TryParse(
            stringValue, NumberStyles.Float,
            CultureInfo.InvariantCulture, out double parsedValue))
        {
            this.AddToCache(cacheKey, parsedValue);
            return parsedValue;
        }

        return null;
    }

    /// <summary>
    /// Gets the value for the specified key in the specified section as a DateTime.
    /// </summary>
    /// <param name="section">The section name in the INI file.</param>
    /// <param name="key">The key name in the section.</param>
    /// <returns>The DateTime value if parsed successfully, otherwise null.</returns>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    [return: MaybeNull]
    public DateTime? GetDateTime(
        string section,
        string key)
    {
        string cacheKey = CreateCacheKey(section, key, "datetime");

        if (this.TryGetCache(cacheKey, out object? cachedValue))
        {
            return (DateTime?)cachedValue;
        }

        string stringValue = this.GetString(section, key);

        if (stringValue.Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        else if (DateTime.TryParse(
            stringValue, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out DateTime parsedValue))
        {
            this.AddToCache(cacheKey, parsedValue);
            return parsedValue;
        }

        return null;
    }

    /// <summary>
    /// Gets the value for the specified key in the specified section as a TimeSpan.
    /// </summary>
    /// <param name="section">The section name in the INI file.</param>
    /// <param name="key">The key name in the section.</param>
    /// <returns>The TimeSpan value if parsed successfully, otherwise null.</returns>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    [return: MaybeNull]
    public TimeSpan? GetTimeSpan(
        string section,
        string key)
    {
        string cacheKey = CreateCacheKey(section, key, "timespan");

        if (this.TryGetCache(cacheKey, out object? cachedValue))
        {
            return (TimeSpan?)cachedValue;
        }

        string stringValue = this.GetString(section, key);

        if (stringValue.Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        else if (TimeSpan.TryParse(
            stringValue,
            CultureInfo.InvariantCulture, out TimeSpan parsedValue))
        {
            this.AddToCache(cacheKey, parsedValue);
            return parsedValue;
        }

        return null;
    }

    /// <summary>
    /// Gets the value for the specified key in the specified section as a Guid.
    /// </summary>
    /// <param name="section">The section name in the INI file.</param>
    /// <param name="key">The key name in the section.</param>
    /// <returns>The Guid value if parsed successfully, otherwise null.</returns>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    [return: MaybeNull]
    public Guid? GetGuid(
        string section,
        string key)
    {
        string cacheKey = CreateCacheKey(section, key, "guid");

        if (this.TryGetCache(cacheKey, out object? cachedValue))
        {
            return (Guid?)cachedValue;
        }

        string stringValue = this.GetString(section, key);

        if (stringValue.Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        else if (Guid.TryParse(stringValue, out Guid parsedValue))
        {
            this.AddToCache(cacheKey, parsedValue);
            return parsedValue;
        }

        return null;
    }

    /// <summary>
    /// Gets the value for the specified key in the specified section as an enum of type <typeparamref name="TEnum"/>.
    /// </summary>
    /// <typeparam name="TEnum">The enum type to parse.</typeparam>
    /// <param name="section">The section name in the INI file.</param>
    /// <param name="key">The key name in the section.</param>
    /// <returns>The enum value if parsed successfully, otherwise null.</returns>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    [return: MaybeNull]
    public TEnum? GetEnum<TEnum>(
        string section,
        string key) where TEnum : struct, Enum
    {
        string cacheKey = CreateCacheKey(section, key, $"enum:{typeof(TEnum).FullName}");

        if (this.TryGetCache(cacheKey, out object? cachedValue))
        {
            return (TEnum?)cachedValue;
        }

        string stringValue = this.GetString(section, key);
        if (string.IsNullOrEmpty(stringValue))
        {
            return null;
        }
        else if (stringValue.Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Try parse name (case-insensitive)
        if (Enum.TryParse(stringValue, true, out TEnum result))
        {
            this.AddToCache(cacheKey, result);
            return result;
        }

        // Try parse numeric value (handles all underlying types)
        try
        {
            object numeric = Convert.ChangeType(stringValue,
                Enum.GetUnderlyingType(typeof(TEnum)),
                CultureInfo.InvariantCulture);

            TEnum boxed = (TEnum)Enum.ToObject(typeof(TEnum), numeric);
            this.AddToCache(cacheKey, boxed);
            return boxed;
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            return null;
        }
    }

    /// <summary>
    /// Forces a write of any pending changes to the file.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Flush()
    {
        if (_isDirty)
        {
            this.WriteFile();
        }
    }

    #endregion Public API

    #region Private Methods

    /// <summary>
    /// Creates a cache key from section, key, and optional type suffix.
    /// </summary>
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string CreateCacheKey(
        string section,
        string key,
        string? typeSuffix = null) => typeSuffix == null ? $"{section}:{key}" : $"{section}:{key}:{typeSuffix}";

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryGetCache(string key, [NotNullWhen(true)] out object? value)
    {
        _fileLock.EnterReadLock();
        try
        {
            return _valueCache.TryGetValue(key, out value);
        }
        finally
        {
            _fileLock.ExitReadLock();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AddToCache(string key, object value)
    {
        _fileLock.EnterWriteLock();
        try
        {
            if (_valueCache.Count >= MaxCacheSize)
            {
                _valueCache.Clear();
            }

            _valueCache[key] = value;
        }
        finally
        {
            _fileLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Formats a value for storage in the INI file.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string FormatValue(object value)
    {
        if (value == null)
        {
            return string.Empty;
        }

        // ToByteArray numeric values with invariant culture for consistency.
        // Sanitize string to prevent INI injection via newlines (SEC-19).
        string result = value switch
        {
            float f => f.ToString("R", CultureInfo.InvariantCulture),
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            decimal m => m.ToString("G", CultureInfo.InvariantCulture),
            DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };

        return result.Replace("\r", "", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
    }

    /// <summary>
    /// Loads the INI file with retry logic for handling file access issues.
    /// </summary>
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void LoadWithRetry()
    {
        const int maxRetries = 3;
        int retryCount = 0;
        bool success = false;

        while (!success && retryCount < maxRetries)
        {
            try
            {
                this.Load();
                success = true;
            }
            catch (IOException)
            {
                retryCount++;
                if (retryCount >= maxRetries)
                {
                    throw;
                }

                // Push exponential backoff delay
                Thread.Sleep(100 * (int)Math.Pow(2, retryCount - 1));
            }
        }
    }

    /// <summary>
    /// Loads the data from the INI file into memory with optimized parsing.
    /// </summary>
    /// <exception cref="IOException">Thrown when file reading fails.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when file access is denied.</exception>
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Load()
    {
        if (!this.ExistsFile)
        {
            return;
        }

        _fileLock.EnterWriteLock();

        StringBuilder pendingComments = new();
        string currentSection = string.Empty;
        Dictionary<string, string> currentSectionData;

        try
        {
            // Clear existing data
            _iniData.Clear();
            _valueCache.Clear();
            _comments.Clear();

            currentSectionData = new(StringComparer.OrdinalIgnoreCase);
            _iniData[currentSection] = currentSectionData;

            // Use a buffered reader for better performance
            using StreamReader reader = new(
                _path, Encoding.UTF8, true, DefaultBufferSize);

            int lineNumber = 0;
            string? line;

            while ((line = reader.ReadLine()) != null)
            {
                lineNumber++;
                string trimmedLine = line.Trim();

                if (string.IsNullOrEmpty(trimmedLine))
                {
                    _ = pendingComments.Clear(); // blank line -> reset comment buffer
                    continue;
                }

                // Skip empty lines or comments
                if (trimmedLine[0] == CommentChar)
                {
                    // Skip separator lines — WriteFile() emits its own framing separators,
                    // so storing these in _comments would cause duplication on each round-trip.
                    ReadOnlySpan<char> commentBody = trimmedLine.AsSpan(1).Trim();
                    if (IsSeparatorLine(commentBody))
                    {
                        continue;
                    }

                    if (pendingComments.Length > 0)
                    {
                        _ = pendingComments.Append('\n');
                    }

                    _ = pendingComments.Append(trimmedLine[1..].Trim()); // remove ';'
                    continue;
                }


                // Process section
                if (trimmedLine.Length >= 2 && trimmedLine[0] == SectionStart && trimmedLine[^1] == SectionEnd)
                {
                    currentSection = trimmedLine[1..^1].Trim();

                    // Validate section name
                    if (pendingComments.Length > 0)
                    {
                        _comments[currentSection] = pendingComments.ToString();
                        _ = pendingComments.Clear();
                    }

                    if (!_iniData.TryGetValue(currentSection, out currentSectionData!))
                    {
                        currentSectionData = new(StringComparer.Ordinal);
                        _iniData[currentSection] = currentSectionData;
                    }

                    continue;
                }

                // Handle key-value pairs with optimized parsing
                int separatorIndex = trimmedLine.IndexOf(KeyValueSeparator, StringComparison.Ordinal);
                if (separatorIndex > 0 && separatorIndex < trimmedLine.Length - 1)
                {
                    string key = trimmedLine[..separatorIndex].Trim();
                    string value = trimmedLine[(separatorIndex + 1)..].Trim();

                    // Detect duplicates ( SEC-20 )
                    if (currentSectionData.ContainsKey(key))
                    {
                        throw new InvalidDataException(
                            $"Duplicate key '{key}' found in section '{currentSection}' at line {lineNumber}.");
                    }

                    currentSectionData[key] = value;

                    // Lưu comment của key này
                    if (pendingComments.Length > 0)
                    {
                        _comments[CreateCacheKey(currentSection, key)] = pendingComments.ToString();
                        _ = pendingComments.Clear();
                    }
                }
            }

            // Store the last read time for file change detection
            _lastFileReadTime = File.GetLastWriteTimeUtc(_path);
            _isDirty = false;
        }
        catch (FileNotFoundException ex)
        {
            throw new IOException($"Configuration file not found: {_path}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new UnauthorizedAccessException($"Access denied to configuration file: {_path}", ex);
        }
        catch (IOException)
        {
            // Re-throw IO exceptions as-is
            throw;
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            throw new IOException($"Error reading configuration file: {_path}", ex);
        }
        finally
        {
            _fileLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Writes the stored comment for <paramref name="commentKey"/> to <paramref name="writer"/>,
    /// emitting one <c>; line</c> per newline segment. Does nothing when no comment is registered.
    /// <para>
    /// For property-level comments (i.e. <paramref name="commentKey"/> contains a <c>':'</c>),
    /// the key name is extracted from <paramref name="commentKey"/> and prepended to the first
    /// comment line as <c>; KeyName: first comment line</c> so readers can immediately see
    /// which setting each comment describes.
    /// </para>
    /// Must only be called from inside a write lock (i.e. from <see cref="WriteFile"/>).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    [SuppressMessage("Style", "IDE0301:Simplify collection initialization", Justification = "<Pending>")]
    [SuppressMessage("Style", "IDE0060:Remove unused parameter", Justification = "<Pending>")]
    private void WriteInlineComment(
        StreamWriter writer,
        string section,
        string commentKey)
    {
        if (!_comments.TryGetValue(commentKey, out string? comment))
        {
            return;
        }

        // Extract key name from "Section:Key" so we can prefix the first comment line.
        // Section-level comments have no ':' -> keyPrefix stays empty.
        string keyPrefix = string.Empty;
        int colonIdx = commentKey.IndexOf(':', StringComparison.Ordinal);
        if (colonIdx >= 0 && colonIdx < commentKey.Length - 1)
        {
            keyPrefix = commentKey[(colonIdx + 1)..];
        }

        // Support multi-line comments embedded via \n in the attribute string.
        // First line gets the "KeyName: " prefix; subsequent lines are indented with spaces
        // to align with the text after the prefix so the block stays readable.
        ReadOnlySpan<char> remaining = MemoryExtensions.AsSpan(comment);
        bool isFirstLine = true;
        while (!remaining.IsEmpty)
        {
            int nl = MemoryExtensions.IndexOf(remaining, '\n');
            ReadOnlySpan<char> segment =
                nl < 0 ? remaining : remaining[..nl];

            writer.Write(CommentChar);
            writer.Write(' ');

            if (isFirstLine && !string.IsNullOrEmpty(keyPrefix))
            {
                // e.g. "; MaxConnectionsPerIp: Max concurrent connections …"
                writer.Write(keyPrefix);
                writer.Write(": ");
                isFirstLine = false;
            }

            writer.WriteLine(MemoryExtensions.Trim(segment).ToString());

            remaining = nl < 0
                ? ReadOnlySpan<char>.Empty
                : remaining[(nl + 1)..];
        }
    }

    /// <summary>
    /// Checks if the file has been modified externally and reloads if necessary.
    /// </summary>
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void CheckFileChanges()
    {
        if (!this.ExistsFile)
        {
            return;
        }

        try
        {
            DateTime lastWriteTime = File.GetLastWriteTimeUtc(_path);
            if (lastWriteTime > _lastFileReadTime)
            {
                this.Load();
            }
        }
        catch (IOException)
        {
            // Ignore file access errors - we'll use the data we have
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> when the comment body (after removing the leading <c>';'</c>)
    /// consists entirely of dash characters, matching the section-separator pattern
    /// emitted by <see cref="WriteFile"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsSeparatorLine(ReadOnlySpan<char> commentBody)
    {
        if (commentBody.IsEmpty)
        {
            return false;
        }

        foreach (char c in commentBody)
        {
            if (c != '-')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Writes the INI data to the file with optimized I/O and error handling.
    /// Uses atomic file replacement to prevent data corruption.
    /// </summary>
    /// <exception cref="IOException">Thrown when file writing fails.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when file access is denied.</exception>
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void WriteFile()
    {
        _fileLock.EnterWriteLock();
        try
        {
            // Re-check under lock to avoid unnecessary writes
            if (!_isDirty)
            {
                return;
            }

            string? tempFileName = null;
            bool committed = false;

            try
            {
                // Ensure directory exists with validation
                string? directory = Path.GetDirectoryName(_path);
                if (string.IsNullOrWhiteSpace(directory))
                {
                    throw new InvalidOperationException(
                        "Cannot write configuration file: invalid directory path.");
                }

                if (!Directory.Exists(directory))
                {
                    try
                    {
                        _ = Directory.CreateDirectory(directory);
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        throw new UnauthorizedAccessException(
                            $"Access denied when creating directory: {directory}", ex);
                    }
                }

                // Write to a temporary file first to prevent corruption.
                // Use a unique name to avoid collisions between multiple processes
                // attempting to write to the same logical configuration file.
                tempFileName = $"{_path}.{Guid.NewGuid():N}.tmp";


                using (StreamWriter writer = new(
                    tempFileName, false, Encoding.UTF8, DefaultBufferSize))
                {
                    bool isFirstSection = true;
                    foreach (KeyValuePair<string, Dictionary<string, string>> section in _iniData)
                    {
                        if (section.Key != string.Empty)
                        {
                            // Blank line between sections (except before the first)
                            if (!isFirstSection)
                            {
                                writer.WriteLine();
                            }

                            string sectionCommentKey = section.Key;
                            bool hasSectionComment = _comments.ContainsKey(sectionCommentKey);

                            // ── Section-level comment lines (wrapped in separators) ──
                            if (hasSectionComment)
                            {
                                writer.WriteLine(s_sectionSeparator);
                                this.WriteInlineComment(writer, section.Key, commentKey: sectionCommentKey);
                                writer.WriteLine(s_sectionSeparator);
                            }

                            // ── [Section] header ─────────────────────────────────────
                            writer.Write(SectionStart);
                            writer.Write(section.Key);
                            writer.WriteLine(SectionEnd);

                            isFirstSection = false;
                        }

                        // Align '=' by padding keys to the longest key in this section
                        int maxKeyLength = 0;
                        foreach (KeyValuePair<string, string> kv in section.Value)
                        {
                            if (kv.Key.Length > maxKeyLength)
                            {
                                maxKeyLength = kv.Key.Length;
                            }
                        }

                        foreach (KeyValuePair<string, string> keyValue in section.Value)
                        {
                            // Property-level comment lines go directly above their key=value line
                            // so Load() can re-associate them with the correct key on round-trip.
                            string commentKey = CreateCacheKey(section.Key, keyValue.Key);

                            // Normalize round-tripped on-disk comments ("Key: ...") back to the stored
                            // form ("...") so WriteInlineComment doesn't double-prefix on each flush.
                            if (_comments.TryGetValue(commentKey, out string? comment)
                                && comment.Length >= keyValue.Key.Length + 2
                                && comment.StartsWith(keyValue.Key, StringComparison.Ordinal)
                                && comment[keyValue.Key.Length] == ':'
                                && comment[keyValue.Key.Length + 1] == ' ')
                            {
                                _comments[commentKey] = comment[(keyValue.Key.Length + 2)..];
                            }

                            this.WriteInlineComment(writer, section.Key, commentKey: commentKey);

                            writer.Write(keyValue.Key.PadRight(maxKeyLength));
                            writer.Write(" = ");
                            writer.WriteLine(keyValue.Value);
                        }
                    }

                    // Ensure all data is written to disk
                    writer.Flush();
                }

                // Atomic file replacement with retry for cross-process synchronization
                int retries = 5;
                int delayMs = 15;
                while (true)
                {
                    try
                    {
                        if (File.Exists(_path))
                        {
                            try
                            {
                                File.Replace(tempFileName, _path, null);
                            }
                            catch (FileNotFoundException)
                            {
                                // Race condition: destination file was deleted between File.Exists and File.Replace.
                                // Fall back to Move.
                                File.Move(tempFileName, _path);
                            }
                        }
                        else
                        {
                            File.Move(tempFileName, _path);
                        }
                        break;
                    }
                    catch (IOException) when (retries > 0)
                    {
                        retries--;
                        Thread.Sleep(delayMs);
                        delayMs *= 2;
                    }
                }

                // Update last write time after our own modification
                _lastFileReadTime = File.GetLastWriteTimeUtc(_path);
                _isDirty = false;
                committed = true;
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new UnauthorizedAccessException($"Access denied when writing configuration file: {_path}", ex);
            }
            catch (PathTooLongException ex)
            {
                throw new IOException($"Configuration file path is too long: {_path}", ex);
            }
            catch (IOException ex) when (ex is not IOException { InnerException: not null })
            {
                throw new IOException($"I/O error writing configuration file: {_path}", ex);
            }
            finally
            {
                // Clean up temp file only if write was not successfully committed
                if (!committed && tempFileName != null && File.Exists(tempFileName))
                {
                    try
                    {
                        File.Delete(tempFileName);
                    }
                    catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
                    {
                        // Ignore cleanup errors
                    }
                }
            }
        }
        finally
        {
            _fileLock.ExitWriteLock();
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _fileLock.Dispose();

    #endregion Private Methods
}
