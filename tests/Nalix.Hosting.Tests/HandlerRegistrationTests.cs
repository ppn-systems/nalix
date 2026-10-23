using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Hosting;
using Nalix.Hosting.Tests.HandlerScan;
using Nalix.Runtime.Dispatching;
using Nalix.SDK.Options;
using Nalix.SDK.Transport;

namespace Nalix.Hosting.Tests;

[Collection(TestCollections.RealServer)]
public sealed class HandlerRegistrationTests : IDisposable
{
    public HandlerRegistrationTests()
    {
        TestAssemblySetup.EnsureHighLimits();
        if (!Nalix.Codec.DataFrames.PacketRegistry.IsBuilt)
        {
            Nalix.Codec.DataFrames.PacketRegistry.Build();
        }
    }

    [PacketHandler("HandlerScan")]
    [Nalix.Abstractions.Injection.Injectable]
    public sealed class HandlerScanController
    {
        public static int ReceivedCount;

        [PacketOpcode(8888)]
        public void Handle(IPacketContext<HandlerScanPacket> context) => Interlocked.Increment(ref ReceivedCount);
    }

    [Fact]
    public async Task MapHandlers_RegisteredHandler_ReceivesDispatchedPacket()
    {
        HandlerScanController.ReceivedCount = 0;
        int port = TestUtils.GetFreePort();

        var builder = NetworkApplication.CreateBuilder();
        builder.ListenTcp<TestUtils.IntegrationTestProtocol>().OnPort((ushort)port);
        builder.MapHandlers<HandlerScanController>();

        using NetworkApplication app = builder.Build();
        await app.ActivateAsync().WaitAsync(TestUtils.Timeout);

        try
        {
            using TcpSession client = new(new TransportOptions { Address = "127.0.0.1", Port = (ushort)port });
            await client.ConnectAsync().WaitAsync(TestUtils.Timeout);

            using HandlerScanPacket pkt = new() { Value = 1 };
            var h = pkt.Header;
            h.OpCode = 8888;
            pkt.Header = h;

            await client.SendAsync(pkt, ct: default);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (HandlerScanController.ReceivedCount < 1 && !cts.IsCancellationRequested)
            {
                await Task.Delay(50, cts.Token);
            }

            Assert.Equal(1, HandlerScanController.ReceivedCount);
        }
        finally
        {
            await app.DeactivateAsync().WaitAsync(TestUtils.Timeout);
        }
    }

    [PacketHandler("HandlerScanDup1")]
    [Nalix.Abstractions.Injection.Injectable]
    public sealed class DupHandlerA
    {
        [PacketOpcode(7777)]
        public void Handle(IPacketContext<HandlerScanPacket> context) { }
    }

    [PacketHandler("HandlerScanDup2")]
    [Nalix.Abstractions.Injection.Injectable]
    public sealed class DupHandlerB
    {
        [PacketOpcode(7777)]
        public void Handle(IPacketContext<HandlerScanPacket> context) { }
    }

    [Fact]
    public void MapHandlers_DuplicateOpcodeAcrossHandlers_ThrowsAtRegistration()
    {
        // Documented/observed behavior: registering a second handler for an opcode
        // already claimed by another controller fails fast with InternalErrorException,
        // rather than silently overwriting ("last wins"). See
        // PacketDispatchOptions<TPacket>.RegisterHandler in
        // src/Nalix.Runtime/Dispatching/Options/PacketDispatchOptions.PublicMethods.cs (~line 262).
        var builder = NetworkApplication.CreateBuilder();
        builder.MapHandlers<DupHandlerA>();

        _ = Assert.Throws<InternalErrorException>(() => builder.MapHandlers<DupHandlerB>());
    }

    public sealed class NoHandlerMethodsController
    {
        public void NotAHandler() { }
    }

    [Fact]
    public void MapHandlers_TypeWithNoValidHandlerMethod_ThrowsClearError()
    {
        var builder = NetworkApplication.CreateBuilder();

        // Not annotated with [PacketHandler] / no source-generated registrar -> must fail
        // clearly, not silently register zero handlers.
        _ = Assert.Throws<InternalErrorException>(() => builder.MapHandlers<NoHandlerMethodsController>());
    }

    public void Dispose() => Nalix.Framework.Injection.InstanceManager.Instance.Clear(dispose: false);
}
