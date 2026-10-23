using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using FluentAssertions;
using Nalix.Network.Internal.Pooling;
using Xunit;

namespace Nalix.Network.Tests;

[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
public sealed class PooledSocketReceiveContextTests
{
    [Fact]
    public async Task ReceiveAsync_AsyncCompletion_ReusesManualResetValueTaskSourceAcrossOperations()
    {
        using Socket listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);

        int port = ((IPEndPoint)listener.LocalEndPoint!).Port;

        Task<Socket> acceptTask = Task.Run(() => listener.Accept());

        using Socket client = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(IPAddress.Loopback, port);

        using Socket server = await acceptTask;
        using PooledSocketReceiveContext receiveContext = new();
        receiveContext.EnsureArgsBound();

        byte[] buffer = new byte[16];
        byte[] firstPayload = [1, 2, 3, 4];
        byte[] secondPayload = [5, 6];

        ValueTask<int> firstReceive = receiveContext.ReceiveAsync(server, buffer, 0, buffer.Length);
        _ = firstReceive.IsCompleted.Should().BeFalse();

        _ = await client.SendAsync(firstPayload, SocketFlags.None);

        int firstRead = await firstReceive;
        _ = firstRead.Should().Be(firstPayload.Length);
        _ = buffer[0..firstRead].Should().Equal(firstPayload);

        ValueTask<int> secondReceive = receiveContext.ReceiveAsync(server, buffer, 0, buffer.Length);
        _ = secondReceive.IsCompleted.Should().BeFalse();

        _ = await client.SendAsync(secondPayload, SocketFlags.None);

        int secondRead = await secondReceive;
        _ = secondRead.Should().Be(secondPayload.Length);
        _ = buffer[0..secondRead].Should().Equal(secondPayload);
    }
}















