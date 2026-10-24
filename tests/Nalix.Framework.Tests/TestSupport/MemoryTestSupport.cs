// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Framework.Options;

namespace Nalix.Framework.Tests.TestSupport;

internal static class MemoryTestSupport
{
    public static BufferOptions CreateBufferOptions(bool enableMemoryTrimming)
    {
        return new BufferOptions
        {
            EnableBufferLeakDetection = false,
            EnableBufferLeakStackTrace = false,
            SuspiciousThresholdSeconds = 30
        };
    }
}
