// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Codec.LZ4;

namespace Nalix.Codec.Tests.LZ4;

public sealed class LZ4CompressionConstantsTests
{
    [Fact]
    public void Constants_WhenRead_MatchExpectedLz4Values()
    {
        Assert.Equal(4, LZ4CompressionConstants.MinMatchLength);
        Assert.Equal(0xFFFF, LZ4CompressionConstants.MaxOffset);
        Assert.Equal(0x40000, LZ4CompressionConstants.MaxBlockSize);
        Assert.Equal(5, LZ4CompressionConstants.LastLiteralSize);
        Assert.Equal(0x0F, LZ4CompressionConstants.TokenMatchMask);
        Assert.Equal(0x0F, LZ4CompressionConstants.TokenLiteralMask);
    }
}


















