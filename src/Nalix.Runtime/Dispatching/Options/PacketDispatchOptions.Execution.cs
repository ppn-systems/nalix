// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.IO;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions.Diagnostics;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Runtime.Dispatching;
using Nalix.Runtime.Extensions;
using Nalix.Runtime.Internal.Compilation;
using Nalix.Runtime.Internal.Pooling;

namespace Nalix.Runtime.Routing;

public sealed partial class PacketDispatchOptions<TPacket>
{
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private ValueTask ExecuteHandlerAsync(PacketHandler<TPacket> descriptor, PacketContext<TPacket> context)
    {
        // If the packet was deserialized into the wrong runtime type, fail early and
        // send a protocol-level response instead of letting the handler crash later.
        Type? expectedType = descriptor.ExpectedPacketType;
        if (expectedType is not null && !expectedType.IsInstanceOfType(context.Packet))
        {
            return HandleTypeMismatchAsync(this, descriptor, context);
        }

        // Industrial-grade validation: if the packet implements IPacketValidatable,
        // we must ensure it's structurally and logically sound before letting
        // application code touch it.
        if (context.Packet is IPacketValidatable validatable && !validatable.Validate(out string? failureReason))
        {
            return HandleValidationFailureAsync(this, descriptor, context, failureReason);
        }

        // Void / Task / ValueTask handlers do not produce an outbound packet payload.
        context.SkipOutbound = HasNoOutboundResult(descriptor.ReturnType);

        if (!_pipeline.IsEmpty)
        {
            // The packet pipeline runs first so middleware can transform, validate, or short-circuit
            // the context before the actual handler executes.
            // Terminal handler is invoked directly — no per-packet delegate/closure allocated.
            ValueTask pending = _pipeline.ExecuteAsync(context, this, descriptor, context.CancellationToken);
            if (pending.IsCompletedSuccessfully)
            {
#pragma warning disable CA1849 // Completed-success fast path.
                pending.GetAwaiter().GetResult();
#pragma warning restore CA1849
                return default;
            }
            return AwaitPipelineCompletionAsync(pending);
        }
        else
        {
            ValueTask pending = this.ExecuteTerminalHandler(descriptor, context, context.CancellationToken);
            if (pending.IsCompletedSuccessfully)
            {
#pragma warning disable CA1849 // Completed-success fast path.
                pending.GetAwaiter().GetResult();
#pragma warning restore CA1849
                return default;
            }
            return AwaitHandlerCompletionAsync(pending);
        }
    }

    /// <summary>
    /// Executes the terminal packet handler with explicit parameters.
    /// This method replaces the former per-packet-captured InvokeHandlerAsync local function.
    /// No delegate, closure, or async state machine is allocated for synchronous completions.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal ValueTask ExecuteTerminalHandler(PacketHandler<TPacket> descriptor, PacketContext<TPacket> context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Permission / encryption policy check — sync, no allocation.
        if (!descriptor.CanExecute(context, out ProtocolReason denyReason))
        {
            // Cold path: denied — requires async I/O to send directive.
            return SendDenyControlAsync(this, descriptor, context, denyReason);
        }

        // Handler execution — try sync fast-path.
        ValueTask<object?> handlerResult = descriptor.ExecuteAsync(context);

        if (handlerResult.IsCompletedSuccessfully)
        {
            object? result = handlerResult.Result;

            if (context.SkipOutbound || result is null)
            {
                return default; // Hot path: zero allocation.
            }

            // Response needed — delegate to async helper.
            return SendHandlerResponseAsync(this, descriptor, context, result, ct);
        }

