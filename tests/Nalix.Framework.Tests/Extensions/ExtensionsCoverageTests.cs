// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Buffers.Binary;
using Nalix.Abstractions;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Primitives;
using Nalix.Codec.Extensions;
using Nalix.Environment.Extensions;
using Nalix.Framework.Extensions;

namespace Nalix.Framework.Tests.Extensions;

public sealed class ExtensionsCoverageTests
{
    [Fact]
    public void EnumExtensionsWhenUsingByteFlagsAddAndRemoveReturnExpectedValues()
    {
        ByteFlags value = ByteFlags.None;
        value = value.AddFlag(ByteFlags.A);
        value = value.AddFlag(ByteFlags.C);

        Assert.Equal(ByteFlags.A | ByteFlags.C, value);

        value = value.RemoveFlag(ByteFlags.A);
        Assert.Equal(ByteFlags.C, value);
    }

    [Fact]
    public void EnumExtensionsWhenUsingUShortFlagsAddAndRemoveReturnExpectedValues()
    {
        UShortFlags value = UShortFlags.None;
        value = value.AddFlag(UShortFlags.B);
        value = value.AddFlag(UShortFlags.C);

        Assert.Equal(UShortFlags.B | UShortFlags.C, value);

        value = value.RemoveFlag(UShortFlags.B);
        Assert.Equal(UShortFlags.C, value);
    }

    [Fact]
    public void EnumExtensionsWhenUsingUIntFlagsAddAndRemoveReturnExpectedValues()
    {
        UIntFlags value = UIntFlags.None;
        value = value.AddFlag(UIntFlags.A);
        value = value.AddFlag(UIntFlags.B);

        Assert.Equal(UIntFlags.A | UIntFlags.B, value);

        value = value.RemoveFlag(UIntFlags.A);
        Assert.Equal(UIntFlags.B, value);
    }

    [Fact]
    public void EnumExtensionsWhenUsingULongFlagsAddAndRemoveReturnExpectedValues()
    {
        ULongFlags value = ULongFlags.None;
        value = value.AddFlag(ULongFlags.A);
        value = value.AddFlag(ULongFlags.C);

        Assert.Equal(ULongFlags.A | ULongFlags.C, value);

        value = value.RemoveFlag(ULongFlags.A);
        Assert.Equal(ULongFlags.C, value);
    }

    // EnumExtensions validates the [Flags] contract in Debug builds only.
#if DEBUG
    [Fact]
    public void EnumExtensionsWhenTypeIsNotFlagsThrowsArgumentExceptionInDebugBuilds()
    {
        NotFlags value = NotFlags.A;

        Assert.Throws<ArgumentException>(() => value.AddFlag(NotFlags.B));
        Assert.Throws<ArgumentException>(() => value.RemoveFlag(NotFlags.B));
    }
#endif

    [Fact]
    public void TaskAwaitWhenTaskIsNullThrowsArgumentNullException()
    {
        Task? task = null;

        Assert.Throws<ArgumentNullException>(() => task!.Await());
    }

    [Fact]
    public void TaskAwaitWhenTaskFaultsRethrowsOriginalException()
    {
        InvalidOperationException expected = new("boom");
        Task faulted = Task.FromException(expected);

        InvalidOperationException actual = Assert.Throws<InvalidOperationException>(() => faulted.Await());
        Assert.Same(expected, actual);
    }

    [Fact]
    public void TaskAwaitWithResultReturnsValue()
    {
        Task<int> task = Task.FromResult(123);

        int value = task.Await();

        Assert.Equal(123, value);
    }

    [Fact]
    public void TaskAwaitWithConfigureAwaitFlagStillCompletes()
    {
        Task task = Task.CompletedTask;

        task.Await(continueOnCapturedContext: false);
    }

    [Fact]
    public void ValueTaskAwaitWithNoResultCompletes()
    {
        ValueTask task = ValueTask.CompletedTask;

        task.Await();
    }

    [Fact]
    public void ValueTaskAwaitWithResultReturnsValue()
    {
        ValueTask<int> task = ValueTask.FromResult(42);

        int value = task.Await();

        Assert.Equal(42, value);
    }

    [Fact]
    public async Task WithTimeoutWhenTaskCompletesBeforeDeadlineReturnsResult()
    {
        Task<int> task = Task.FromResult(9);

        int? result = await task.WithTimeout(100);

        Assert.Equal(9, result);
    }

    [Fact]
    public async Task WithTimeoutWhenValueTypeTaskDoesNotCompleteReturnsDefaultValue()
    {
        // Increase delay to 5s to ensure timeout (50ms) always wins even on slow runners
        Task<int> task = Task.Delay(5000).ContinueWith(_ => 7, TaskScheduler.Default);

        int? result = await task.WithTimeout(50);

        // For value types in unconstrained generics, default is 0, not null
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task WithTimeoutWhenReferenceTypeTaskDoesNotCompleteReturnsNull()
    {
        // Increase delay to 5000ms to ensure 50ms timeout wins
        Task<string> task = Task.Delay(5000).ContinueWith(_ => "done", TaskScheduler.Default);

        string? result = await task.WithTimeout(50);

        Assert.Null(result);
    }

    [Fact]
    public async Task WithTimeoutWhenTaskIsNullThrowsArgumentNullException()
    {
        Task<int>? task = null;

        await Assert.ThrowsAsync<ArgumentNullException>(() => task!.WithTimeout(10));
    }

    [Fact]
    public async Task WithTimeoutWhenTimeoutIsLessThanMinusOneThrowsArgumentOutOfRangeException()
    {
        Task<int> task = Task.FromResult(1);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => task.WithTimeout(-2));
    }

