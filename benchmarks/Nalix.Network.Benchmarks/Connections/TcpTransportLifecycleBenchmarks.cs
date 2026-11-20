using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Benchmarks.Shared;
using Nalix.Network.Connections;

namespace Nalix.Network.Benchmarks.Connections;

/// <summary>
/// Cost of the TCP transport lifecycle: creating and disposing a <see cref="Connection"/>, and the steady-state
/// send path through <see cref="Connection.Transport"/> (which must stay allocation-free per send).
/// </summary>
[Config(typeof(NalixBenchmarkConfig))]
public class TcpTransportLifecycleBenchmarks
{
    private static readonly byte[] Payload = new byte[64];

    private Socket _listener = null!;
    private Socket _client = null!;
    private Socket _serverSocket = null!;
    private Connection _sendConnection = null!;
    private CancellationTokenSource _drainCts = null!;
    private Task _drainTask = null!;

    private sealed class StubOpCodeExtractor : IOpCodeExtractor
    {
        public ushort Extract(ReadOnlySpan<byte> payload) => 0;
    }

    [GlobalSetup]
    public void Setup()
    {
        try { Nalix.Framework.Memory.Objects.ObjectPoolManager.Configure(new Nalix.Framework.Memory.Objects.ObjectPoolManager()); } catch (InvalidOperationException) { }

        _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _listener.Listen(1);

        _client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        Task<Socket> acceptTask = _listener.AcceptAsync();
        _client.Connect(_listener.LocalEndPoint!);
        _serverSocket = acceptTask.GetAwaiter().GetResult();

        _sendConnection = new Connection(_serverSocket, new StubOpCodeExtractor());

        // Keep the peer drained so the steady-state send never blocks on a full socket buffer.
        _drainCts = new CancellationTokenSource();
        Socket peer = _client;
        CancellationToken token = _drainCts.Token;
        _drainTask = Task.Run(() =>
        {
            byte[] buffer = new byte[64 * 1024];
            try
            {
                while (!token.IsCancellationRequested)
                {
                    if (peer.Receive(buffer) == 0)
                    {
                        break;
                    }
                }
            }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        });
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _drainCts.Cancel();
        _sendConnection.Dispose();
        _client.Dispose();
        _listener.Dispose();
        try { _drainTask.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
    }

    /// <summary>Creates a connection over a fresh socket and disposes it, as a new client session does.</summary>
    [Benchmark]
    public void CreateAndDispose_Connection()
    {
        using Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        Connection connection = new(socket, new StubOpCodeExtractor(), new IPEndPoint(IPAddress.Loopback, 0));
        connection.Dispose();
    }

    /// <summary>
    /// Churn: 256 TCP connections are accepted, wrapped in a <see cref="Connection"/> and closed per invocation,
    /// so Allocated/op is bytes per connection and the Gen0/Gen1/Gen2 columns are collections per 1000 connections.
    /// </summary>
    [Benchmark(OperationsPerInvoke = ChurnBatch)]
    public void Churn_TcpConnections()
    {
        for (int i = 0; i < ChurnBatch; i++)
        {
            Socket client = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            Task<Socket> accept = _listener.AcceptAsync();
            client.Connect(_listener.LocalEndPoint!);
            Socket server = accept.GetAwaiter().GetResult();

            Connection connection = new(server, new StubOpCodeExtractor());
            connection.Dispose();
            client.Dispose();
        }
    }

    private const int ChurnBatch = 256;

    [Benchmark]
    public void Send_Steady()
    {
        _sendConnection.Transport.Send(Payload);
    }

    [Benchmark]
    public ValueTask SendAsync_Steady()
    {
        return _sendConnection.Transport.SendAsync(Payload);
    }
}
