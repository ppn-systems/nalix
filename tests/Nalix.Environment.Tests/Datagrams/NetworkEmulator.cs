// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Net;
using Nalix.Abstractions.Networking.Datagrams;
using Nalix.Abstractions.Primitives;
using Nalix.Codec.Security.Datagram;
using Nalix.Environment.Datagrams;
using Nalix.Environment.Memory;
using Nalix.Environment.Options;

namespace Nalix.Environment.Tests.Datagrams;

/// <summary>
/// Deterministic network between a client and a server <see cref="DatagramEngine"/>: loss, duplication,
/// reordering by delay jitter, a bandwidth cap, an MTU black hole and NAT rebinding, on a simulated clock.
/// </summary>
internal sealed class NetworkEmulator : IDisposable
{
    public const ulong ConnectionId = 0x1122334455667788UL;

    private readonly System.Random _random;
    private readonly PriorityQueue<InFlight, (long, long)> _wire = new();
    private readonly byte[] _buffer = new byte[DatagramEngine.MaxDatagramSize];
    private long _sequence;
    private long _linkFreeAt;

    public NetworkEmulator(int seed, DatagramEngineOptions? clientOptions = null, DatagramEngineOptions? serverOptions = null)
    {
        _random = new System.Random(seed);
        clientOptions ??= new DatagramEngineOptions();
        serverOptions ??= new DatagramEngineOptions();

        Bytes32 secret = new(Sequence(3, 32));
        byte[] nonce = Sequence(9, 16);

        this.ClientHandler = new RecordingHandler();
        this.ServerHandler = new RecordingHandler();
        this.Client = new DatagramEngine(
            clientOptions, DatagramPeerLimits.From(serverOptions),
            ChaCha20Poly1305DatagramProtection.Create(secret, ConnectionId, nonce, isServer: false),
            ConnectionId, isServer: false, this.ClientHandler, this.Now);
        this.Server = new DatagramEngine(
            serverOptions, DatagramPeerLimits.From(clientOptions),
            ChaCha20Poly1305DatagramProtection.Create(secret, ConnectionId, nonce, isServer: true),
            ConnectionId, isServer: true, this.ServerHandler, this.Now);

        this.ClientAddress = new IPEndPoint(IPAddress.Parse("10.0.0.2"), 40000).Serialize();
    }

    public DatagramEngine Client { get; }

    public DatagramEngine Server { get; }

    public RecordingHandler ClientHandler { get; }

    public RecordingHandler ServerHandler { get; }

    /// <summary>Gets the current simulated time in microseconds.</summary>
    public long Now { get; private set; } = 1_000_000;

    /// <summary>Gets or sets the client's address as seen by the server (change it to emulate NAT rebinding).</summary>
    public SocketAddress ClientAddress { get; set; }

    public double LossRate { get; set; }

    public double DuplicateRate { get; set; }

    /// <summary>Gets or sets the one-way delay in microseconds.</summary>
    public long Delay { get; set; } = 20_000;

    /// <summary>Gets or sets the extra random delay in microseconds; non-zero values reorder datagrams.</summary>
    public long Jitter { get; set; }

    /// <summary>Gets or sets the link rate in bytes per second; 0 means unlimited.</summary>
    public long Bandwidth { get; set; }

    /// <summary>Gets or sets the largest datagram the path carries; larger ones vanish (MTU black hole).</summary>
    public int PathMtu { get; set; } = 1500;

    /// <summary>Gets or sets a filter that drops a datagram when it returns <see langword="true"/>.</summary>
    public Func<bool, byte[], bool>? Drop { get; set; }

    /// <summary>Gets or sets a value indicating whether the network delivers anything.</summary>
    public bool Connected { get; set; } = true;

    /// <summary>Gets every datagram the client sent, for replay tests.</summary>
    public List<byte[]> ClientSent { get; } = [];

    /// <summary>Gets or sets a hook run after every step, for invariants.</summary>
    public Action? AfterStep { get; set; }

    /// <summary>
    /// Runs the simulation until <paramref name="done"/> holds or <paramref name="maxSimulatedMicros"/> passes.
    /// </summary>
    /// <returns><see langword="true"/> when <paramref name="done"/> held.</returns>
    public bool RunUntil(Func<bool> done, long maxSimulatedMicros = 120_000_000)
    {
        long end = this.Now + maxSimulatedMicros;
        long lastNow = this.Now;
        int stuck = 0;
        while (!done())
        {
            if (this.Now > end)
            {
                return false;
            }

            this.Step();

            stuck = this.Now == lastNow ? stuck + 1 : 0;
            lastNow = this.Now;
            if (stuck > 100_000)
            {
                throw new InvalidOperationException(
                    $"Simulation stuck at {this.Now}: client timer {this.Client.NextTimer} state {this.Client.State}, " +
                    $"server timer {this.Server.NextTimer} state {this.Server.State}, wire {_wire.Count}.");
            }
        }

        return true;
    }

