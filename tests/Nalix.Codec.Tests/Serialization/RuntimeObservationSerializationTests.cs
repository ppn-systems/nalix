// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Codec.DataFrames;
using Nalix.Codec.ProtocolFrames;
using Nalix.Codec.Serialization;
using Nalix.Observability.Contracts;

namespace Nalix.Codec.Tests.Serialization;

public sealed class RuntimeObservationSerializationTests
{
    [Fact]
    public void Get_WhenTypeIsRuntimeObservation_ResolvesFormatter()
    {
        Exception? caught = Record.Exception(() =>
        {
            IFormatter<RuntimeObservation> formatter = FormatterProvider.Get<RuntimeObservation>();
            Assert.NotNull(formatter);
        });

        if (caught is not null)
        {
            Assert.Fail(
                string.Format("FormatterProvider.Get<RuntimeObservation>() threw.{0}{1}",
                    System.Environment.NewLine, FormatExceptionChain(caught)));
        }
    }

    [Fact]
    public void Deserialize_WhenRuntimeObservationWasSerialized_PreservesAllFields()
    {
        RuntimeObservation source = CreateRuntimeObservationTestPacket();

        byte[] bytes = source.Serialize();
        Assert.True(bytes.Length > 0, "Serialize produced zero bytes.");
        Assert.Equal(source.Length, bytes.Length);

        RuntimeObservation result = RuntimeObservation.Deserialize(bytes);

        Assert.NotNull(result);
        Assert.Equal(source.Header.OpCode, result.Header.OpCode);
        Assert.Equal(source.Stage, result.Stage);
        Assert.Equal(source.Target, result.Target);
        Assert.Equal(source.Reason, result.Reason);
        Assert.True(source.ObservationData.Span.SequenceEqual(result.ObservationData.Span),
            string.Format("ObservationData mismatch: expected {0} bytes, got {1} bytes.",
                source.ObservationData.Length, result.ObservationData.Length));
    }

    [Fact]
    public void Serialize_WhenDestinationIsSpan_WritesReportedLength()
    {
        RuntimeObservation source = CreateRuntimeObservationTestPacket();

        byte[] buffer = new byte[source.Length];
        int written = source.Serialize(buffer);

        Assert.Equal(source.Length, written);
        Assert.True(written > 0);

        RuntimeObservation result = RuntimeObservation.Deserialize(buffer);

        Assert.NotNull(result);
        Assert.Equal(source.Stage, result.Stage);
        Assert.Equal(source.Target, result.Target);
    }

    [Fact]
    public void StaticOpCode_WhenRuntimeObservationIsCreated_MatchesProtocolOpCode()
    {
        RuntimeObservation packet = RuntimeObservation.Create();

        ushort opcode = packet.Header.OpCode;
        ushort staticOpcode = RuntimeObservation.StaticOpCode;

        Assert.Equal((ushort)ProtocolOpCode.RUNTIME_OBSERVATION, staticOpcode);
        Assert.Equal(staticOpcode, opcode);
        Assert.NotEqual((ushort)0, opcode);
        Assert.True(opcode <= 0xFF,
            string.Format("OpCode 0x{0:X4} exceeds the reserved system range 0x00-0xFF.", opcode));
    }

    [Fact]
    public void StaticSize_WhenRuntimeObservationSchemaIsBuilt_IsPopulated()
    {
        int staticSize = PacketSchema<RuntimeObservation>.StaticSize;

        Assert.True(staticSize > 0,
            string.Format("PacketSchema<RuntimeObservation>.StaticSize is {0}.", staticSize));
        Assert.True(staticSize >= 4,
            string.Format("StaticSize {0} is too small.", staticSize));
    }

    [Fact]
    public void Get_WhenTypeIsControl_ResolvesFormatter()
    {
        IFormatter<Control> formatter = FormatterProvider.Get<Control>();
        Assert.NotNull(formatter);
    }

    [Fact]
    public void Deserialize_WhenControlWasSerialized_PreservesPublicState()
    {
        using Control source = Control.Create();
        source.Initialize(ControlType.PING, sequenceId: 42, flags: PacketFlags.SYSTEM, reasonCode: ProtocolReason.NONE);

        byte[] bytes = source.Serialize();
        Assert.True(bytes.Length > 0);
        Assert.Equal(source.Length, bytes.Length);

        Control result = Control.Deserialize(bytes);

        Assert.NotNull(result);
        Assert.Equal(source.Header.OpCode, result.Header.OpCode);
        Assert.Equal(source.Type, result.Type);
        Assert.Equal(source.Reason, result.Reason);
    }

