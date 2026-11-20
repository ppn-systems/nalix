// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using Microsoft.Extensions.Logging;

namespace Nalix.Hosting.Internal;

internal static partial class Logging
{
    [LoggerMessage(Level = LogLevel.Trace, Message = "[NW.DefaultFrameProcessor:ProcessFrame] Dropped inbound packet due to decryption or decompression failure.")]
    public static partial void DroppedInboundPacket(this ILogger logger);

    [LoggerMessage(Level = LogLevel.Trace, Message = "[NW.DefaultFrameProcessor:ProcessFrame] Rejected inbound sequence on {Transport} transport: received={ReceivedSeq}, current={CurrentSeq}.")]
    public static partial void RejectedSequence(this ILogger logger, string transport, uint? receivedSeq, uint currentSeq);

    [LoggerMessage(Level = LogLevel.Trace, Message = "[NW.DefaultFrameProcessor:ProcessFrame] Internal or serialization exception during message processing.")]
    public static partial void ProcessExceptionTrace(this ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "[NW.DefaultFrameProcessor:ProcessFrame] Unhandled exception during message processing.")]
    public static partial void ProcessExceptionError(this ILogger logger, Exception ex);
}
