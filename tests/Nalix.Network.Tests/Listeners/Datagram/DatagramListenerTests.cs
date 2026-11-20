// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net.Sockets;
using System.Reflection;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Datagrams;
using Nalix.Environment.Configuration;
using Nalix.Network.Listeners.Datagram;
using Nalix.Network.Options;
using NSubstitute;
using Xunit;

namespace Nalix.Network.Tests.Listeners.Datagram;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
public sealed class DatagramListenerTests
{
    [Fact]
    public void Activate_WhenAnotherListenerOwnsThePort_ThrowsInsteadOfSharingIt()
    {
        // ReuseAddress (on by default for TCP) must not let two UDP sockets split one port's datagrams.
        Assert.True(ConfigurationManager.Instance.Get<NetworkSocketOptions>().ReuseAddress);

        using DatagramListener first = CreateListener(0);
        first.Activate();
        Assert.True(first.IsListening);

        using DatagramListener second = CreateListener(first.Port);
        SocketException error = Assert.Throws<SocketException>(() => second.Activate());

        Assert.Equal(SocketError.AddressAlreadyInUse, error.SocketErrorCode);
        Assert.False(second.IsListening);
        Assert.True(first.IsListening);
    }

    [Fact]
    public void Deactivate_ThenActivate_RebindsThePort()
    {
        using DatagramListener listener = CreateListener(0);
        listener.Activate();
        ushort port = listener.Port;

        listener.Deactivate();
        Assert.False(listener.IsListening);

        using DatagramListener other = CreateListener(port);
        other.Activate();
        Assert.True(other.IsListening);
    }

    [Fact]
    public async Task Deactivate_WhileActivateIsInProgress_StopsTheListenerOnceActivationEnds()
    {
        using DatagramListener listener = CreateListener(0);
        listener.Activate();

        // Put the listener back in the middle of an activation: Activate holds the lock in the STARTING state.
        FieldInfo state = typeof(DatagramListener).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!;
        SemaphoreSlim gate = (SemaphoreSlim)typeof(DatagramListener).GetField("_lock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(listener)!;
        gate.Wait();
        state.SetValue(listener, 1); // STARTING

        Task deactivate = Task.Run(() => listener.Deactivate());
        await Task.Delay(100);
        Assert.False(deactivate.IsCompleted, "Deactivate must wait for the activation in progress.");

        state.SetValue(listener, 2); // RUNNING: the activation finished
        _ = gate.Release();
        await deactivate.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(listener.IsListening);
    }

    private static DatagramListener CreateListener(ushort port)
        => new(port, Substitute.For<IProtocol>(), Substitute.For<IConnectionHub>(), Substitute.For<IDatagramProtectionFactory>());
}
