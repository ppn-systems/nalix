// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Security;
using Nalix.Abstractions.Validation;
using Nalix.Environment.Configuration.Binding;

namespace Nalix.Network.Options;

/// <summary>
/// Configuration options for the <c>AsyncCallback</c> dispatcher and
/// per-connection receive throttle in <c>FramedSocketConnection</c>.
///
/// <para><b>DDoS protection tuning:</b><br/>
/// These values control two independent throttle layers:
/// <list type="bullet">
///   <item>
///     <b>Layer 1</b> — per-connection packet queue cap
///     (<see cref="MaxPerConnectionPendingPackets"/>).
///     Packets from a single connection that arrive faster than the protocol
///     handler can process them are dropped at the receive loop before they
///     ever reach the ThreadPool.
///   </item>
///   <item>
///     <b>Layer 2</b> — global and per-IP callback caps
///     (<see cref="MaxPendingNormalCallbacks"/>, <see cref="MaxPendingPerIp"/>).
///     Enforced inside <c>AsyncCallback.Invoke</c>; close/disconnect events
///     always bypass these limits via <c>InvokeHighPriority</c>.
///   </item>
/// </list>
/// </para>
/// </summary>
[IniComment("Async callback dispatcher and per-connection throttle settings (DDoS protection)")]
public sealed partial class NetworkCallbackOptions : ConfigurationLoader, IValidatableConfiguration
{
    #region Layer 1 — Per-connection receive throttle

    /// <summary>
    /// Maximum number of packets that may be queued-but-not-yet-processed
    /// for a <b>single connection</b> at any moment.
    /// <para>
    /// When a connection sends packets faster than the protocol handler can
    /// consume them the excess packets are dropped at the receive loop and a
    /// warning is logged. Legitimate clients rarely have more than 1–2 packets
    /// in-flight simultaneously; the default of <c>16</c> gives generous
    /// headroom while blocking flood attacks.
    /// </para>
    /// </summary>
    [IniComment("Max packets queued per connection before dropping (Layer 1, default 16) (min=1, max=1024)")]
    [ValueRange(1, 1024)]
    public int MaxPerConnectionPendingPackets { get; set; } = 16;

    /// <summary>
    /// Maximum number of concurrently open fragmented streams per connection.
    /// </summary>
    [IniComment("Max concurrently open fragmented streams per connection (Layer 1, default 4) (min=1, max=256)")]
    [ValueRange(1, 256)]
    public int MaxPerConnectionOpenFragmentStreams { get; set; } = 4;

    /// <summary>
    /// Minimum <see cref="PermissionLevel"/> a connection must hold before it may send fragmented frames.
    /// <para>
    /// Reassembly state is allocated per connection, so fragments from a connection below this level are
    /// dropped before any buffer is created. The default <see cref="PermissionLevel.ESTABLISHED"/> is the level
    /// granted when the handshake completes (or a session is resumed). Handshake frames are far smaller than
    /// <c>FragmentOptions.MaxChunkSize</c> and are never fragmented.
    /// </para>
    /// </summary>
    [IniComment("Minimum permission level required to send fragmented frames (default ESTABLISHED)")]
    public PermissionLevel MinFragmentPermissionLevel { get; set; } = PermissionLevel.ESTABLISHED;

    #endregion Layer 1 — Per-connection receive throttle

    #region Layer 2 — Global and per-IP callback caps

    /// <summary>
    /// Action will be executed when the number of pending packets for a connection
    /// exceeds <see cref="MaxPerConnectionPendingPackets"/>.
    /// </summary>
    [IniComment("Action to take when a connection exceeds max pending packets (0=DropPacket, 1=Disconnect)")]
    public NetworkOverflowPolicy OverflowPolicy { get; set; } = NetworkOverflowPolicy.DropPacket;

    /// <summary>
    /// Maximum total <b>normal-priority</b> callbacks that may be pending in
    /// <c>AsyncCallback</c> simultaneously across all connections.
    /// <para>
    /// High-priority close/disconnect callbacks are <b>never</b> counted
    /// against this limit and are always dispatched immediately.
    /// </para>
    /// </summary>
    [IniComment("Max total normal-priority callbacks pending globally (Layer 2, default 10000) (min=100, max=1000000)")]
    [ValueRange(100, 1_000_000)]
    public int MaxPendingNormalCallbacks { get; set; } = 10_000;

