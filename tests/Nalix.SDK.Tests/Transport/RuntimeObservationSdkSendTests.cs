// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Codec.DataFrames;
using Nalix.Environment.Memory;
using Nalix.Observability.Contracts;

namespace Nalix.SDK.Tests.Transport;

/// <summary>
/// Regression tests that exercise the SDK client send-frame allocation path
/// for RuntimeObservation with variable-size ObservationData.
/// </summary>
public sealed class RuntimeObservationSdkSendTests
{
    [Fact]
    public void Serialize_WithObservationData_WritesFullFrameIntoLease()
    {
        byte[] payload = [100, 101, 102, 103];

        RuntimeObservation packet = RuntimeObservation.Create();
        packet.Initialize(
            RuntimeObservationStage.RESPONSE,
            RuntimeObservationTarget.INSTANCES,
            ProtocolReason.NONE,
            payload);

        int reportedLength = packet.Length;

        BufferLease lease = BufferLease.Rent(reportedLength);
        try
        {
            int written = packet.Serialize(lease.SpanFull);
            lease.CommitLength(written);

            try
            {
                Assert.True(lease.Length >= reportedLength,
                    string.Format(
                        "BufferLease length ({0}) < packet.Length ({1}). SDK allocated only the static-size portion.",
                        lease.Length, reportedLength));

                Assert.Equal(reportedLength, written);

                byte[] frameBytes = lease.Memory.ToArray();
                Assert.True(frameBytes.Length >= payload.Length,
                    string.Format("Frame ({0} bytes) shorter than payload ({1} bytes).",
                        frameBytes.Length, payload.Length));

                ReadOnlySpan<byte> tail = frameBytes.AsSpan(frameBytes.Length - payload.Length);
                Assert.True(tail.SequenceEqual(payload.AsSpan()),
                    string.Format(
                        "Payload not found at tail of frame.{0}  expected = {1}{0}  actual   = {2}{0}  frame hex= {3}",
                        System.Environment.NewLine,
                        FormatHex(payload),
                        FormatHex(tail.ToArray()),
                        FormatHex(frameBytes)));
            }
            catch (Exception ex)
            {
                Assert.Fail(string.Format(
                    "FAIL: SDK send-frame for RuntimeObservation.{0}  packet.Length     = {1}{0}  written           = {2}{0}  lease.Length       = {3}{0}  lease.Capacity     = {4}{0}  payload.Length     = {5}{0}  Exception:{0}{6}",
                    System.Environment.NewLine,
                    reportedLength, written,
                    lease.Length, lease.Capacity,
                    payload.Length,
                    FormatExceptionChain(ex)));
            }
        }
        finally
        {
            lease.Dispose();
        }
    }

    [Fact]
    public void Serialize_WithLargeObservationData_DoesNotTruncate()
    {
        byte[] payload = new byte[256];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i & 0xFF);
        }

        RuntimeObservation packet = RuntimeObservation.Create();
        packet.Initialize(
            RuntimeObservationStage.RESPONSE,
            RuntimeObservationTarget.DISPATCH,
            ProtocolReason.NONE,
            payload);

        int reportedLength = packet.Length;
        int expectedLength = PacketSchema<RuntimeObservation>.StaticSize + payload.Length;

        Assert.Equal(expectedLength, reportedLength);

        BufferLease lease = BufferLease.Rent(reportedLength);
        try
        {
            int written = packet.Serialize(lease.SpanFull);
            lease.CommitLength(written);

            Assert.Equal(reportedLength, written);
            Assert.Equal(reportedLength, lease.Length);

            byte[] frameBytes = lease.Memory.ToArray();
            ReadOnlySpan<byte> tail = frameBytes.AsSpan(frameBytes.Length - payload.Length);
            Assert.True(tail.SequenceEqual(payload.AsSpan()),
                string.Format(
                    "Large payload truncated in SDK frame.{0}  frame.Length = {1}{0}  payload.Length = {2}{0}  tail[0..16] = {3}",
                    System.Environment.NewLine,
                    frameBytes.Length,
                    payload.Length,
                    FormatHex(tail[..16].ToArray())));
        }
        finally
        {
            lease.Dispose();
        }
    }

    [Fact]
    public void Serialize_ThenDeserialize_PreservesObservationData()
    {
        byte[] payload = [0xDE, 0xAD, 0xBE, 0xEF];

        RuntimeObservation packet = RuntimeObservation.Create();
        packet.Initialize(
            RuntimeObservationStage.RESPONSE,
            RuntimeObservationTarget.INSTANCES,
            ProtocolReason.NONE,
            payload);

        byte[] bytes = packet.Serialize();
        RuntimeObservation result = RuntimeObservation.Deserialize(bytes);

        Assert.NotNull(result);
        Assert.Equal(RuntimeObservationStage.RESPONSE, result.Stage);
        Assert.Equal(RuntimeObservationTarget.INSTANCES, result.Target);
        Assert.True(payload.AsSpan().SequenceEqual(result.ObservationData.Span),
            string.Format(
                "Roundtrip failed on desktop runtime.{0}  expected = {1}{0}  actual   = {2}",
                System.Environment.NewLine,
                FormatHex(payload),
                FormatHex(result.ObservationData.ToArray())));
    }

    private static string FormatExceptionChain(Exception? exception)
    {
        StringBuilder builder = new();
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            builder.AppendLine(string.Format("--- {0} ---", current.GetType().FullName));
            builder.AppendLine(current.Message);
            if (current.StackTrace is not null)
            {
                builder.AppendLine(current.StackTrace);
            }
            builder.AppendLine();
        }
        return builder.ToString();
    }

    private static string FormatHex(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) { return "(empty)"; }
        StringBuilder sb = new(bytes.Length * 3);
        for (int i = 0; i < bytes.Length; i++)
        {
            if (i > 0) { sb.Append(' '); }
            sb.Append(bytes[i].ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    private static string FormatHex(byte[] bytes) => FormatHex(bytes.AsSpan());
}
