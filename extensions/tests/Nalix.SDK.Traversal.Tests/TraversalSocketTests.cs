// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Nalix.Abstractions.Exceptions;
using Nalix.SDK.Traversal;

namespace Nalix.SDK.Traversal.Tests;

/// <summary>
/// Verifies that <see cref="TraversalSocket"/> adds and strips the reflector token on relayed datagrams.
/// </summary>
public sealed class TraversalSocketTests
{
    private const ulong Token = 0x0102030405060708UL;

    [Fact]
    public async Task SendAsync_WhenReflected_PrependsLittleEndianTokenToPayload()
    {
        using UdpClient reflector = new(new IPEndPoint(IPAddress.Loopback, 0));
        using UdpClient local = new(0);
        local.Connect((IPEndPoint)reflector.Client.LocalEndPoint!);
        using TraversalSocket socket = new(local, isReflected: true, Token);
        byte[] payload = [0xAA, 0xBB, 0xCC];

        int sent = await socket.SendAsync(payload).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        UdpReceiveResult received = await reflector.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(payload.Length, sent);
        Assert.Equal(8 + payload.Length, received.Buffer.Length);
        Assert.Equal(Token, BinaryPrimitives.ReadUInt64LittleEndian(received.Buffer));
        Assert.Equal(payload, received.Buffer[8..]);
    }

    [Fact]
    public async Task ReceiveAsync_WhenReflectedWithMatchingToken_ReturnsPayloadWithoutToken()
    {
        using UdpClient reflector = new(new IPEndPoint(IPAddress.Loopback, 0));
        using UdpClient local = new(0);
        local.Connect((IPEndPoint)reflector.Client.LocalEndPoint!);
        using TraversalSocket socket = new(local, isReflected: true, Token);
        await SendFromReflectorAsync(reflector, local, Token, [1, 2, 3, 4]);

        byte[] buffer = new byte[16];
        int read = await socket.ReceiveAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(4, read);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, buffer[..read]);
    }

    [Fact]
    public async Task ReceiveAsync_WhenReflectedTokenDoesNotMatch_DropsDatagramAndReturnsZero()
    {
        using UdpClient reflector = new(new IPEndPoint(IPAddress.Loopback, 0));
        using UdpClient local = new(0);
        local.Connect((IPEndPoint)reflector.Client.LocalEndPoint!);
        using TraversalSocket socket = new(local, isReflected: true, Token);
        await SendFromReflectorAsync(reflector, local, Token + 1, [9, 9]);

        int read = await socket.ReceiveAsync(new byte[16]).AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, read);
    }

    [Fact]
    public async Task ReceiveAsync_WhenReflectedPayloadExceedsBuffer_ThrowsNetworkException()
    {
        using UdpClient reflector = new(new IPEndPoint(IPAddress.Loopback, 0));
        using UdpClient local = new(0);
        local.Connect((IPEndPoint)reflector.Client.LocalEndPoint!);
        using TraversalSocket socket = new(local, isReflected: true, Token);
        await SendFromReflectorAsync(reflector, local, Token, new byte[32]);

        _ = await Assert.ThrowsAsync<NetworkException>(async () =>
            _ = await socket.ReceiveAsync(new byte[8]).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task SendAsync_WhenNotReflected_SendsPayloadUnchanged()
    {
        using UdpClient peer = new(new IPEndPoint(IPAddress.Loopback, 0));
        using UdpClient local = new(0);
        local.Connect((IPEndPoint)peer.Client.LocalEndPoint!);
        using TraversalSocket socket = new(local, isReflected: false, reflectorToken: 0);
        byte[] payload = [5, 6, 7];

        _ = await socket.SendAsync(payload).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        UdpReceiveResult received = await peer.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(payload, received.Buffer);
    }

    [Fact]
    public async Task SendAsync_WhenDisposed_ThrowsObjectDisposedException()
    {
        using UdpClient local = new(0);
        TraversalSocket socket = new(local, isReflected: false, reflectorToken: 0);
        socket.Dispose();

        _ = await Assert.ThrowsAsync<ObjectDisposedException>(async () => _ = await socket.SendAsync(new byte[1]));
    }

    [Fact]
    public void Dispose_WhenCalledTwice_DoesNotThrow()
    {
        using UdpClient local = new(0);
        TraversalSocket socket = new(local, isReflected: false, reflectorToken: 0);

        socket.Dispose();
        socket.Dispose();
    }

    [Fact]
    public void Ctor_WhenUdpClientIsNull_ThrowsArgumentNullException()
    {
        _ = Assert.Throws<ArgumentNullException>(() => new TraversalSocket(null!, isReflected: false, reflectorToken: 0));
    }

    private static async Task SendFromReflectorAsync(UdpClient reflector, UdpClient local, ulong token, byte[] payload)
    {
        byte[] datagram = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt64LittleEndian(datagram, token);
        payload.CopyTo(datagram, 8);
        _ = await reflector.SendAsync(datagram, datagram.Length, (IPEndPoint)local.Client.LocalEndPoint!);
    }
}
