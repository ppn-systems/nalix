// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Codec.Pooling;
using Nalix.SDK.Transport;
using Nalix.SDK.Transport.Extensions;
using Nalix.Traversal.Packets;

namespace Nalix.SDK.Traversal;

/// <summary>
/// Orchestrates NAT Traversal (STUN/TURN equivalent) to establish P2P or Reflected connections.
/// </summary>
public sealed class TraversalClient
{
    private readonly TransportSession _controlSession;

    /// <summary>
    /// Initializes a new instance of the <see cref="TraversalClient"/> class.
    /// </summary>
    /// <param name="controlSession">The established Nalix transport session (TCP or WS) used for signaling.</param>
    public TraversalClient(TransportSession controlSession) => _controlSession = controlSession ?? throw new ArgumentNullException(nameof(controlSession));

    /// <summary>
    /// Attempts to establish a P2P connection or a Reflected proxy connection to the target peer.
    /// </summary>
    /// <param name="targetPeerId">The ID of the peer to connect to.</param>
    /// <param name="reflectorEndpoint">The UDP endpoint of the Nalix Server running the Reflector protocol.</param>
    /// <param name="timeout">The maximum time to wait for the entire process.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<TraversalSocket> ConnectAsync(ulong targetPeerId, IPEndPoint reflectorEndpoint, TimeSpan timeout, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reflectorEndpoint);

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        // 1. Wait for Candidate Offer
        TaskCompletionSource<(ulong High, ulong Low, ushort Port)> signalTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        using IDisposable signalSub = _controlSession.On<PeerSignal>(p =>
        {
            if (p.TargetPeerId == targetPeerId && p.Type == SignalType.CandidateOffer)
            {
                _ = signalTcs.TrySetResult((p.AddressHigh, p.AddressLow, p.Port));
            }
            else if (p.TargetPeerId == targetPeerId && p.Type == SignalType.Result)
            {
                _ = signalTcs.TrySetException(new InvalidOperationException("Peer signal rejected or peer not found."));
            }
        });

        using (PacketScope<PeerSignal> signalLease = PacketFactory<PeerSignal>.Acquire())
        {
            PeerSignal request = signalLease.Value;
            request.TargetPeerId = targetPeerId;
            request.Type = SignalType.Request;
            await _controlSession.SendAsync(request, cts.Token).ConfigureAwait(false);
        }

        (ulong High, ulong Low, ushort Port) candidate;
        try
        {
            candidate = await signalTcs.Task.WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("Timed out waiting for PeerSignal CandidateOffer.");
        }

        // 2. Try UDP Hole Punching
        Span<byte> ipBytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(ipBytes, candidate.Low);
        BinaryPrimitives.WriteUInt64LittleEndian(ipBytes[8..], candidate.High);

        IPAddress targetAddress = new(ipBytes);
        if (targetAddress.IsIPv4MappedToIPv6)
        {
            targetAddress = targetAddress.MapToIPv4();
        }

        IPEndPoint targetEndpoint = new(targetAddress, candidate.Port);

        UdpClient? udpClient = null;
        TaskCompletionSource<bool> probeTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource probeCts = new();
        probeCts.CancelAfter(TimeSpan.FromSeconds(3));
        Task? receiveTask = null;

        try
        {
            // Use the candidate's own family so IPv4-only hosts can hole-punch without an IPv6 stack.
            udpClient = new(0, targetAddress.AddressFamily);
            if (targetAddress.AddressFamily == AddressFamily.InterNetworkV6)
            {
                udpClient.Client.DualMode = true;
            }
            udpClient.Connect(targetEndpoint);

            receiveTask = this.ReceiveProbeAsync(udpClient, probeTcs, probeCts.Token);

            // Fire and forget probing
            UdpClient capturedClient = udpClient;
            _ = Task.Run(async () =>
            {
                byte[] dummyProbe = new byte[8];
                BinaryPrimitives.WriteUInt64LittleEndian(dummyProbe, targetPeerId);

                while (!probeCts.IsCancellationRequested)
                {
                    try
                    {
                        _ = await capturedClient.SendAsync(dummyProbe, dummyProbe.Length).ConfigureAwait(false);
                        await Task.Delay(200, probeCts.Token).ConfigureAwait(false);
                    }
                    catch (SocketException) { break; }
                    catch (ObjectDisposedException) { break; }
                }
            }, CancellationToken.None);

            bool success = await probeTcs.Task.WaitAsync(probeCts.Token).ConfigureAwait(false);
            if (success)
            {
                // Direct P2P Hole Punching Successful!
                UdpClient result = udpClient;
                udpClient = null; // Transfer ownership
                return new TraversalSocket(result, false, 0);
            }
        }
        catch (OperationCanceledException)
        {
            // Hole punch failed, fallback to Reflector
        }
        finally
        {
            await probeCts.CancelAsync().ConfigureAwait(false);
            if (receiveTask != null)
            {
                try { await receiveTask.ConfigureAwait(false); } catch (SocketException) { } catch (ObjectDisposedException) { }
            }
            udpClient?.Dispose();
        }

        // 3. Fallback: Request Reflector Proxying
        // Accept only an allocation that answers this request, or the server's notification for a session
        // that names targetPeerId as its peer. Anything else (another pair's allocation) must not be adopted.
        ulong requestNonce = (ulong)Random.Shared.NextInt64(1, long.MaxValue);
        TaskCompletionSource<ulong> reflectorTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable reflectorSub = _controlSession.On<ReflectorAllocated>(p =>
        {
            if (p.PeerId != targetPeerId || (p.RequestNonce != requestNonce && p.RequestNonce != 0))
            {
                return;
            }

            if (p.Success)
            {
                _ = reflectorTcs.TrySetResult(p.ReflectorToken);
            }
            else
            {
                _ = reflectorTcs.TrySetException(new InvalidOperationException("Reflector allocation failed on server."));
            }
        });

        using (PacketScope<ReflectorInit> refLease = PacketFactory<ReflectorInit>.Acquire())
        {
            ReflectorInit refInit = refLease.Value;
            refInit.TargetPeerId = targetPeerId;
            refInit.RequestNonce = requestNonce;
            await _controlSession.SendAsync(refInit, cts.Token).ConfigureAwait(false);
        }

        ulong reflectorToken;
        try
        {
            reflectorToken = await reflectorTcs.Task.WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("Timed out waiting for Reflector allocation.");
        }

        // Re-target UDP client to the Server's Reflector Endpoint
        UdpClient? refClient = null;
        try
        {
            refClient = new(0, reflectorEndpoint.AddressFamily);
            if (reflectorEndpoint.AddressFamily == AddressFamily.InterNetworkV6)
            {
                refClient.Client.DualMode = true;
            }
            refClient.Connect(reflectorEndpoint);

            UdpClient result = refClient;
            refClient = null; // Transfer ownership
            return new TraversalSocket(result, true, reflectorToken);
        }
        finally
        {
            refClient?.Dispose();
        }
    }

    private async Task ReceiveProbeAsync(UdpClient client, TaskCompletionSource<bool> tcs, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult result = await client.ReceiveAsync(ct).ConfigureAwait(false);
                if (result.Buffer.Length > 0)
                {
                    _ = tcs.TrySetResult(true);
                    return;
                }
            }
        }
        catch (SocketException)
        {
            // Ignore socket errors during probe
        }
        catch (ObjectDisposedException)
        {
            // Ignore socket errors during probe
        }
        catch (OperationCanceledException)
        {
            // Expected on timeout
        }
    }
}