        // Handler didn't complete synchronously — async slow-path.
        return AwaitHandlerAndRespondAsync(this, descriptor, context, handlerResult, ct);
    }

    #region Terminal Handler Async Slow-Paths

    private static async ValueTask SendDenyControlAsync(
        PacketDispatchOptions<TPacket> owner, PacketHandler<TPacket> descriptor,
        PacketContext<TPacket> context, ProtocolReason reason)
    {
        try
        {
            // Policy denials that can clear up on their own (e.g. rate limiting) get a
            // transient retry hint; hard policy failures (e.g. missing required
            // encryption) are not retryable without changing how the client sends.
            bool transient = reason == ProtocolReason.RATE_LIMITED;

            await owner.TrySendControlAsync(
                context,
                descriptor.OpCode,
                controlType: ControlType.FAIL,
                reason: reason,
                action: transient ? ProtocolAdvice.RETRY : ProtocolAdvice.DO_NOT_RETRY,
                options: new ControlDirectiveOptions(
                    Flags: transient ? ControlFlags.IS_TRANSIENT : ControlFlags.NONE,
                    SequenceId: context.Packet.Header.SequenceId,
                    Arg0: descriptor.OpCode)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            await owner.HandleDispatchExceptionAsync(descriptor, context, ex)
                       .ConfigureAwait(false);
        }
    }

    private static async ValueTask SendHandlerResponseAsync(
        PacketDispatchOptions<TPacket> owner, PacketHandler<TPacket> descriptor,
        PacketContext<TPacket> context, object result, CancellationToken ct)
    {
        IPacket? responsePacket = null;
        try
        {
            if (result is IPacket packetResult)
            {
                responsePacket = packetResult;
                await AwaitReturnAsync(context.Sender.SendAsync(packetResult, ct), ct).ConfigureAwait(false);
            }
            else if (result is ReadOnlyMemory<byte> rom)
            {
                await SendRawAsync(context, rom).ConfigureAwait(false);
            }
            else if (result is byte[] arr)
            {
                await SendRawAsync(context, arr).ConfigureAwait(false);
            }
            else if (result is Memory<byte> mem)
            {
                await SendRawAsync(context, mem).ConfigureAwait(false);
            }
            else if (result is IAsyncEnumerable<IPacket> stream)
            {
                await SendStreamResponseAsync(context, stream, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            await owner.HandleDispatchExceptionAsync(descriptor, context, ex)
                       .ConfigureAwait(false);
        }
        finally
        {
            if (responsePacket is not null && !ReferenceEquals(responsePacket, context.Packet))
            {
                if (responsePacket is IDisposable disposableResponse)
                {
                    disposableResponse.Dispose();
                }
            }
        }
    }

    /// <summary>
    /// Sends every item a handler's <see cref="IAsyncEnumerable{IPacket}"/> return value yields, one
    /// packet per <c>ReplyAsync</c> call (each auto-stamped with the request's <c>SequenceId</c> by
    /// <see cref="Dispatching.PacketSender.ReplyAsync"/>, exactly like a single-packet response — plain
    /// <c>SendAsync</c> does NOT stamp it, which is what a client's <c>StreamAsync</c> correlates every
    /// chunk against). The handler writes a plain <c>async IAsyncEnumerable&lt;TResponse&gt;</c> method
    /// and never touches end-of-stream or correlation-id bookkeeping itself.
    /// </summary>
    /// <remarks>
    /// The LAST item the enumerable actually yields has its <see cref="IPacketStreamable.IsEndOfStream"/>
    /// set to <see langword="true"/> before it is sent (a one-item look-ahead: the loop asks for the
    /// next item before sending the current one, so it always knows whether the current item is the
    /// last). This is what <c>Nalix.SDK</c>'s <c>StreamExtensions.StreamAsync</c> watches for to
    /// complete its <c>IAsyncEnumerable</c> on the client, and it needs no separate terminator packet:
    /// the response type only has to implement <see cref="IPacketStreamable"/>, which the client's own
    /// generic constraint already requires.
    /// <para>
    /// <b>Known limitation:</b> an empty stream (zero items yielded) or a fault the enumerable throws
    /// before yielding a final item leaves the client's stream with no end-of-stream signal — there is
    /// no wire-level "abort this stream" frame today, and this method only ever sees the erased
    /// <c>object</c> result, so it cannot construct a fresh terminator of the concrete response type
    /// on the handler's behalf. A stream handler
    /// should always yield at least one item, and callers that need faults to fail fast rather than
    /// hang should pass a non-zero <c>inactivityTimeoutMs</c> to <c>StreamAsync</c>.
    /// </para>
    /// </remarks>
    private static async ValueTask SendStreamResponseAsync(
        PacketContext<TPacket> context, IAsyncEnumerable<IPacket> stream, CancellationToken ct)
    {
        await using IAsyncEnumerator<IPacket> enumerator = stream.GetAsyncEnumerator(ct);

        bool hasCurrent = await enumerator.MoveNextAsync().ConfigureAwait(false);

        while (hasCurrent)
        {
            IPacket current = enumerator.Current;
            bool disposeCurrent = !ReferenceEquals(current, context.Packet);

            try
            {
                hasCurrent = await enumerator.MoveNextAsync().ConfigureAwait(false);

                if (!hasCurrent && current is IPacketStreamable streamable)
                {
                    streamable.IsEndOfStream = true;
                }

                await AwaitReturnAsync(context.Sender.ReplyAsync(current, ct), ct).ConfigureAwait(false);
            }
            finally
            {
                if (disposeCurrent && current is IDisposable disposableCurrent)
                {
                    disposableCurrent.Dispose();
                }
            }
        }
    }

    private static async ValueTask AwaitHandlerAndRespondAsync(
        PacketDispatchOptions<TPacket> owner, PacketHandler<TPacket> descriptor,
        PacketContext<TPacket> context, ValueTask<object?> handlerResult, CancellationToken ct)
    {
        IPacket? responsePacket = null;
        try
        {
            object? result = await AwaitHandlerResultAsync(handlerResult, ct).ConfigureAwait(false);

            if (!context.SkipOutbound && result is not null)
            {
                if (result is IPacket packetResult)
                {
                    responsePacket = packetResult;
                    await AwaitReturnAsync(context.Sender.SendAsync(packetResult, ct), ct).ConfigureAwait(false);
                }
                else if (result is ReadOnlyMemory<byte> rom)
                {
                    await SendRawAsync(context, rom).ConfigureAwait(false);
                }
                else if (result is byte[] arr)
                {
                    await SendRawAsync(context, arr).ConfigureAwait(false);
                }
                else if (result is Memory<byte> mem)
                {
                    await SendRawAsync(context, mem).ConfigureAwait(false);
                }
                else if (result is IAsyncEnumerable<IPacket> stream)
                {
                    await SendStreamResponseAsync(context, stream, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            await owner.HandleDispatchExceptionAsync(descriptor, context, ex)
                       .ConfigureAwait(false);
        }
        finally
        {
            if (responsePacket is not null && !ReferenceEquals(responsePacket, context.Packet))
            {
                if (responsePacket is IDisposable disposableResponse)
                {
                    disposableResponse.Dispose();
                }
            }
        }
    }

    #endregion Terminal Handler Async Slow-Paths

    #region Terminal Handler Static Helpers

    private static async ValueTask HandleTypeMismatchAsync(PacketDispatchOptions<TPacket> owner, PacketHandler<TPacket> descriptor, PacketContext<TPacket> context)
    {
        Type? actualType = context.Packet?.GetType();
        IPacket? packet = context.Packet;

        if (packet is null)
        {
            return;
        }

        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Debug))
        {
            string actualName = actualType?.Name ?? "null";
            DiagnosticsEvents.Write(
                DiagnosticsEvents.Internal.Debug,
                new DiagnosticLog(
                    "RT.PacketDispatchOptions:ExecuteHandlerAsync",
                    $"type-mismatch opcode=0x{descriptor.OpCode:X4} expected={descriptor.ExpectedPacketType!.Name} actual={actualName}"));
        }

        await owner.TrySendControlAsync(
            context,
            descriptor.OpCode,
            controlType: ControlType.FAIL,
            reason: ProtocolReason.REQUEST_INVALID,
            action: ProtocolAdvice.FIX_AND_RETRY,
            options: new ControlDirectiveOptions(
                SequenceId: packet.Header.SequenceId,
                Arg0: descriptor.OpCode)).ConfigureAwait(false);
    }

    private static async ValueTask HandleValidationFailureAsync(PacketDispatchOptions<TPacket> owner, PacketHandler<TPacket> descriptor, PacketContext<TPacket> context, string? failureReason)
    {
        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Warning))
        {
            DiagnosticsEvents.Write(
                DiagnosticsEvents.Internal.Warning,
                new DiagnosticLog(
                    "RT.PacketDispatchOptions:ExecuteHandlerAsync",
                    $"validation-failed opcode=0x{descriptor.OpCode} reason={failureReason} skipping-handler"));
        }

        await owner.TrySendControlAsync(
            context,
            descriptor.OpCode,
            controlType: ControlType.FAIL,
            reason: ProtocolReason.MALFORMED_PACKET,
            action: ProtocolAdvice.FIX_AND_RETRY,
            options: new ControlDirectiveOptions(
                SequenceId: context.Packet.Header.SequenceId,
                Arg0: descriptor.OpCode)).ConfigureAwait(false);
    }

    private static async ValueTask AwaitPipelineCompletionAsync(ValueTask pending) => await pending.ConfigureAwait(false);

    private static async ValueTask AwaitHandlerCompletionAsync(ValueTask pending) => await pending.ConfigureAwait(false);

    private static async ValueTask<object?> AwaitHandlerResultAsync(ValueTask<object?> pending, CancellationToken token)
    {
        // Fast path: if the handler already completed, avoid an allocation and return the result directly.
        token.ThrowIfCancellationRequested();

        if (pending.IsCompletedSuccessfully)
        {
            return pending.Result;
        }

        return await CancellableValueTaskSource<object?>.Await(pending, token)
                                                         .ConfigureAwait(false);
    }

    private static async ValueTask AwaitReturnAsync(ValueTask pending, CancellationToken token)
    {
        // Same fast-path pattern as above, but for handlers that only need to emit side effects.
        token.ThrowIfCancellationRequested();

        if (pending.IsCompletedSuccessfully)
        {
#pragma warning disable CA1849 // Completed-success fast path; GetResult observes synchronous exceptions without blocking or allocating an async state machine.
            pending.GetAwaiter().GetResult();
#pragma warning restore CA1849
            return;
        }

        await CancellableValueTaskSource.Await(pending, token)
                                        .ConfigureAwait(false);
    }

    private static async ValueTask SendRawAsync(PacketContext<TPacket> context, ReadOnlyMemory<byte> data)
    {
        if (context.IsReliable || context.Connection.UDP is null)
        {
            await context.Connection.TCP.SendAsync(data).ConfigureAwait(false);
        }
        else
        {
            await context.Connection.UDP.SendAsync(data).ConfigureAwait(false);
        }
    }

    #endregion Terminal Handler Static Helpers

    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private async ValueTask HandleDispatchExceptionAsync(
        PacketHandler<TPacket> descriptor,
        PacketContext<TPacket> context, Exception exception)
    {
        // Connection teardown is expected during shutdown or remote disconnect, so do not
        // spam logs or send protocol failures for those paths.
        bool teardownException = IsConnectionTeardownException(exception);
        if (teardownException)
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Debug))
            {
                DiagnosticsEvents.Write(
                    DiagnosticsEvents.Internal.Debug,
                    new DiagnosticLog(
                        "RT.PacketDispatchOptions:HandleDispatchExceptionAsync",
                        $"teardown-suppressed opcode={descriptor.OpCode} ex={exception.GetType().Name}",
                        exception));
            }
        }
        else
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Error))
            {
                DiagnosticsEvents.Write(
                    DiagnosticsEvents.Internal.Error,
                    new DiagnosticLog(
                        "RT.PacketDispatchOptions:HandleDispatchExceptionAsync",
                        $"handler-failed opcode={descriptor.OpCode}",
                        exception));
            }
        }

        if (teardownException)
        {
            return;
        }

        // Custom error hooks run before the control reply so an application can record
        // the failure even if the outbound control message later cannot be delivered.
        _errorHandler?.Invoke(exception, descriptor.OpCode);

        (ProtocolReason reason, ProtocolAdvice action, ControlFlags flags) = MapExceptionToProtocol(exception);

        await this.TrySendControlAsync(
            context,
            descriptor.OpCode,
            controlType: ControlType.FAIL,
            reason: reason,
            action: action,
            options: new ControlDirectiveOptions(
                Flags: flags,
                SequenceId: context.Packet.Header.SequenceId,
                Arg0: descriptor.OpCode)).ConfigureAwait(false);
    }

    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static bool HasNoOutboundResult(Type returnType)
        => returnType == typeof(void)
        || returnType == typeof(Task)
        || returnType == typeof(ValueTask);

    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static T ThrowIfNull<T>(T value, string param) where T : class => value ?? throw new ArgumentNullException(param);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private async ValueTask TrySendControlAsync(
        PacketContext<TPacket> context,
        ushort opCode,
        ControlType controlType,
        ProtocolReason reason,
        ProtocolAdvice action,
        ControlDirectiveOptions options)
    {
        try
        {
            // Best-effort only: if the connection is already falling apart, the control message
            // is skipped and the original failure path is preserved.
            await context.Sender.SendAsync(
                controlType: controlType,
                reason: reason,
                action: action,
                options: options).ConfigureAwait(false);
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex) && IsConnectionTeardownException(ex))
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Debug))
            {
                DiagnosticsEvents.Write(
                    DiagnosticsEvents.Internal.Debug,
                    new DiagnosticLog(
                        "RT.PacketDispatchOptions:TrySendControlAsync",
                        $"control-send-skipped opcode={opCode} reason={reason} ex={ex.GetType().Name}",
                        ex));
            }
        }
    }

    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static bool IsConnectionTeardownException(Exception ex)
    {
        // Cancellation and object disposal are the normal shutdown signals.
        if (ex is OperationCanceledException or ObjectDisposedException)
        {
            return true;
        }

        if (ex is IOException io && io.InnerException is SocketException ioeSocket)
        {
            return IsTeardownSocketError(ioeSocket.SocketErrorCode);
        }

        if (ex is SocketException socketEx)
        {
            return IsTeardownSocketError(socketEx.SocketErrorCode);
        }

        Exception? inner = ex.InnerException;
        if (inner is null)
        {
            return false;
        }

        return IsConnectionTeardownException(inner);
    }

    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static bool IsTeardownSocketError(SocketError errorCode)
        => errorCode is SocketError.OperationAborted
        or SocketError.Interrupted
        or SocketError.ConnectionAborted
        or SocketError.ConnectionReset
        or SocketError.NotConnected
        or SocketError.Shutdown;

    /// <summary>
    /// Maps an exception to the protocol-level response that should be sent back to the peer.
    /// </summary>
    /// <param name="ex">The exception raised by handler execution or dispatch plumbing.</param>
    /// <exception cref="NotImplementedException"></exception>
    [Pure]
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static (ProtocolReason reason, ProtocolAdvice action, ControlFlags flags) MapExceptionToProtocol(Exception ex)
    {
        // 1) Cancellation/Timeout => transient
        if (ex is OperationCanceledException or TimeoutException)
        {
            return (ProtocolReason.TIMEOUT, ProtocolAdvice.RETRY, ControlFlags.IS_TRANSIENT);
        }

        // 2) Validation/Bad input
        if (ex is ArgumentException or FormatException ||
            ex.GetType().Name.Contains("Validation", StringComparison.OrdinalIgnoreCase))
        {
            return (ProtocolReason.REQUEST_INVALID, ProtocolAdvice.FIX_AND_RETRY, ControlFlags.NONE);
        }

        // 3) Unauthorized / security
        if (ex is UnauthorizedAccessException)
        {
            return (ProtocolReason.UNAUTHORIZED, ProtocolAdvice.NONE, ControlFlags.NONE);
        }

        if (ex is CipherException)
        {
            return (ProtocolReason.DECRYPTION_FAILED, ProtocolAdvice.REAUTHENTICATE, ControlFlags.NONE);
        }

        // 4) Unsupported / not implemented
        if (ex is NotSupportedException or NotImplementedException)
        {
            return (ProtocolReason.OPERATION_UNSUPPORTED, ProtocolAdvice.NONE, ControlFlags.NONE);
        }

        // 5) IO / socket => mostly transient
        if (ex is IOException ioEx && ioEx.InnerException is SocketException se1)
        {
            return MapSocketExceptionToProtocol(se1);
        }

        if (ex is SocketException se)
        {
            return MapSocketExceptionToProtocol(se);
        }

        // 6) ObjectDisposed trong teardown: coi như transient nhẹ
        if (ex is ObjectDisposedException)
        {
            return (ProtocolReason.NETWORK_ERROR, ProtocolAdvice.RETRY, ControlFlags.IS_TRANSIENT);
        }

        // 7) Default: internal error
        return (ProtocolReason.INTERNAL_ERROR, ProtocolAdvice.NONE, ControlFlags.NONE);

        static (ProtocolReason, ProtocolAdvice, ControlFlags) MapSocketExceptionToProtocol(SocketException se)
        {
            return se.SocketErrorCode switch
            {
                // Timeout
                SocketError.TimedOut => (ProtocolReason.TIMEOUT, ProtocolAdvice.RETRY, ControlFlags.IS_TRANSIENT),

                // Connection lifecycle
                SocketError.ConnectionReset => (ProtocolReason.CONNECTION_RESET, ProtocolAdvice.RETRY, ControlFlags.IS_TRANSIENT),
                SocketError.ConnectionRefused => (ProtocolReason.CONNECTION_REFUSED, ProtocolAdvice.RETRY, ControlFlags.IS_TRANSIENT),
                SocketError.ConnectionAborted => (ProtocolReason.REMOTE_CLOSED, ProtocolAdvice.RETRY, ControlFlags.IS_TRANSIENT),
                SocketError.Shutdown => (ProtocolReason.LOCAL_CLOSED, ProtocolAdvice.NONE, ControlFlags.NONE),
                SocketError.NotConnected => (ProtocolReason.NETWORK_ERROR, ProtocolAdvice.RETRY, ControlFlags.IS_TRANSIENT),

                // Network
                SocketError.NetworkDown or
                SocketError.NetworkUnreachable or
                SocketError.HostDown or
                SocketError.HostUnreachable => (ProtocolReason.NETWORK_ERROR, ProtocolAdvice.RETRY, ControlFlags.IS_TRANSIENT),
                SocketError.NetworkReset => (ProtocolReason.CONNECTION_RESET, ProtocolAdvice.RETRY, ControlFlags.IS_TRANSIENT),

                // DNS
                SocketError.HostNotFound or
                SocketError.TryAgain or
                SocketError.NoRecovery or
                SocketError.NoData => (ProtocolReason.DNS_FAILURE, ProtocolAdvice.RETRY, ControlFlags.IS_TRANSIENT),

                //  Flow / buffer
                SocketError.NoBufferSpaceAvailable => (ProtocolReason.RESOURCE_LIMIT, ProtocolAdvice.RETRY, ControlFlags.IS_TRANSIENT),
                SocketError.WouldBlock or
                SocketError.IOPending or
                SocketError.InProgress or
                SocketError.AlreadyInProgress => (ProtocolReason.TEMPORARY_FAILURE, ProtocolAdvice.RETRY, ControlFlags.IS_TRANSIENT),

                // Size / framing
                SocketError.MessageSize => (ProtocolReason.MESSAGE_TOO_LARGE, ProtocolAdvice.FIX_AND_RETRY, ControlFlags.NONE),

                // Permission
                SocketError.AccessDenied => (ProtocolReason.FORBIDDEN, ProtocolAdvice.NONE, ControlFlags.NONE),

                // Address
                SocketError.AddressAlreadyInUse => (ProtocolReason.CONNECTION_LIMIT, ProtocolAdvice.RETRY, ControlFlags.NONE),
                SocketError.AddressNotAvailable => (ProtocolReason.REQUEST_INVALID, ProtocolAdvice.FIX_AND_RETRY, ControlFlags.NONE),

                // Programming / misuse
                SocketError.InvalidArgument or
                SocketError.NotSocket or
                SocketError.OperationNotSupported => (ProtocolReason.REQUEST_INVALID, ProtocolAdvice.FIX_AND_RETRY, ControlFlags.NONE),

                // System
                SocketError.SystemNotReady or
                SocketError.NotInitialized => (ProtocolReason.SERVICE_UNAVAILABLE, ProtocolAdvice.RETRY, ControlFlags.IS_TRANSIENT),
                SocketError.VersionNotSupported => (ProtocolReason.VERSION_UNSUPPORTED, ProtocolAdvice.NONE, ControlFlags.NONE),
                SocketError.SocketError => (ProtocolReason.NETWORK_ERROR, ProtocolAdvice.RETRY, ControlFlags.IS_TRANSIENT),

                SocketError.Success => (ProtocolReason.UNKNOWN, ProtocolAdvice.NONE, ControlFlags.NONE),

                SocketError.OperationAborted => (ProtocolReason.CANCELLED, ProtocolAdvice.RETRY, ControlFlags.IS_TRANSIENT),
                SocketError.Interrupted => (ProtocolReason.CANCELLED, ProtocolAdvice.RETRY, ControlFlags.IS_TRANSIENT),
                SocketError.Fault => (ProtocolReason.INTERNAL_ERROR, ProtocolAdvice.NONE, ControlFlags.NONE),
                SocketError.TooManyOpenSockets => (ProtocolReason.FD_LIMIT, ProtocolAdvice.RETRY, ControlFlags.IS_TRANSIENT),
                SocketError.DestinationAddressRequired => (ProtocolReason.MISSING_REQUIRED_FIELD, ProtocolAdvice.FIX_AND_RETRY, ControlFlags.NONE),
                SocketError.ProtocolType => (ProtocolReason.PROTOCOL_ERROR, ProtocolAdvice.NONE, ControlFlags.NONE),
                SocketError.ProtocolOption => (ProtocolReason.PROTOCOL_ERROR, ProtocolAdvice.NONE, ControlFlags.NONE),
                SocketError.ProtocolNotSupported => (ProtocolReason.PROTOCOL_ERROR, ProtocolAdvice.NONE, ControlFlags.NONE),
                SocketError.SocketNotSupported => (ProtocolReason.OPERATION_UNSUPPORTED, ProtocolAdvice.NONE, ControlFlags.NONE),
                SocketError.ProtocolFamilyNotSupported => (ProtocolReason.PROTOCOL_ERROR, ProtocolAdvice.NONE, ControlFlags.NONE),
                SocketError.AddressFamilyNotSupported => (ProtocolReason.PROTOCOL_ERROR, ProtocolAdvice.NONE, ControlFlags.NONE),
                SocketError.IsConnected => (ProtocolReason.STATE_VIOLATION, ProtocolAdvice.FIX_AND_RETRY, ControlFlags.NONE),
                SocketError.ProcessLimit => (ProtocolReason.RESOURCE_LIMIT, ProtocolAdvice.RETRY, ControlFlags.IS_TRANSIENT),
                SocketError.Disconnecting => (ProtocolReason.LOCAL_CLOSED, ProtocolAdvice.RETRY, ControlFlags.IS_TRANSIENT),
                SocketError.TypeNotFound => (ProtocolReason.PROTOCOL_ERROR, ProtocolAdvice.NONE, ControlFlags.NONE),

                // fallback
                _ => (ProtocolReason.NETWORK_ERROR, ProtocolAdvice.RETRY, ControlFlags.NONE),
            };
        }
    }
}
