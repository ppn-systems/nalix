// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers;
using System.Net;
using System.Threading;
using Nalix.Abstractions.Networking.Datagrams;
using Nalix.Environment.Datagrams;

namespace Nalix.Network.Internal.Datagram;

internal sealed partial class DatagramSocketConnection
{
    #region Send

    /// <summary>
    /// Queues a message.
    /// </summary>
    /// <returns><see langword="false"/> when the message is too large, the queue is full, or the session is closed.</returns>
    /// <exception cref="ObjectDisposedException">The session is disposed.</exception>
    public bool TrySend(ReadOnlySpan<byte> message, DeliveryMethod method, byte channel, out bool closed)
    {
        bool queued;
        lock (_engineLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, _owner);

            queued = _engine.TrySend(message, method, channel, 0, DatagramEngine.NowMicros());
            closed = _engine.State >= DatagramEngineState.Draining;
        }

        if (queued)
        {
            this.Flush();
        }

        return queued;
    }

    /// <summary>Starts a graceful close and sends the CLOSE frame.</summary>
    public void Close(string? reason)
    {
        lock (_engineLock)
        {
            if (_disposed)
            {
                return;
            }

            _engine.Close(DatagramCloseCode.NoError, reason, DatagramEngine.NowMicros());
        }

        this.Flush();
    }

    /// <summary>Sends everything the engine produces now and re-arms the deadline.</summary>
    private void Flush()
    {
        _ = Interlocked.Exchange(ref _flushRequested, 1);

        // Re-check after leaving: a request made while this thread was finishing would otherwise be lost.
        while (Volatile.Read(ref _flushRequested) == 1 && _sendLock.TryEnter())
        {
            try
            {
                while (Interlocked.Exchange(ref _flushRequested, 0) == 1)
                {
                    this.Drain();
                }
            }
            finally
            {
                _sendLock.Exit();
            }
        }
    }

    /// <summary>Polls the engine in batches under its lock and sends each batch outside it. Called under the send lock.</summary>
    private void Drain()
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(SendBatchSize * DatagramEngine.MaxDatagramSize);
        SocketAddress?[] targets = ArrayPool<SocketAddress?>.Shared.Rent(SendBatchSize);
        Span<int> lengths = stackalloc int[SendBatchSize];

        try
        {
            int count;
            do
            {
                count = 0;
                lock (_engineLock)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    long now = DatagramEngine.NowMicros();
                    while (count < SendBatchSize)
                    {
                        int length = _engine.Poll(
                            buffer.AsSpan(count * DatagramEngine.MaxDatagramSize, DatagramEngine.MaxDatagramSize), now, out SocketAddress? to);
                        if (length == 0)
                        {
                            break;
                        }

                        SocketAddress? target = to ?? _engine.ActivePath;
                        if (target is not null)
                        {
                            targets[count] = target;
                            lengths[count] = length;
                            count++;
                        }
                    }

                    this.ArmLocked();
                }

                int sent = 0;
                for (int i = 0; i < count; i++)
                {
                    _listener.SendTo(buffer.AsSpan(i * DatagramEngine.MaxDatagramSize, lengths[i]), targets[i]!);
                    sent += lengths[i];
                    targets[i] = null;
                }

                if (sent > 0)
                {
                    _owner.IncrementBytesSent(sent);
                }
            }
            while (count == SendBatchSize);
        }
        finally
        {
            Array.Clear(targets, 0, SendBatchSize);
            ArrayPool<SocketAddress?>.Shared.Return(targets);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    #endregion Send
}
