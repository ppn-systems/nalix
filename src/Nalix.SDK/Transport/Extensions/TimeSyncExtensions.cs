// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Codec.ProtocolFrames;
using Nalix.Environment.Time;
using Nalix.SDK.Options;

namespace Nalix.SDK.Transport.Extensions;

/// <summary>
/// Provides extension methods for performing time synchronization operations over a <see cref="TcpSession"/>.
/// </summary>
public static class TimeSyncExtensions
{
    private static int s_syncSequence;

    /// <summary>
    /// A fluent builder for <see cref="TimeSync"/> frames.
    /// </summary>
    public readonly ref struct TimeSyncBuilder(TimeSync t)
    {
        /// <inheritdoc/>
        public TimeSyncBuilder WithSeq(ushort seq) { t.SequenceId = seq; return this; }


        /// <inheritdoc/>
        public TimeSyncBuilder StampNow()
        {
            t.MonoTicks = Clock.MonoTicksNow();
            t.Timestamp = Clock.UnixMillisecondsNow();
            return this;
        }

        /// <inheritdoc/>
        public TimeSync Build() => t;
    }

    /// <summary>
    /// Creates a new TimeSync frame.
    /// </summary>
    public static TimeSyncBuilder NewTimeSync(this ITransportSession _, ControlType type)
    {
#pragma warning disable CA2000
        TimeSync t = TimeSync.Create();
#pragma warning restore CA2000
        t.Initialize(type, sequenceId: 0, flags: PacketFlags.SYSTEM);
        return new TimeSyncBuilder(t);
    }

    /// <summary>
    /// Sends a time synchronization request to the server and adjusts the client's internal <see cref="Clock"/> based on the response.
    /// </summary>
    /// <param name="session">The connected transport session.</param>
    /// <param name="timeoutMs">The maximum time to wait for a time sync response, in milliseconds.</param>
    /// <param name="ct">A cancellation token that can be used to abort the synchronization process.</param>
    /// <returns>
    /// A tuple containing:
    /// <list type="bullet">
    /// <item><description><c>RttMs</c>: The measured round-trip time (RTT) in milliseconds.</description></item>
    /// <item><description><c>AdjustedMs</c>: The clock adjustment applied in milliseconds; <c>0</c> when time sync is disabled.</description></item>
    /// </list>
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="session"/> is null.</exception>
    /// <exception cref="NetworkException">Thrown if the session is not connected.</exception>
    /// <exception cref="TimeoutException">Thrown if no response is received within the specified timeout.</exception>
    public static async ValueTask<(double RttMs, double AdjustedMs)> SyncTimeAsync(this ITransportSession session, int timeoutMs = 5000, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        ushort seq = unchecked((ushort)Interlocked.Increment(ref s_syncSequence));

        TimeSync req = session
            .NewTimeSync(ControlType.TIMESYNCREQUEST)
            .WithSeq(seq)
            .Build();

        long t1Mono = req.MonoTicks;

        TimeSync res = await session.RequestAsync<TimeSync>(
            req,
            options: RequestOptions.Default.WithTimeout(timeoutMs).WithEncrypt(false),
            predicate: p => p.Type == ControlType.TIMESYNCRESPONSE && p.SequenceId == seq,
            ct: ct).ConfigureAwait(false);

        long t4Mono = Clock.MonoTicksNow();

        double rttMs = Clock.MonoTicksToMilliseconds(t4Mono - t1Mono);

        double adjustedMs = 0;

        if (session.Options.TimeSyncEnabled)
        {
            double offset = TimeSyncCalculator.CalculateOffsetMs(res.Timestamp, rttMs);
            session.Options.TimeOffsetMs = (session.Options.TimeOffsetMs * 0.9) + (offset * 0.1);

            adjustedMs = session.Options.TimeOffsetMs;
        }

        return (rttMs, adjustedMs);
    }
}
