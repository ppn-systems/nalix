// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Primitives;
using Nalix.Codec.DataFrames;
using Nalix.Codec.ProtocolFrames;
using Nalix.Codec.Transforms;
using Nalix.Environment.Fragments;

namespace Nalix.Codec.Tests.TestSupport;

public static class FrameTestData
{
    public enum PacketRoundTripKind
    {
        Control,
        Directive
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1825:Avoid zero-length array allocations", Justification = "<Pending>")]
    public static TheoryData<PacketRoundTripKind> PacketRoundTripCases()
    {
        return
        [
            PacketRoundTripKind.Control,
            PacketRoundTripKind.Directive
        ];
    }

    public static byte[] CreatePacketBytes(string payload)
    {
        byte[] payloadBytes = Encoding.UTF8.GetBytes(payload);
        byte[] buffer = new byte[FrameTransformer.Offset + payloadBytes.Length];

        // Fill header with predictable dummy data that won't fail FrameTransformer checks
        for (int i = 0; i < FrameTransformer.Offset; i++)
        {
            buffer[i] = (byte)(i + 1);
        }

        payloadBytes.CopyTo(buffer, FrameTransformer.Offset);
        return buffer;
    }

    public static FrameBase CreateRoundTripPacket(PacketRoundTripKind packetKind)
        => packetKind switch
        {
            PacketRoundTripKind.Control => CreateControlPacket(),
            PacketRoundTripKind.Directive => CreateDirectivePacket(),
            _ => throw new InvalidOperationException("Unexpected packet round-trip kind.")
        };

    public static Control CreateControlPacket()
    {
        Control packet = new();
        packet.Initialize(ControlType.PING, 55, PacketFlags.SYSTEM, ProtocolReason.NONE);
        return packet;
    }

    public static Directive CreateDirectivePacket()
    {
        Directive packet = new();
        packet.Initialize(ControlType.REDIRECT, ProtocolReason.THROTTLED, ProtocolAdvice.SLOW_DOWN, 12, PacketFlags.SYSTEM, ControlFlags.SLOW_DOWN, 9, 8, 7);
        return packet;
    }

    public static void AssertRoundTripPacketEquivalent(PacketRoundTripKind packetKind, FrameBase expected, FrameBase actual)
    {
        switch (packetKind)
        {
            case PacketRoundTripKind.Control:
                {
                    Control expectedControl = Assert.IsType<Control>(expected);
                    Control actualControl = Assert.IsType<Control>(actual);
                    Assert.Equal(expectedControl.Header.OpCode, actualControl.Header.OpCode);
                    Assert.Equal(expectedControl.Type, actualControl.Type);
                    Assert.Equal(expectedControl.Reason, actualControl.Reason);
                    Assert.Equal(expectedControl.Header.Flags, actualControl.Header.Flags);
                    Assert.Equal(expectedControl.Header.SequenceId, actualControl.Header.SequenceId);
                    break;
                }
            case PacketRoundTripKind.Directive:
                {
                    Directive expectedDirective = Assert.IsType<Directive>(expected);
                    Directive actualDirective = Assert.IsType<Directive>(actual);
                    Assert.Equal(expectedDirective.Header.OpCode, actualDirective.Header.OpCode);
                    Assert.Equal(expectedDirective.Type, actualDirective.Type);
                    Assert.Equal(expectedDirective.Reason, actualDirective.Reason);
                    Assert.Equal(expectedDirective.Action, actualDirective.Action);
                    Assert.Equal(expectedDirective.Control, actualDirective.Control);
                    Assert.Equal(expectedDirective.Arg0, actualDirective.Arg0);
                    Assert.Equal(expectedDirective.Arg1, actualDirective.Arg1);
                    Assert.Equal(expectedDirective.Arg2, actualDirective.Arg2);
                    Assert.Equal(expectedDirective.Header.SequenceId, actualDirective.Header.SequenceId);
                    break;
                }
            default:
                throw new InvalidOperationException("Unexpected packet round-trip kind.");
        }
    }

    public static byte[] CreateFragmentPayload(FragmentHeader header, ReadOnlySpan<byte> body)
    {
        byte[] payload = new byte[FragmentHeader.WireSize + body.Length];
        header.WriteTo(payload);
        body.CopyTo(payload.AsSpan(FragmentHeader.WireSize));
        return payload;
    }
}
