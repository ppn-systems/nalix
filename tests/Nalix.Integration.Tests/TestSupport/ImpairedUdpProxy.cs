// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Nalix.Integration.Tests.TestSupport;

/// <summary>
/// A UDP relay between a client and a server on real sockets that impairs the traffic like a real network:
/// random and burst loss, delay with jitter (which reorders), duplication, a path MTU, blackouts, and NAT
/// rebinding (<see cref="Rebind"/>: the server starts seeing the client from another port).
/// </summary>
/// <remarks>
/// The client sends to <see cref="Port"/>. The proxy forwards to the server from its own "NAT" socket, so the
/// server sees the proxy's address, and forwards what comes back to the last client address it heard from.
/// Every impairment applies to both directions.
/// </remarks>
internal sealed class ImpairedUdpProxy : IDisposable
{
    private readonly IPEndPoint _server;
    private readonly Socket _front;
    private readonly System.Random _random;
    private readonly PriorityQueue<(byte[] Data, bool ToServer), long> _queue = new();
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<Task> _loops = [];
    private Socket _nat;
    private IPEndPoint? _client;
    private bool _inBurst;
    private long _forwarded;
    private long _dropped;

    public ImpairedUdpProxy(IPEndPoint server, int seed)
    {
        _server = server;
        _random = new System.Random(seed);
        _front = Bind();
        _nat = Bind();

        _loops.Add(Task.Run(() => this.ReceiveLoopAsync(_front, toServer: true)));
        _loops.Add(Task.Run(() => this.ReceiveLoopAsync(_nat, toServer: false)));
        _loops.Add(Task.Run(this.DeliveryLoopAsync));
    }

    /// <summary>Gets the port the client sends to.</summary>
    public ushort Port => (ushort)((IPEndPoint)_front.LocalEndPoint!).Port;

    /// <summary>Gets the port the server currently sees the client on.</summary>
    public ushort NatPort => (ushort)((IPEndPoint)Volatile.Read(ref _nat).LocalEndPoint!).Port;

    public double LossRate { get; set; }

    /// <summary>Gets or sets the chance per datagram that a loss burst starts (Gilbert–Elliott).</summary>
    public double BurstStart { get; set; }

    /// <summary>Gets or sets the chance per datagram that a loss burst ends.</summary>
    public double BurstEnd { get; set; } = 0.3;

    public double DuplicateRate { get; set; }

    /// <summary>Gets or sets the one-way delay.</summary>
    public TimeSpan Delay { get; set; }

    /// <summary>Gets or sets the extra random one-way delay; reorders datagrams.</summary>
    public TimeSpan Jitter { get; set; }

    /// <summary>Gets or sets the largest datagram forwarded; larger ones vanish (an MTU black hole).</summary>
    public int MaxDatagramSize { get; set; } = 65_507;

    /// <summary>Gets or sets a value indicating whether anything is forwarded (false: a blackout).</summary>
    public bool Connected { get; set; } = true;

    public long Forwarded => Interlocked.Read(ref _forwarded);

    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>
    /// Replaces the socket that talks to the server, as a NAT does when its mapping expires: from now on the
    /// server sees the client on another port, and what it sends to the old one is lost.
    /// </summary>
    public void Rebind()
    {
        Socket fresh = Bind();
        Socket old = Interlocked.Exchange(ref _nat, fresh);
        old.Dispose();
        lock (_lock)
        {
            _loops.Add(Task.Run(() => this.ReceiveLoopAsync(fresh, toServer: false)));
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _front.Dispose();
        Volatile.Read(ref _nat).Dispose();
        _ = _wake.Release();
        try
        {
            _ = Task.WaitAll([.. _loops], TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }

        _wake.Dispose();
        _stop.Dispose();
    }

    private static Socket Bind()
    {
        Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return socket;
    }

    private async Task ReceiveLoopAsync(Socket socket, bool toServer)
    {
        byte[] buffer = new byte[65_536];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);

        while (!_stop.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, _stop.Token).ConfigureAwait(false);
            }
            catch (SocketException) when (!_stop.IsCancellationRequested && ReferenceEquals(socket, _front))
            {
                continue; // An ICMP error reported on the receive side (Windows): keep relaying.
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return; // Stopped, or a NAT socket replaced by Rebind.
            }

            if (toServer)
            {
                Volatile.Write(ref _client, (IPEndPoint)result.RemoteEndPoint);
            }

            this.Admit(buffer.AsSpan(0, result.ReceivedBytes).ToArray(), toServer);
        }
    }

    /// <summary>Decides the fate of one datagram and queues its copies for delivery.</summary>
    private void Admit(byte[] data, bool toServer)
    {
        lock (_lock)
        {
            if (this.BurstStart > 0)
            {
                _inBurst = _inBurst ? _random.NextDouble() >= this.BurstEnd : _random.NextDouble() < this.BurstStart;
            }

            if (!this.Connected || data.Length > this.MaxDatagramSize || _inBurst || _random.NextDouble() < this.LossRate)
            {
                _ = Interlocked.Increment(ref _dropped);
                return;
            }

            int copies = _random.NextDouble() < this.DuplicateRate ? 2 : 1;
            for (int i = 0; i < copies; i++)
            {
                long jitter = this.Jitter > TimeSpan.Zero ? _random.NextInt64(this.Jitter.Ticks) : 0;
                long due = _clock.Elapsed.Ticks + this.Delay.Ticks + jitter;
                _queue.Enqueue((data, toServer), due);
            }
        }

        _ = _wake.Release();
    }

    private async Task DeliveryLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            (byte[] Data, bool ToServer) next = default;
            long wait;
            lock (_lock)
            {
                if (!_queue.TryPeek(out next, out long due))
                {
                    wait = -1;
                }
                else
                {
                    wait = Math.Max(0, due - _clock.Elapsed.Ticks);
                    if (wait == 0)
                    {
                        _ = _queue.Dequeue();
                    }
                }
            }

            if (wait != 0)
            {
                try
                {
                    _ = await _wake.WaitAsync(wait < 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromTicks(wait), _stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            try
            {
                if (next.ToServer)
                {
                    _ = Volatile.Read(ref _nat).SendTo(next.Data, _server);
                }
                else if (Volatile.Read(ref _client) is { } client)
                {
                    _ = _front.SendTo(next.Data, client);
                }

                _ = Interlocked.Increment(ref _forwarded);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or SocketException)
            {
                // The NAT socket was just replaced, or the peer is gone: the datagram is lost, as on a real path.
            }
        }
    }
}
