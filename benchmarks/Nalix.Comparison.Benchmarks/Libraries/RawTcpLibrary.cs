// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Nalix.Comparison.Benchmarks.Harness;

namespace Nalix.Comparison.Benchmarks.Libraries;

/// <summary>
/// Baseline: hand-written length-prefixed echo over raw <see cref="Socket"/>.
/// No framework, no dispatch, no serialization — the practical ceiling for this machine.
/// </summary>
public sealed class RawTcpLibrary : IBenchLibrary
{
    public string Name => "raw-tcp";

    public string Description => "Baseline: raw Socket, 4-byte length prefix echo (no framework)";

    public Task<IAsyncDisposable> StartServerAsync(int port)
    {
        Socket listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, port));
        listener.Listen(512);
        CancellationTokenSource cts = new();
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                Socket s;
                try { s = await listener.AcceptAsync(cts.Token); }
                catch { break; }
                s.NoDelay = true;
                _ = Task.Run(() => ServeAsync(s, cts.Token));
            }
        });
        return Task.FromResult<IAsyncDisposable>(new Stopper(listener, cts));
    }

    private static async Task ServeAsync(Socket s, CancellationToken ct)
    {
        byte[] buf = new byte[64 * 1024];
        try
        {
            while (true)
            {
                if (!await ReadExactAsync(s, buf.AsMemory(0, 4), ct)) break;
                int len = BinaryPrimitives.ReadInt32LittleEndian(buf);
                if (!await ReadExactAsync(s, buf.AsMemory(4, len), ct)) break;
                await s.SendAsync(buf.AsMemory(0, 4 + len), SocketFlags.None, ct);
            }
        }
        catch { /* connection closed */ }
        finally { s.Dispose(); }
    }

    internal static async ValueTask<bool> ReadExactAsync(Socket s, Memory<byte> m, CancellationToken ct)
    {
        while (m.Length > 0)
        {
            int n = await s.ReceiveAsync(m, SocketFlags.None, ct);
            if (n == 0) return false;
            m = m[n..];
        }

        return true;
    }

    public IBenchClient CreateClient(int port) => new Client(port);

    private sealed class Stopper(Socket l, CancellationTokenSource cts) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            cts.Cancel();
            l.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Client(int port) : IBenchClient
    {
        private readonly Socket _s = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        private readonly byte[] _buf = new byte[64 * 1024];

        public Task ConnectAsync() => _s.ConnectAsync(IPAddress.Loopback, port);

        public async ValueTask<int> EchoAsync(byte[] payload)
        {
            BinaryPrimitives.WriteInt32LittleEndian(_buf, payload.Length);
            payload.CopyTo(_buf, 4);
            await _s.SendAsync(_buf.AsMemory(0, 4 + payload.Length), SocketFlags.None);
            await ReadExactAsync(_s, _buf.AsMemory(0, 4), default);
            int len = BinaryPrimitives.ReadInt32LittleEndian(_buf);
            await ReadExactAsync(_s, _buf.AsMemory(4, len), default);
            return len;
        }

        public ValueTask DisposeAsync()
        {
            _s.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
