// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net;
using Nalix.Abstractions;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Codec.DataFrames;
using Nalix.Codec.ProtocolFrames;
using Nalix.Environment.Memory;
using Nalix.Hosting.Protocols;
using Nalix.Network.Protocols;
using Nalix.Runtime.Dispatching;
using Nalix.Runtime.Handlers;
using Nalix.SDK.Options;
using Nalix.SDK.Transport;
using Nalix.Tests.Shared;

namespace Nalix.SDK.Tests.TestSupport;

internal static class TestEnvironment
{
    public static void SetupCertificate()
    {
        string? current = AppDomain.CurrentDomain.BaseDirectory;
        string? certPath = null;

        for (int i = 0; i < 10 && current != null; i++)
        {
            string candidate = Path.Combine(current, "shared", "certificate.private");
            if (File.Exists(candidate))
            {
                certPath = candidate;
                break;
            }
            current = Path.GetDirectoryName(current);
        }

        if (certPath == null)
        {
            // Try absolute fallback
            string fallback = @"e:\Cs\Nalix\shared\certificate.private";
            if (File.Exists(fallback)) certPath = fallback;
        }

        if (certPath != null)
        {
            HandshakeHandlers.SetCertificatePath(certPath);
            return;
        }

        HandshakeHandlers.Initialize();
    }

    /// <summary>
    /// Returns a loopback port that is free for both TCP and UDP, so a server that listens on the same
    /// number for both protocols never collides with another process.
    /// </summary>
    public static int GetFreePort() => TestPorts.Next();

    public static string GetServerPublicKey()
    {
        string? current = AppDomain.CurrentDomain.BaseDirectory;
        string? pubPath = null;

        for (int i = 0; i < 10 && current != null; i++)
        {
            string candidate = Path.Combine(current, "shared", "certificate.public");
            if (File.Exists(candidate))
            {
                pubPath = candidate;
                break;
            }
            current = Path.GetDirectoryName(current);
        }

        if (pubPath == null)
        {
            string fallback = @"e:\Cs\Nalix\shared\certificate.public";
            if (File.Exists(fallback)) pubPath = fallback;
        }

        if (pubPath != null)
        {
            string[] lines = System.IO.File.ReadAllLines(pubPath);
            foreach (string line in lines)
            {
                string trimmed = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#'))
                {
                    continue;
                }
                return trimmed;
            }
        }

        SetupCertificate();
        if (!HandshakeHandlers.ServerPublicKey.IsZero)
        {
            return HandshakeHandlers.ServerPublicKey.ToString();
        }

        throw new System.IO.FileNotFoundException("Public key file not found.");
    }
}
