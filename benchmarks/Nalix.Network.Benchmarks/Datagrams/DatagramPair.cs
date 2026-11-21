using System.Net;
using Nalix.Abstractions.Networking.Datagrams;
using Nalix.Abstractions.Primitives;
using Nalix.Codec.Security.Datagram;
using Nalix.Environment.Datagrams;
using Nalix.Environment.Memory;
using Nalix.Environment.Options;

namespace Nalix.Network.Benchmarks.Datagrams;

/// <summary>
/// A client and a server engine wired back to back in memory, with a virtual clock: what the benchmarks measure is
/// the protocol work (framing, AEAD, ACKs, reliability, fragmentation), not sockets.
/// </summary>
internal sealed class DatagramPair : IDisposable
{
    private const ulong ConnectionId = 0x1122334455667788;

    private readonly byte[] _buffer = new byte[DatagramEngine.MaxDatagramSize];
    private readonly SocketAddress _clientAddress = new IPEndPoint(IPAddress.Loopback, 40000).Serialize();
    private long _expectedServerMessages;

    public DatagramPair(DatagramEngineOptions? options = null)
    {
        options ??= new DatagramEngineOptions();
        byte[] secretBytes = new byte[32];
        for (int i = 0; i < secretBytes.Length; i++)
        {
            secretBytes[i] = (byte)(i + 3);
        }

        Bytes32 secret = new(secretBytes);
        byte[] nonce = new byte[16];

        this.Client = new DatagramEngine(
            options, DatagramPeerLimits.From(options),
            ChaCha20Poly1305DatagramProtection.Create(secret, ConnectionId, nonce, isServer: false),
            ConnectionId, isServer: false, this.ClientHandler, this.Now);
        this.Server = new DatagramEngine(
            options, DatagramPeerLimits.From(options),
            ChaCha20Poly1305DatagramProtection.Create(secret, ConnectionId, nonce, isServer: true),
            ConnectionId, isServer: true, this.ServerHandler, this.Now);

        this.Pump();
        if (this.Server.State != DatagramEngineState.Established || this.Client.State != DatagramEngineState.Established)
        {
            throw new InvalidOperationException("The pair did not establish.");
        }
    }

    public DatagramEngine Client { get; }

    public DatagramEngine Server { get; }

    public CountingHandler ClientHandler { get; } = new();

    public CountingHandler ServerHandler { get; } = new();

    /// <summary>Gets the virtual time in microseconds.</summary>
    public long Now { get; private set; } = 1_000_000;

    /// <summary>
    /// Moves datagrams both ways and fires due timers until the server has delivered
    /// <paramref name="serverMessages"/> more messages and nothing is left unacknowledged. The clock advances
    /// 100 µs per round so pacing and the ACK delay make progress.
    /// </summary>
    public void Pump(long serverMessages = 0)
    {
        _expectedServerMessages = this.ServerHandler.Messages + serverMessages;

        for (int round = 0; round < 100_000; round++)
        {
            int moved = this.Move(this.Client, this.Server, _clientAddress) + this.Move(this.Server, this.Client, null);
            if (moved == 0)
            {
                if (!this.HasWorkPending())
                {
                    return;
                }

                long next = Math.Min(this.Client.NextTimer, this.Server.NextTimer);
                this.Now = next == long.MaxValue ? this.Now + 100 : Math.Max(this.Now + 100, next);
            }
            else
            {
                this.Now += 100;
            }

            this.Client.OnTimer(this.Now);
            this.Server.OnTimer(this.Now);
        }

        throw new InvalidOperationException("The pair did not go idle.");
    }

    public void Dispose()
    {
        this.Client.Dispose();
        this.Server.Dispose();
    }

    // Messages still queued (pacing holds them back with nothing in flight) or unacknowledged data on either side.
    private bool HasWorkPending()
        => this.ServerHandler.Messages < _expectedServerMessages || this.Client.BytesInFlight > 0 || this.Server.BytesInFlight > 0;

    private int Move(DatagramEngine from, DatagramEngine to, SocketAddress? source)
    {
        int moved = 0;
        int length;
        while ((length = from.Poll(_buffer, this.Now, out _)) > 0)
        {
            _ = to.Receive(_buffer.AsSpan(0, length), source, this.Now);
            moved++;
        }

        return moved;
    }

    /// <summary>Counts and releases what an engine delivers.</summary>
    internal sealed class CountingHandler : IDatagramEngineHandler
    {
        public long Messages { get; private set; }

        public void OnMessage(BufferLease message, DeliveryMethod method, byte channel)
        {
            this.Messages++;
            message.Dispose();
        }

        public void OnClosed(DatagramCloseCode code, ulong applicationCode, string reason, bool remote)
        {
        }
    }
}