    [Fact]
    public void LinkCancellationWhenTokenIsNotCancelableReturnsDisposableAndDoesNotCancel()
    {
        TaskCompletionSource<int> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        using IDisposable registration = tcs.LinkCancellation(CancellationToken.None);

        Assert.False(tcs.Task.IsCanceled);
    }

    [Fact]
    public void LinkCancellationWhenTokenCancelsTransitionsTaskToCanceled()
    {
        TaskCompletionSource<int> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource cts = new();
        using IDisposable registration = tcs.LinkCancellation(cts.Token);

        cts.Cancel();

        Assert.True(tcs.Task.IsCanceled);
    }

    [Fact]
    public void LinkCancellationWhenTcsIsNullThrowsArgumentNullException()
    {
        TaskCompletionSource<int>? tcs = null;

        Assert.Throws<ArgumentNullException>(() => tcs!.LinkCancellation(CancellationToken.None));
    }

    [Fact]
    public void HeaderExtensionsWhenBufferContainsValuesReadsExpectedFields()
    {
        byte[] buffer = new byte[(int)PacketHeaderOffset.Region];
        const ushort opCode = 0x7788;
        const ushort sequence = 0x1234;

        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan((int)PacketHeaderOffset.OpCode), opCode);
        buffer[(int)PacketHeaderOffset.Flags] = (byte)PacketFlags.ENCRYPTED;
        buffer[(int)PacketHeaderOffset.Priority] = (byte)PacketPriority.HIGH;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan((int)PacketHeaderOffset.SequenceId), sequence);

        PacketHeader header = buffer.AsSpan().AsHeaderRef();
        Assert.Equal(opCode, header.OpCode);
        Assert.Equal(PacketFlags.ENCRYPTED, header.Flags);
        Assert.Equal(PacketPriority.HIGH, header.Priority);
        Assert.Equal(sequence, header.SequenceId);
    }

    [Fact]
    public void HeaderExtensionsWriteHeaderWritesAllFields()
    {
        byte[] buffer = new byte[(int)PacketHeaderOffset.Region];
        PacketHeader header = new()
        {
            OpCode = 0x7788,
            Flags = PacketFlags.NONE,
            Priority = PacketPriority.HIGH,
            SequenceId = 0x1234
        };

        buffer.AsSpan().AsHeaderRef() = header;

        PacketHeader read = buffer.AsSpan().AsHeaderRef();
        Assert.Equal(header.OpCode, read.OpCode);
        Assert.Equal(header.Flags, read.Flags);
        Assert.Equal(header.Priority, read.Priority);
        Assert.Equal(header.SequenceId, read.SequenceId);
    }

    [Fact]
    public void HeaderExtensionsWhenBufferTooSmallThrowsArgumentException()
    {
        byte[] small = new byte[2];

        Assert.Throws<ArgumentException>(() => small.AsSpan().AsHeaderRef());
        Assert.Throws<ArgumentException>(() => { small.AsSpan().AsHeaderRef() = default; });
    }

    [Fact]
    public void ReportExtensionsWhenReportableIsNullThrowsArgumentNullException()
    {
        IReportable? reportable = null;

        Assert.Throws<ArgumentNullException>(() => reportable!.SaveReportToFile());
    }

    [Fact]
    public void ReportExtensionsSaveReportToFileCreatesFileAndPersistsContent()
    {
        FakeReportable reportable = new("hello-report");

        string path = reportable.SaveReportToFile("Prefix:*Value");

        try
        {
            Assert.True(File.Exists(path));
            Assert.Contains("prefix_", Path.GetFileName(path), StringComparison.OrdinalIgnoreCase);
            Assert.Equal("hello-report", File.ReadAllText(path));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Flags]
    private enum ByteFlags : byte
    {
        None = 0,
        A = 1,
        B = 2,
        C = 4
    }

    [Flags]
    private enum UShortFlags : ushort
    {
        None = 0,
        A = 1,
        B = 2,
        C = 4
    }

    [Flags]
    private enum UIntFlags : uint
    {
        None = 0,
        A = 1,
        B = 2
    }

    [Flags]
    private enum ULongFlags : ulong
    {
        None = 0,
        A = 1,
        B = 2,
        C = 4
    }

    private enum NotFlags : byte
    {
        A = 1,
        B = 2
    }

    private sealed class FakeReportable(string text) : IReportable
    {
        private readonly string _text = text;

        public string GenerateReport() => _text;

#if NET10_0_OR_GREATER
        public void WriteReportData(System.Text.Json.Utf8JsonWriter writer)
        {
            writer.WriteString("text", _text);
        }
#endif
    }
}
















