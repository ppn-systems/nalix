// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.ComponentModel;
using System.Diagnostics;
using Nalix.Abstractions;
using Nalix.Environment.Sequencing;

namespace Nalix.Network.Internal.Transport;

/// <summary>
/// Pooled holder for the send and receive counters of one UDP binding. A <see cref="SocketUdpTransport"/> reads its
/// counters from this holder and detaches them when it is disposed, so the holder can be returned to the pool as soon
/// as the binding is released.
/// </summary>
[DebuggerNonUserCode]
[EditorBrowsable(EditorBrowsableState.Never)]
internal sealed class UdpSequenceState : IPoolable
{
    public SequenceCounter Send { get; } = new();

    public SequenceCounter Receive { get; } = new();

    /// <inheritdoc/>
    public void ResetForPool()
    {
        this.Send.Reset(0);
        this.Receive.Reset(0);
    }
}
