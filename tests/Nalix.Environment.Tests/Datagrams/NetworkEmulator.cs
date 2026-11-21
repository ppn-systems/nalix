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
/// Deterministic network between a client and a server <see cref="DatagramEngine"/>: random and burst loss,
/// duplication, reordering by delay jitter, a bandwidth cap with a drop-tail queue, an MTU black hole, NAT
/// rebinding and injected (spoofed) datagrams, on a simulated clock.
/// </summary>
internal sealed class NetworkEmulator : IDisposable
{
    public const ulong ConnectionId = 0x1122334455667788UL;

    private readonly System.Random _random;
    private readonly PriorityQueue<InFlight, (long, long)> _wire = new();
    private readonly byte[] _buffer = new byte[DatagramEngine.MaxDatagramSize];
    private long _sequence;
    private long _linkFreeAt;
    private bool _inBurst;

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

    /// <summary>
    /// Gets or sets the chance, per datagram, that a loss burst starts (Gilbert–Elliott model, as on Wi-Fi and
    /// mobile links); 0 disables bursts.
    /// </summary>
    public double BurstStart { get; set; }

    /// <summary>Gets or sets the chance, per datagram, that a loss burst ends.</summary>
    public double BurstEnd { get; set; } = 0.3;

    /// <summary>Gets or sets the share of datagrams lost during a burst.</summary>
    public double BurstLoss { get; set; } = 1.0;

    /// <summary>
    /// Gets or sets the bytes the bottleneck queue holds when <see cref="Bandwidth"/> is set; datagrams arriving
    /// at a full queue are dropped (drop-tail). 0 means unbounded.
    /// </summary>
    public long QueueBytes { get; set; }

    /// <summary>Gets the client datagrams the network dropped.</summary>
    public long ClientDropped { get; private set; }

    /// <summary>Gets the server datagrams the network dropped.</summary>
    public long ServerDropped { get; private set; }

    /// <summary>Gets the simulated send time of every datagram in <see cref="ClientSent"/>.</summary>
    public List<long> ClientSendTimes { get; } = [];

    /// <summary>Gets the datagrams the server refused (<see cref="DatagramEngine.Receive"/> returned false).</summary>
    public List<(long Time, byte[] Data)> ServerRejected { get; } = [];

    /// <summary>Gets the bytes the server sent to each address it sent to.</summary>
    public Dictionary<SocketAddress, long> ServerBytesTo { get; } = [];

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
                if (!this.Server.Receive(datagram.Data, datagram.From, this.Now))
                {
                    this.ServerRejected.Add((this.Now, datagram.Data));
                }
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

    /// <summary>
    /// Puts <paramref name="data"/> on the wire as if sent from <paramref name="from"/>, arriving after
    /// <paramref name="delay"/> microseconds: an off-path attacker replaying or racing a datagram it observed.
    /// </summary>
    public void Inject(byte[] data, SocketAddress from, long delay)
        => _wire.Enqueue(new InFlight(data, from, ToServer: true, ToClientAddress: null), (this.Now + delay, _sequence++));

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
                this.ClientSendTimes.Add(this.Now);
            }
            else
            {
                SocketAddress to = path ?? this.Server.ActivePath ?? this.ClientAddress;
                this.ServerBytesTo[to] = this.ServerBytesTo.GetValueOrDefault(to) + length;
            }

            if (this.BurstStart > 0)
            {
                _inBurst = _inBurst ? _random.NextDouble() >= this.BurstEnd : _random.NextDouble() < this.BurstStart;
            }

            bool queueFull = this.Bandwidth > 0 && this.QueueBytes > 0 &&
                (_linkFreeAt - this.Now) * this.Bandwidth / 1_000_000 + length > this.QueueBytes;

            if (!this.Connected || length > this.PathMtu || queueFull || _random.NextDouble() < this.LossRate ||
                (_inBurst && _random.NextDouble() < this.BurstLoss) || (this.Drop?.Invoke(fromClient, data) ?? false))
            {
                if (fromClient)
                {
                    this.ClientDropped++;
                }
                else
                {
                    this.ServerDropped++;
                }

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
