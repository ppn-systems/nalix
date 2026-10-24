// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Security;

namespace Nalix.Runtime.Tests.TestSupport;

internal sealed class FakeTransport : IConnection.ITransport
{
    public List<byte[]> SentMessages { get; } = [];

    public TransportFraming Framing => TransportFraming.UInt16LengthPrefixed;

    public ISequenceCounter SendSequence { get; } = new FakeSequenceCounter();

    public ISequenceCounter ReceiveSequence { get; } = new FakeSequenceCounter();

    public uint NextSendSequence() => SendSequence.Next();

    public uint NextReceiveSequence() => ReceiveSequence.Next();

    public uint CurrentSendSequence => SendSequence.Current();

    public uint CurrentReceiveSequence => ReceiveSequence.Current();

    public void Send(ReadOnlySpan<byte> message) => SentMessages.Add(message.ToArray());

    public ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
    {
        SentMessages.Add(message.ToArray());
        return ValueTask.CompletedTask;
    }

    public void BeginReceive(CancellationToken cancellationToken = default) { }

    public void UseFraming(TransportFraming framing) { }
}