    /// <summary>
    /// Emit a warning log every time pending normal callbacks reaches a
    /// multiple of this value. Set to <c>0</c> to disable warnings.
    /// </summary>
    [IniComment("Log warning when pending callbacks crosses this threshold (0 = disabled, default 5000) (min=0, max=1,000,000)")]
    [ValueRange(0, 1_000_000)]
    public int CallbackWarningThreshold { get; set; } = 5_000;

    /// <summary>
    /// Maximum normal-priority callbacks pending for a <b>single remote IP</b>.
    /// <para>
    /// Callbacks from that IP are dropped once this threshold is exceeded,
    /// regardless of how much global headroom remains. This prevents one
    /// attacker IP from monopolising the global callback quota and starving
    /// legitimate connections.
    /// </para>
    /// </summary>
    [IniComment("Max normal-priority callbacks per remote IP (Layer 2, default 64)")]
    [ValueRange(1, 10_000)]
    public int MaxPendingPerIp { get; set; } = 64;

    /// <summary>
    /// Maximum number of StateWrapper objects held in the
    /// internal reuse pool inside <c>AsyncCallback</c>.
    /// <para>
    /// Higher values reduce GC pressure under sustained load at the cost of
    /// a small constant memory footprint (~few KB per object).
    /// </para>
    /// </summary>
    [IniComment("StateWrapper object pool ceiling inside AsyncCallback (default 1000) (min=64, max=100,000)")]
    [ValueRange(64, 100_000)]
    public int MaxPooledCallbackStates { get; set; } = 1_000;

    /// <summary>
    /// Size of the fixed-size array used for Layer 2 per-IP fairness tracking.
    /// Larger values reduce hash collisions (false backpressure) but consume more memory.
    /// </summary>
    [IniComment("Size of the fixed-size fairness map array (default 16384) (min=1024, max=65536)")]
    [ValueRange(1024, 65536)]
    public int FairnessMapSize { get; set; } = 16384;

    /// <summary>
    /// Maximum total <b>post-process</b> (send) callbacks that may be pending globally.
    /// </summary>
    [IniComment("Max total send callbacks pending globally (Layer 2, default 5000) (min=100, max=1000000)")]
    [ValueRange(100, 1_000_000)]
    public int MaxPendingPostCallbacks { get; set; } = 5_000;

    /// <summary>
    /// Maximum send callbacks pending for a <b>single remote IP</b>.
    /// </summary>
    [IniComment("Max send callbacks per remote IP (Layer 2, default 32)")]
    [ValueRange(1, 10_000)]
    public int MaxPendingPostPerIp { get; set; } = 32;

    /// <summary>
    /// Maximum execution time for a single callback before it is considered slow and logged.
    /// </summary>
    [IniComment("Max execution time for a single callback before logging a warning (default 50) (min=1, max=60000)")]
    [ValueRange(1, 60_000)]
    public int MaxCallbackExecutionMs { get; set; } = 50;

    #endregion Layer 2 — Global and per-IP callback caps

    /// <inheritdoc/>
    public void Validate()
    {
        this.ValidateDataAnnotations();

        // Cross-field guard: warning threshold should be below global cap
        if (this.CallbackWarningThreshold > 0 && this.CallbackWarningThreshold >= this.MaxPendingNormalCallbacks)
        {
            throw new System.ArgumentException(
                $"{nameof(this.CallbackWarningThreshold)} ({this.CallbackWarningThreshold}) must be less than {nameof(this.MaxPendingNormalCallbacks)} ({this.MaxPendingNormalCallbacks}).");
        }

        // Cross-field guard: per-IP cap should not exceed global cap
        if (this.MaxPendingPerIp > this.MaxPendingNormalCallbacks)
        {
            throw new System.ArgumentException(
                $"{nameof(this.MaxPendingPerIp)} ({this.MaxPendingPerIp}) must not exceed {nameof(this.MaxPendingNormalCallbacks)} ({this.MaxPendingNormalCallbacks}).");
        }

        // Cross-field guard: post-process per-IP cap should not exceed post-process global cap
        if (this.MaxPendingPostPerIp > this.MaxPendingPostCallbacks)
        {
            throw new System.ArgumentException(
                $"{nameof(this.MaxPendingPostPerIp)} ({this.MaxPendingPostPerIp}) must not exceed {nameof(this.MaxPendingPostCallbacks)} ({this.MaxPendingPostCallbacks}).");
        }
    }
}
