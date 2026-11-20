// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Runtime.CompilerServices;
using System.Threading;

namespace Nalix.Network.Listeners.Datagram;

public sealed partial class DatagramListener
{
    /// <summary>
    /// Counters of the datagram listener. Lock-free and allocation-free.
    /// </summary>
    public sealed class DMetrics
    {
        #region Fields

        private long _datagramsReceived;
        private long _bytesReceived;
        private long _droppedMalformed;
        private long _droppedUnknown;
        private long _droppedUnauthenticated;
        private long _ticketsIssued;
        private long _ticketsExpired;
        private long _accepted;
        private long _guardRejections;
        private long _errors;

        #endregion Fields

        #region Properties

        /// <summary>Gets the number of datagrams read from the socket.</summary>
        public long DatagramsReceived => Volatile.Read(ref _datagramsReceived);

        /// <summary>Gets the number of bytes read from the socket.</summary>
        public long BytesReceived => Volatile.Read(ref _bytesReceived);

        /// <summary>Gets the number of datagrams dropped because they were too large or had no NDP header.</summary>
        public long DroppedMalformed => Volatile.Read(ref _droppedMalformed);

        /// <summary>Gets the number of datagrams dropped because their connection identifier is unknown.</summary>
        public long DroppedUnknown => Volatile.Read(ref _droppedUnknown);

        /// <summary>Gets the number of datagrams of a known session that did not authenticate (or were replays).</summary>
        public long DroppedUnauthenticated => Volatile.Read(ref _droppedUnauthenticated);

        /// <summary>Gets the total number of dropped datagrams.</summary>
        public long TotalDropped => this.DroppedMalformed + this.DroppedUnknown + this.DroppedUnauthenticated;

        /// <summary>Gets the number of tickets issued.</summary>
        public long TicketsIssued => Volatile.Read(ref _ticketsIssued);

        /// <summary>Gets the number of tickets that expired before their first authenticated datagram.</summary>
        public long TicketsExpired => Volatile.Read(ref _ticketsExpired);

        /// <summary>Gets the number of sessions accepted.</summary>
        public long TotalAccepted => Volatile.Read(ref _accepted);

        /// <summary>Gets the number of authenticated sessions the connection guard refused.</summary>
        public long TotalGuardRejections => Volatile.Read(ref _guardRejections);

        /// <summary>Gets the number of receive, processing and accept errors.</summary>
        public long TotalErrors => Volatile.Read(ref _errors);

        #endregion Properties

        #region Internal Methods

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void RECORD_RECEIVED(int bytes)
        {
            _ = Interlocked.Increment(ref _datagramsReceived);
            _ = Interlocked.Add(ref _bytesReceived, bytes);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void RECORD_DROPPED_MALFORMED() => Interlocked.Increment(ref _droppedMalformed);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void RECORD_DROPPED_UNKNOWN() => Interlocked.Increment(ref _droppedUnknown);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void RECORD_DROPPED_UNAUTHENTICATED() => Interlocked.Increment(ref _droppedUnauthenticated);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void RECORD_TICKET_ISSUED() => Interlocked.Increment(ref _ticketsIssued);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void RECORD_TICKET_EXPIRED() => Interlocked.Increment(ref _ticketsExpired);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void RECORD_ACCEPTED() => Interlocked.Increment(ref _accepted);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void RECORD_GUARD_REJECTION() => Interlocked.Increment(ref _guardRejections);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void RECORD_ERROR() => Interlocked.Increment(ref _errors);

        #endregion Internal Methods
    }
}
