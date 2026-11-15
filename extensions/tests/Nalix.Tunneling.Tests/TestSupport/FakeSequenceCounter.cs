// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Security;

namespace Nalix.Tunneling.Tests.TestSupport;

internal sealed class FakeSequenceCounter : ISequenceCounter
{
    private uint _value;

    public uint Next() => ++this._value;

    public uint Current() => this._value;

    public void Reset(uint newValue = 0) => this._value = newValue;

    public bool IsValid(uint? receivedSeq, uint window = 0) => true;

    public void UpdateTo(uint receivedSeq) => this._value = receivedSeq;

    public void ResumeFrom(uint lastKnownSeq, uint safetyGap = 1000) => this._value = lastKnownSeq + safetyGap;

    public bool IsApproachingOverflow(uint margin = 1_000_000) => false;
}
