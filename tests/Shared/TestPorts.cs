// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Nalix.Tests.Shared;

/// <summary>
/// Hands out loopback ports that are free for both TCP and UDP.
/// </summary>
/// <remarks>
/// <c>dotnet test</c> runs several test assemblies at the same time in separate processes. Binding port 0 and
/// releasing it lets two processes receive the same number (or a number whose UDP side is taken), and a client may then
/// talk to the other process's server. Each assembly therefore draws from its own slice of a fixed range and only
/// returns ports that are currently free on both protocols. This file is linked into every test project that opens
/// sockets (see <c>tests/Directory.Build.props</c>); it is not a test project of its own.
/// </remarks>
internal static class TestPorts
{
    private const int RangeStart = 20000;
    private const int SliceSize = 1000;
    private const int SliceCount = 10;

    private static readonly Lock s_gate = new();
    private static readonly int s_sliceStart = RangeStart + (ComputeSlice() * SliceSize);
    private static int s_cursor = Random.Shared.Next(0, SliceSize);

    /// <summary>
    /// Returns a port that is free for TCP and UDP and unique to this assembly's slice.
    /// </summary>
    public static int Next()
    {
        lock (s_gate)
        {
            for (int attempt = 0; attempt < SliceSize; attempt++)
            {
                int port = s_sliceStart + (s_cursor++ % SliceSize);
                if (IsFree(port))
                {
                    return port;
                }
            }
        }

        throw new InvalidOperationException("Could not find a port that is free for both TCP and UDP.");
    }

    private static int ComputeSlice()
    {
        string name = typeof(TestPorts).Assembly.GetName().Name ?? string.Empty;
        return name switch
        {
            "Nalix.Network.Tests" => 0,
            "Nalix.SDK.Tests" => 1,
            "Nalix.Hosting.Tests" => 2,
            "Nalix.Integration.Tests" => 3,
            "Nalix.Runtime.Tests" => 4,
            _ => 5 + (Math.Abs(name.GetHashCode(StringComparison.Ordinal)) % (SliceCount - 5)),
        };
    }

    private static bool IsFree(int port)
    {
        try
        {
            using Socket tcp = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            tcp.Bind(new IPEndPoint(IPAddress.Any, port));

            using Socket udp = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            udp.Bind(new IPEndPoint(IPAddress.Any, port));

            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
