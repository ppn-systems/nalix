// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Nalix.Observability.Contracts;

/// <summary>
/// Defines the lifecycle stage of a runtime observation packet.
/// </summary>
public enum RuntimeObservationStage : byte
{
    /// <summary>
    /// No observation operation has been assigned.
    /// </summary>
    NONE = 0x00,

    /// <summary>
    /// The packet requests a live snapshot of the selected <see cref="RuntimeObservationTargets"/>.
    /// </summary>
    REQUEST = 0x01,

    /// <summary>
    /// The packet contains a live snapshot or a failure reason.
    /// </summary>
    RESPONSE = 0x02,

    /// <summary>
    /// The packet queries recorded history. <see cref="RuntimeObservation.ObservationData"/> carries a UTF-8 JSON
    /// query; see <see cref="RuntimeObservationHistory"/> for its shape.
    /// </summary>
    HISTORY_REQUEST = 0x03,

    /// <summary>
    /// The packet contains a recorded-history result or a failure reason.
    /// </summary>
    HISTORY_RESPONSE = 0x04
}