    [Fact]
    public void StaticOpCode_WhenControlIsCreated_MatchesProtocolOpCode()
    {
        using Control packet = Control.Create();

        ushort opcode = packet.Header.OpCode;
        ushort staticOpcode = Control.StaticOpCode;

        Assert.Equal((ushort)ProtocolOpCode.SYSTEM_CONTROL, staticOpcode);
        Assert.Equal(staticOpcode, opcode);
        Assert.NotEqual((ushort)0, opcode);
    }

    [Fact]
    public void Get_WhenRuntimeObservationAndControlAreRequested_BothResolve()
    {
        IFormatter<RuntimeObservation>? roFormatter = null;
        IFormatter<Control>? ctrlFormatter = null;
        Exception? roEx = Record.Exception(() => roFormatter = FormatterProvider.Get<RuntimeObservation>());
        Exception? ctrlEx = Record.Exception(() => ctrlFormatter = FormatterProvider.Get<Control>());

        Assert.Null(ctrlEx);
        Assert.NotNull(ctrlFormatter);

        if (roEx is not null)
        {
            Assert.Fail(
                string.Format("RuntimeObservation formatter failed but Control succeeded.{0}Control type: {1}{0}RO exception:{0}{2}",
                    System.Environment.NewLine, ctrlFormatter!.GetType().FullName, FormatExceptionChain(roEx)));
        }

        Assert.NotNull(roFormatter);
    }

    [Fact]
    public void Length_WhenRuntimeObservationIsSerialized_MatchesByteCount()
    {
        RuntimeObservation packet = CreateRuntimeObservationTestPacket();
        int reportedLength = packet.Length;
        byte[] bytes = packet.Serialize();
        Assert.Equal(bytes.Length, reportedLength);
    }

    [Fact]
    public void Length_WhenObservationDataIsEmpty_MatchesByteCount()
    {
        RuntimeObservation packet = RuntimeObservation.Create();
        packet.Initialize(RuntimeObservationStage.REQUEST, RuntimeObservationTarget.DISPATCH);
        int reportedLength = packet.Length;
        byte[] bytes = packet.Serialize();
        Assert.Equal(bytes.Length, reportedLength);
    }

    [Fact]
    public void Serialize_WhenUsingLiteSerializer_ProducesBytes()
    {
        RuntimeObservation source = CreateRuntimeObservationTestPacket();
        byte[] bytes = LiteSerializer.Serialize(source);
        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
    }

    [Fact]
    public void Deserialize_WhenRequestHasEmptyObservationData_PreservesFields()
    {
        RuntimeObservation source = RuntimeObservation.Create();
        source.Initialize(RuntimeObservationStage.REQUEST, RuntimeObservationTarget.TASKS);

        byte[] bytes = source.Serialize();
        RuntimeObservation result = RuntimeObservation.Deserialize(bytes);

        Assert.NotNull(result);
        Assert.Equal(RuntimeObservationStage.REQUEST, result.Stage);
        Assert.Equal(RuntimeObservationTarget.TASKS, result.Target);
        Assert.Equal(ProtocolReason.NONE, result.Reason);
        Assert.True(result.ObservationData.IsEmpty);
    }

    [Fact]
    public void Get_WhenTypeIsObservabilityAccess_ResolvesFormatter()
    {
        Exception? caught = Record.Exception(() =>
        {
            IFormatter<ObservabilityAccess> formatter = FormatterProvider.Get<ObservabilityAccess>();
            Assert.NotNull(formatter);
        });

        if (caught is not null)
        {
            Assert.Fail(
                string.Format("FormatterProvider.Get<ObservabilityAccess>() threw.{0}{1}",
                    System.Environment.NewLine, FormatExceptionChain(caught)));
        }
    }

