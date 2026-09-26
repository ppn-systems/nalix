using System;
using System.Net;
using System.Net.Sockets;
using BenchmarkDotNet.Attributes;
using Nalix.Abstractions.Identity;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Environment.Configuration;
using Nalix.Network.Connections;
using Nalix.Network.Options;
using Nalix.Benchmarks.Shared;

namespace Nalix.Network.Benchmarks.Connections;

[Config(typeof(NalixBenchmarkConfig))]
public class ConnectionHubBenchmarks
{
    private Socket _listener = null!;
    private Socket _client = null!;
    private Socket _serverSocket = null!;
    private ConnectionHub _hub = null!;
    private Connection _preRegisteredConnection = null!;
    private Connection _benchmarkConnection = null!;
    private ulong _preRegisteredId;

    /// <summary>
    /// Minimal op-code reader. The hub never inspects a payload, so a local stub keeps this
    /// benchmark from pulling in Nalix.Hosting just for DefaultOpCodeExtractor.
    /// </summary>
    private sealed class StubOpCodeExtractor : IOpCodeExtractor
    {
        public ushort Extract(ReadOnlySpan<byte> payload) => 0;
    }

    [GlobalSetup]
    public void Setup()
    {
        // ConnectionHubOptions no longer caps the connection count, so only the shard count is
        // pinned here to keep the measurement stable across machines.
        var options = ConfigurationManager.Instance.Get<ConnectionHubOptions>();
        options.ShardCount = 8;

        _hub = new ConnectionHub();

        // Setup real connected loopback sockets
        _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _listener.Listen(1);

        _client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var acceptTask = _listener.AcceptAsync();
        _client.Connect(_listener.LocalEndPoint!);
        _serverSocket = acceptTask.GetAwaiter().GetResult();

        StubOpCodeExtractor packetClassifier = new();

        _preRegisteredConnection = new Connection(_serverSocket, packetClassifier);
        _preRegisteredId = _preRegisteredConnection.ConnectionId;
        _hub.RegisterConnection(_preRegisteredConnection);

        _benchmarkConnection = new Connection(_serverSocket, packetClassifier);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _preRegisteredConnection.Dispose();
        _benchmarkConnection.Dispose();
        _hub.Dispose();
        _serverSocket.Dispose();
        _client.Dispose();
        _listener.Dispose();
    }

    [Benchmark]
    public void RegisterAndUnregister()
    {
        _hub.RegisterConnection(_benchmarkConnection);
        _hub.UnregisterConnection(_benchmarkConnection);
    }

    [Benchmark]
    public IConnection? GetConnection()
    {
        return _hub.GetConnection(_preRegisteredId);
    }

}
