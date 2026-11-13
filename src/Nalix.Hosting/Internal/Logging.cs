// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using Microsoft.Extensions.Logging;

namespace Nalix.Hosting.Internal;

internal static partial class Logging
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "[NW.DefaultFrameProcessor:ProcessFrame] Discarding unreliable frame because UDP transport is not initialized.")]
    public static partial void DiscardUnreliableFrame(this ILogger logger);

    [LoggerMessage(Level = LogLevel.Trace, Message = "[NW.DefaultFrameProcessor:ProcessFrame] Dropped inbound packet due to decryption or decompression failure.")]
    public static partial void DroppedInboundPacket(this ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[NW.DefaultFrameProcessor:ProcessFrame] Rejected encrypted frame opcode=0x{OpCode:X4} on {Transport} transport: {Reason}.")]
    public static partial void UnauthenticatedFrame(this ILogger logger, ushort opCode, string transport, string reason);

    [LoggerMessage(Level = LogLevel.Trace, Message = "[NW.DefaultFrameProcessor:ProcessFrame] Rejected inbound sequence on {Transport} transport: received={ReceivedSeq}, current={CurrentSeq}.")]
    public static partial void RejectedSequence(this ILogger logger, string transport, uint? receivedSeq, uint currentSeq);

    [LoggerMessage(Level = LogLevel.Trace, Message = "[NW.DefaultFrameProcessor:ProcessFrame] Internal or serialization exception during message processing.")]
    public static partial void ProcessExceptionTrace(this ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "[NW.DefaultFrameProcessor:ProcessFrame] Unhandled exception during message processing.")]
    public static partial void ProcessExceptionError(this ILogger logger, Exception ex);
}
