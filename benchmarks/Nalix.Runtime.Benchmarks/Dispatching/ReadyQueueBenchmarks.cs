// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using BenchmarkDotNet.Attributes;
using Nalix.Benchmarks.Shared;

namespace Nalix.Runtime.Benchmarks.Dispatching;

/// <summary>
/// Isolates the ready-queue primitive used by <c>DispatchChannel&lt;TPacket&gt;</c>
/// (a per-priority hand-off between "packet arrived for connection X" producers and
/// dispatch workers). Only <c>TryWrite</c>/<c>TryRead</c> are exercised, matching
/// production usage exactly: the async wait machinery of <see cref="Channel{T}"/>
/// (ReadAsync/WaitToReadAsync) is never used on this path.
/// </summary>
[Config(typeof(NalixBenchmarkConfig))]
[MemoryDiagnoser]
public class ReadyQueueBenchmarks
{
    private sealed class Token;

    private Channel<Token> _channel = null!;
    private ConcurrentQueue<Token> _queue = null!;
    private Token _item = null!;

    [IterationSetup]
    public void Setup()
    {
        _channel = Channel.CreateUnbounded<Token>(new UnboundedChannelOptions
        {
            AllowSynchronousContinuations = false,
            SingleReader = false,
            SingleWriter = false
        });
        _queue = new ConcurrentQueue<Token>();
        _item = new Token();
    }

    /// <summary>
    /// Single-threaded write-then-read round trip: the cheapest possible case, no
    /// contention. Establishes the fixed per-call overhead of each primitive.
    /// </summary>
    [Benchmark(Baseline = true)]
    public bool Channel_WriteRead_SingleThread()
    {
        _ = _channel.Writer.TryWrite(_item);
        return _channel.Reader.TryRead(out _);
    }

    [Benchmark]
    public bool ConcurrentQueue_WriteRead_SingleThread()
    {
        _queue.Enqueue(_item);
        return _queue.TryDequeue(out _);
    }

    /// <summary>
    /// One producer thread, one consumer thread racing concurrently — closer to the
    /// production shape where a socket-completion callback writes and a dispatch
    /// worker reads from a different thread.
    /// </summary>
    [Benchmark]
    public void Channel_WriteRead_TwoThreads()
    {
        const int n = 20_000;
        using CountdownEvent done = new(2);

        var producer = new Thread(() =>
        {
            for (int i = 0; i < n; i++)
            {
                while (!_channel.Writer.TryWrite(_item)) { Thread.SpinWait(1); }
            }
            done.Signal();
        });

        var consumer = new Thread(() =>
        {
            int received = 0;
            while (received < n)
            {
                if (_channel.Reader.TryRead(out _))
                {
                    received++;
                }
            }
            done.Signal();
        });

        producer.Start();
        consumer.Start();
        done.Wait();
    }

    [Benchmark]
    public void ConcurrentQueue_WriteRead_TwoThreads()
    {
        const int n = 20_000;
        using CountdownEvent done = new(2);

        var producer = new Thread(() =>
        {
            for (int i = 0; i < n; i++)
            {
                _queue.Enqueue(_item);
            }
            done.Signal();
        });

        var consumer = new Thread(() =>
        {
            int received = 0;
            while (received < n)
            {
                if (_queue.TryDequeue(out _))
                {
                    received++;
                }
            }
            done.Signal();
        });

        producer.Start();
        consumer.Start();
        done.Wait();
    }
}
