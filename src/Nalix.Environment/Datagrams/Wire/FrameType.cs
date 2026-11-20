// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace Nalix.Environment.Datagrams.Wire;

/// <summary>
/// NDP v1 frame types (§7 of the design draft).
/// </summary>
internal enum FrameType : byte
{
    Padding = 0x00,
    Ping = 0x01,
    Ack = 0x02,
    Message = 0x03,
    PathChallenge = 0x04,
    PathResponse = 0x05,
    Close = 0x06,
    NewTicketHint = 0x07,
    Limits = 0x08,
}