    [Fact]
    public void Deserialize_WhenObservabilityAccessWasSerialized_PreservesStage()
    {
        ObservabilityAccess source = ObservabilityAccess.Create();
        source.Initialize(ObservabilityAccessStage.REQUEST, reason: ProtocolReason.NONE, accessKey: default);

        byte[] bytes = source.Serialize();
        Assert.True(bytes.Length > 0);

        ObservabilityAccess result = ObservabilityAccess.Deserialize(bytes);
        Assert.NotNull(result);
        Assert.Equal(ObservabilityAccessStage.REQUEST, result.Stage);
    }

    // =====================================================================
    //  Regression tests: RuntimeObservation variable-size payload
    // =====================================================================

    /// <summary>
    /// Proves that the generated <see cref="RuntimeObservation.Length"/> property
    /// includes the dynamic <c>ObservationData</c> payload bytes, not just the
    /// 4-byte length-prefix. A static-size-only bug would report a shorter length.
    /// </summary>
    [Fact]
    public void Length_WhenObservationDataIsNonEmpty_IncludesDynamicPayload()
    {
        byte[] payload = [1, 2, 3, 4, 5, 6, 7, 8];

        RuntimeObservation packet = RuntimeObservation.Create();
        packet.Initialize(
            RuntimeObservationStage.RESPONSE,
            RuntimeObservationTarget.INSTANCES,
            ProtocolReason.NONE,
            payload);

        // Expected: Header(6) + Stage(1) + Target(1) + Reason(2) + LengthPrefix(4) + Payload(8) = 22
        int expectedMinLength = PacketSchema<RuntimeObservation>.StaticSize + payload.Length;

        try
        {
            Assert.True(packet.Length >= expectedMinLength,
                string.Format(
                    "packet.Length ({0}) < StaticSize ({1}) + payload.Length ({2}). " +
                    "Generated Length likely ignores dynamic ObservationData bytes.",
                    packet.Length, PacketSchema<RuntimeObservation>.StaticSize, payload.Length));

            Assert.Equal(
                PacketSchema<RuntimeObservation>.StaticSize + payload.Length,
                packet.Length);
        }
        catch (Exception ex)
        {
            Assert.Fail(string.Format(
                "FAIL: RuntimeObservation Length does not include dynamic payload.{0}" +
                "  expected min length = {1}{0}" +
                "  actual packet.Length = {2}{0}" +
                "  PacketSchema.StaticSize = {3}{0}" +
                "  ObservationData.Length = {4}{0}" +
                "  payload byte count    = {5}{0}" +
                "  Exception:{0}{6}",
                System.Environment.NewLine,
                expectedMinLength,
                packet.Length,
                PacketSchema<RuntimeObservation>.StaticSize,
                packet.ObservationData.Length,
                payload.Length,
                FormatExceptionChain(ex)));
        }
    }

    /// <summary>
    /// Serializes <see cref="RuntimeObservation"/> with non-empty <c>ObservationData</c>
    /// and asserts that the actual number of written bytes equals the reported
    /// <see cref="RuntimeObservation.Length"/>. Also asserts the tail bytes of the
    /// serialized output match the payload.
    /// </summary>
    [Fact]
    public void Serialize_WhenObservationDataIsNonEmpty_ByteCountMatchesLength()
    {
        byte[] payload = [10, 20, 30, 40, 50];

        RuntimeObservation packet = RuntimeObservation.Create();
        packet.Initialize(
            RuntimeObservationStage.RESPONSE,
            RuntimeObservationTarget.INSTANCES,
            ProtocolReason.NONE,
            payload);

        int reportedLength = packet.Length;
        byte[] serialized = packet.Serialize();

        try
        {
            Assert.Equal(reportedLength, serialized.Length);

            // The last N bytes of the serialized output must be the payload.
            Assert.True(serialized.Length >= payload.Length,
                string.Format("Serialized output ({0} bytes) is shorter than payload ({1} bytes).",
                    serialized.Length, payload.Length));

            ReadOnlySpan<byte> tail = serialized.AsSpan(serialized.Length - payload.Length);
            Assert.True(tail.SequenceEqual(payload.AsSpan()),
                string.Format(
                    "Payload bytes not found at tail of serialized output.{0}" +
                    "  expected tail = {1}{0}" +
                    "  actual tail   = {2}{0}" +
                    "  full hex      = {3}",
                    System.Environment.NewLine,
                    FormatHex(payload),
                    FormatHex(tail.ToArray()),
                    FormatHex(serialized)));
        }
        catch (Exception ex)
        {
            Assert.Fail(string.Format(
                "FAIL: Serialized byte count or payload tail mismatch.{0}" +
                "  packet.Length    = {1}{0}" +
                "  serialized.Length= {2}{0}" +
                "  payload.Length   = {3}{0}" +
                "  payload hex      = {4}{0}" +
                "  serialized hex   = {5}{0}" +
                "  Exception:{0}{6}",
                System.Environment.NewLine,
                reportedLength,
                serialized.Length,
                payload.Length,
                FormatHex(payload),
                FormatHex(serialized),
                FormatExceptionChain(ex)));
        }
    }