    /// <summary>Sends what both engines produce, then advances to the next delivery or timer and runs it.</summary>
    public void Step()
    {
        this.Flush(this.Client, fromClient: true);
        this.Flush(this.Server, fromClient: false);

        long next = Math.Min(this.Client.NextTimer, this.Server.NextTimer);
        if (_wire.TryPeek(out _, out (long Time, long Sequence) head))
        {
            next = Math.Min(next, head.Time);
        }

        if (next == long.MaxValue)
        {
            this.Now += 1_000_000;
            return;
        }

        this.Now = Math.Max(this.Now, next);

        while (_wire.TryPeek(out InFlight datagram, out (long Time, long Sequence) at) && at.Time <= this.Now)
        {
            _ = _wire.Dequeue();
            if (datagram.ToServer)
            {
                _ = this.Server.Receive(datagram.Data, datagram.From, this.Now);
            }
            else if (datagram.ToClientAddress is null || datagram.ToClientAddress.Equals(this.ClientAddress))
            {
                _ = this.Client.Receive(datagram.Data, from: null, this.Now);
            }
        }

        if (this.Client.NextTimer <= this.Now)
        {
            this.Client.OnTimer(this.Now);
        }

        if (this.Server.NextTimer <= this.Now)
        {
            this.Server.OnTimer(this.Now);
        }

        this.AfterStep?.Invoke();
    }

    public void Dispose()
    {
        this.Client.Dispose();
        this.Server.Dispose();
        this.ClientHandler.Dispose();
        this.ServerHandler.Dispose();
    }

    private void Flush(DatagramEngine engine, bool fromClient)
    {
        int length;
        while ((length = engine.Poll(_buffer, this.Now, out SocketAddress? path)) > 0)
        {
            byte[] data = _buffer.AsSpan(0, length).ToArray();
            if (fromClient)
            {
                this.ClientSent.Add(data);
            }

            if (!this.Connected || length > this.PathMtu || _random.NextDouble() < this.LossRate || (this.Drop?.Invoke(fromClient, data) ?? false))
            {
                continue;
            }

            long departure = this.Now;
            if (this.Bandwidth > 0)
            {
                departure = Math.Max(departure, _linkFreeAt);
                _linkFreeAt = departure + (length * 1_000_000L / this.Bandwidth);
            }

            int copies = _random.NextDouble() < this.DuplicateRate ? 2 : 1;
            for (int i = 0; i < copies; i++)
            {
                long arrival = departure + this.Delay + (this.Jitter > 0 ? _random.NextInt64(this.Jitter) : 0);
                InFlight item = new(data, fromClient ? this.ClientAddress : null, fromClient, fromClient ? null : path ?? this.Server.ActivePath);
                _wire.Enqueue(item, (arrival, _sequence++));
            }
        }
    }

    public static byte[] Sequence(byte start, int length)
    {
        byte[] bytes = new byte[length];
        for (int i = 0; i < length; i++)
        {
            bytes[i] = (byte)(start + i);
        }

        return bytes;
    }

    private readonly record struct InFlight(byte[] Data, SocketAddress? From, bool ToServer, SocketAddress? ToClientAddress);

    /// <summary>Collects what an engine delivers.</summary>
    internal sealed class RecordingHandler : IDatagramEngineHandler, IDisposable
    {
        public List<(byte[] Data, DeliveryMethod Method, byte Channel)> Messages { get; } = [];

        public (DatagramCloseCode Code, ulong ApplicationCode, string Reason, bool Remote)? Closed { get; private set; }

        public int CloseCount { get; private set; }

        public void OnMessage(BufferLease message, DeliveryMethod method, byte channel)
        {
            this.Messages.Add((message.Span.ToArray(), method, channel));
            message.Dispose();
        }

        public void OnClosed(DatagramCloseCode code, ulong applicationCode, string reason, bool remote)
        {
            this.Closed = (code, applicationCode, reason, remote);
            this.CloseCount++;
        }

        public void Dispose() => this.Messages.Clear();
    }
}
