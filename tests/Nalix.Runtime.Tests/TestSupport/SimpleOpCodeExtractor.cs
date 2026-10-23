// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers.Binary;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Protocols;

namespace Nalix.Runtime.Tests.TestSupport;

internal sealed class SimpleOpCodeExtractor : IOpCodeExtractor
{
    public ushort Extract(ReadOnlySpan<byte> payload) =>
        payload.Length >= 2 ? BinaryPrimitives.ReadUInt16LittleEndian(payload) : (ushort)0;
}
