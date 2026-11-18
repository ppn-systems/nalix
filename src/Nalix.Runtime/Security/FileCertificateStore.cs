// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.IO;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Injection;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Security;
using Nalix.Codec.Security.Asymmetric;
using Nalix.Environment.IO;

namespace Nalix.Runtime.Security;

/// <summary>
/// Implements <see cref="ICertificateStore"/> using the local file system.
/// </summary>
[Injectable(typeof(ICertificateStore))]
public sealed class FileCertificateStore : ICertificateStore
{
    /// <inheritdoc/>
    public Bytes32 Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            // Auto-generate key pair if missing
            X25519.X25519KeyPair key = X25519.GenerateKeyPair();
            this.Save(path, key.PrivateKey, key.PublicKey);
        }

        try
        {
            string? hex = null;
            string[] lines = File.ReadAllLines(path);

            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string line = lines[i];
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                string trimmed = line.Trim();
                if (trimmed.AsSpan().StartsWith('#'))
                {
                    continue;
                }

                hex = trimmed;
                break;
            }

            if (string.IsNullOrWhiteSpace(hex))
            {
                throw new InternalErrorException(
                    $"Handshake failed: No valid certificate data found in '{path}'. Please check file format and content.");
            }

            return Bytes32.Parse(hex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InternalErrorException(
                $"Handshake failed: Access denied while reading server identity from '{path}'. Exception detail: " + ex.Message, ex);
        }
        catch (IOException ex)
        {
            throw new InternalErrorException(
                $"Handshake failed: Unable to read server identity from '{path}'. Exception detail: " + ex.Message, ex);
        }
        catch (FormatException ex)
        {
            throw new InternalErrorException(
                $"Handshake failed: Invalid server identity format in '{path}'. Exception detail: " + ex.Message, ex);
        }
    }

    /// <inheritdoc/>
    public void Save(string path, Bytes32 privateKey, Bytes32 publicKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            _ = Directory.CreateDirectory(directory);
        }

        if (!Directories.TryWriteNewFile(path, privateKey.ToString(), isPrivate: true))
        {
            return;
        }

        string publicPath = Path.Combine(
            directory ?? string.Empty,
            Path.GetFileNameWithoutExtension(path) + ".public");

        _ = Directories.TryWriteNewFile(publicPath, publicKey.ToString(), isPrivate: false);
    }
}