    /// <summary>
    /// Roundtrip test with non-empty <c>ObservationData</c>.
    /// Verifies that <c>Stage</c>, <c>Target</c>, <c>Reason</c>, and the exact
    /// payload bytes survive serialization and deserialization.
    /// </summary>
    [Fact]
    public void Deserialize_WhenObservationDataIsNonEmpty_PreservesPayload()
    {
        byte[] payload = [1, 3, 5, 7, 9, 11];

        RuntimeObservation source = RuntimeObservation.Create();
        source.Initialize(
            RuntimeObservationStage.RESPONSE,
            RuntimeObservationTarget.INSTANCES,
            ProtocolReason.NONE,
            payload);

        byte[] bytes = source.Serialize();
        RuntimeObservation result = RuntimeObservation.Deserialize(bytes);

        try
        {
            Assert.NotNull(result);
            Assert.Equal(source.Stage, result.Stage);
            Assert.Equal(source.Target, result.Target);
            Assert.Equal(source.Reason, result.Reason);
            Assert.Equal(source.Header.OpCode, result.Header.OpCode);
            Assert.False(result.ObservationData.IsEmpty,
                "Deserialized ObservationData is empty — payload was lost.");
            Assert.Equal(payload.Length, result.ObservationData.Length);

            Assert.True(payload.AsSpan().SequenceEqual(result.ObservationData.Span),
                string.Format(
                    "ObservationData roundtrip mismatch.{0}" +
                    "  expected = {1}{0}" +
                    "  actual   = {2}",
                    System.Environment.NewLine,
                    FormatHex(payload),
                    FormatHex(result.ObservationData.ToArray())));
        }
        catch (Exception ex)
        {
            Assert.Fail(string.Format(
                "FAIL: RuntimeObservation roundtrip.{0}" +
                "  source.Stage      = {1}{0}" +
                "  source.Target     = {2}{0}" +
                "  source.Reason     = {3}{0}" +
                "  source.Data.Len   = {4}{0}" +
                "  result?.Stage     = {5}{0}" +
                "  result?.Target    = {6}{0}" +
                "  result?.Reason    = {7}{0}" +
                "  result?.Data.Len  = {8}{0}" +
                "  payload hex       = {9}{0}" +
                "  Exception:{0}{10}",
                System.Environment.NewLine,
                source.Stage, source.Target, source.Reason, source.ObservationData.Length,
                result?.Stage, result?.Target, result?.Reason, result?.ObservationData.Length,
                FormatHex(payload),
                FormatExceptionChain(ex)));
        }
    }

    /// <summary>
    /// Comparison test: verifies that a fixed-size packet (<see cref="Control"/>)
    /// and a variable-size packet (<see cref="RuntimeObservation"/>) both produce
    /// correct serialized byte counts. If only RuntimeObservation fails, the
    /// regression is specific to variable-size source-generated metadata.
    /// </summary>
    [Fact]
    public void Length_WhenFixedAndVariableSizePacketsAreCompared_MatchSerializedByteCounts()
    {
        // Control is fixed-size; always works.
        using Control control = Control.Create();
        control.Initialize(ControlType.PING, sequenceId: 1, flags: PacketFlags.SYSTEM, reasonCode: ProtocolReason.NONE);

        byte[] controlBytes = control.Serialize();
        Assert.Equal(control.Length, controlBytes.Length);
        Assert.Equal(PacketSchema<Control>.StaticSize, control.Length);

        // RuntimeObservation is variable-size with payload.
        byte[] payload = [0xAA, 0xBB, 0xCC];
        RuntimeObservation ro = RuntimeObservation.Create();
        ro.Initialize(
            RuntimeObservationStage.RESPONSE,
            RuntimeObservationTarget.DISPATCH,
            ProtocolReason.NONE,
            payload);

        byte[] roBytes = ro.Serialize();

        // Variable-size must be strictly larger than StaticSize when payload is non-empty.
        Assert.True(ro.Length > PacketSchema<RuntimeObservation>.StaticSize,
            string.Format(
                "RuntimeObservation.Length ({0}) == StaticSize ({1}) despite non-empty ObservationData ({2} bytes). " +
                "Generated length is treating packet as static/fixed-size.",
                ro.Length, PacketSchema<RuntimeObservation>.StaticSize, payload.Length));

        Assert.Equal(ro.Length, roBytes.Length);
    }

