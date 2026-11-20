// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.


using Nalix.Environment.Fragments;

namespace Nalix.Environment.Datagrams;

public sealed partial class DatagramEngine
{
    /// <summary>Largest MESSAGE frame header: type, channel, info, two 8-byte varints and a 4-byte length.</summary>
    private const int MaxMessageFrameHeader = 3 + 8 + 8 + 4;

    /// <summary>
    /// Largest message sent in one MESSAGE frame. Frames are sized for <see cref="DatagramEngineOptions.MinimumMtu"/>
    /// so that a retransmission still fits after the path MTU falls back.
    /// </summary>
    public const int MaxUnfragmentedMessageSize =
        DatagramEngineOptions.MinimumMtu - DatagramHeader.Size - TagSizeBound - MaxMessageFrameHeader;

    /// <summary>The tag size assumed for sizing frames (Poly1305).</summary>
    private const int TagSizeBound = 16;

    internal const int ChunkBodySize = MaxUnfragmentedMessageSize - FragmentHeader.WireSize;
    internal const int PriorityLevels = 8;
}