    /// <summary>
    /// Varies the payload size to prove the generated Length scales with
    /// <c>ObservationData.Length</c>, not a compile-time constant.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(16)]
    [InlineData(256)]
    [InlineData(1024)]
    public void Length_WhenObservationDataSizeVaries_ScalesWithPayload(int payloadSize)
    {
        byte[] payload = new byte[payloadSize];
        for (int i = 0; i < payloadSize; i++)
        {
            payload[i] = (byte)(i & 0xFF);
        }

        RuntimeObservation packet = RuntimeObservation.Create();
        packet.Initialize(
            RuntimeObservationStage.RESPONSE,
            RuntimeObservationTarget.DISPATCH,
            ProtocolReason.NONE,
            payload);

        int expectedLength = PacketSchema<RuntimeObservation>.StaticSize + payloadSize;
        byte[] serialized = packet.Serialize();

        Assert.Equal(expectedLength, packet.Length);
        Assert.Equal(expectedLength, serialized.Length);

        if (payloadSize > 0)
        {
            // Verify payload bytes are present in the serialized output.
            ReadOnlySpan<byte> tail = serialized.AsSpan(serialized.Length - payloadSize);
            Assert.True(tail.SequenceEqual(payload.AsSpan()),
                string.Format(
                    "Payload not at tail for payloadSize={0}.{1}  expected = {2}{1}  actual   = {3}",
                    payloadSize, System.Environment.NewLine,
                    FormatHex(payload[..Math.Min(16, payloadSize)]),
                    FormatHex(tail[..Math.Min(16, tail.Length)].ToArray())));
        }
    }

    /// <summary>
    /// Verifies that the <see cref="PacketSchema{RuntimeObservation}.StaticSize"/>
    /// is the expected static overhead: Header(6) + Stage(1) + Target(1) +
    /// Reason(2) + LengthPrefix(4) = 14.
    /// If this value is wrong, all dynamic-length tests will produce incorrect
    /// expected values, masking the real bug.
    /// </summary>
    [Fact]
    public void StaticSize_WhenRuntimeObservationSchemaIsBuilt_IsFourteenBytes()
    {
        int staticSize = PacketSchema<RuntimeObservation>.StaticSize;

        // Header(6) + Stage(1) + Target(1) + Reason(2) + LengthPrefix(4) = 14
        Assert.Equal(14, staticSize);
    }

    // =====================================================================
    //  Diagnostic helpers
    // =====================================================================

    private static RuntimeObservation CreateRuntimeObservationTestPacket()
    {
        RuntimeObservation packet = RuntimeObservation.Create();
        ReadOnlyMemory<byte> payload = new ReadOnlyMemory<byte>(
            Encoding.UTF8.GetBytes("{\"dispatch\":{\"queue_depth\":42,\"processed\":1000}}"));
        packet.Initialize(
            RuntimeObservationStage.RESPONSE,
            RuntimeObservationTarget.DISPATCH,
            ProtocolReason.NONE,
            payload);
        return packet;
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
        if (bytes.IsEmpty)
        {
            return "(empty)";
        }

        StringBuilder sb = new(bytes.Length * 3);
        for (int i = 0; i < bytes.Length; i++)
        {
            if (i > 0)
            {
                _ = sb.Append(' ');
            }
            _ = sb.Append(bytes[i].ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    private static string FormatHex(byte[] bytes) => FormatHex(bytes.AsSpan());
}
