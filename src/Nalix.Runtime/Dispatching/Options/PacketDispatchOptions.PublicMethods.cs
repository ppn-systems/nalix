// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions.Diagnostics;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Middleware;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Framework.Injection;
using Nalix.Runtime.Dispatching;
using Nalix.Runtime.Internal.Compilation;

namespace Nalix.Runtime.Routing;

public sealed partial class PacketDispatchOptions<TPacket>
{
    /// <summary>
    /// Registers a custom error hook that receives handler exceptions before the dispatcher
    /// emits the protocol-level failure response.
    /// </summary>
    /// <param name="errorHandler">
    /// An action that takes an exception and the packet ID, which allows custom handling of errors
    /// that occur while processing packets.
    /// </param>
    /// <returns>
    /// The current <see cref="PacketDispatchOptions{TPacket}"/> instance for method chaining.
    /// </returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public PacketDispatchOptions<TPacket> WithErrorHandling(
        Action<Exception, ushort> errorHandler)
    {
        this.LOG_ERROR_HANDLER_SET();
        _errorHandler = errorHandler;

        return this;
    }

    /// <summary>
    /// Adds a packet middleware component to the execution pipeline.
    /// </summary>
    /// <param name="middleware">
    /// The <see cref="IPacketMiddleware{TPacket}"/> instance that will be invoked during packet processing.
    /// </param>
    /// <returns>
    /// The current <see cref="PacketDispatchOptions{TPacket}"/> instance for method chaining.
    /// </returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public PacketDispatchOptions<TPacket> WithMiddleware(IPacketMiddleware<TPacket> middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);
        this.LOG_MIDDLEWARE_ADDED(middleware);
        _pipeline.Use(middleware);

        return this;
    }

    /// <summary>
    /// Overrides the number of worker loops used by the packet dispatcher.
    /// </summary>
    /// <param name="loopCount">
    /// The number of worker loops to start. Must be between 1 and 64.
    /// If <see langword="null"/>, the dispatcher chooses automatically based on the host CPU.
    /// </param>
    /// <returns>The current options instance.</returns>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public PacketDispatchOptions<TPacket> WithDispatchLoopCount(int? loopCount)
    {
        if (loopCount.HasValue && (loopCount.Value <= 0 || loopCount.Value > 64))
        {
            THROW_LOOP_COUNT_OUT_OF_RANGE();
        }

        this.Drain.Count = loopCount ?? 0;
        this.LOG_DISPATCH_LOOP_COUNT(loopCount);
        return this;
    }

    /// <summary>
    /// Configures how packet middleware reacts when one of the middleware stages throws.
    /// </summary>
    /// <param name="continueOnError">
    /// A value indicating whether the pipeline should continue processing subsequent middleware
    /// when an exception occurs. If <see langword="true"/>, execution continues; otherwise, the
    /// pipeline is terminated.
    /// </param>
    /// <param name="errorHandler">
    /// An optional delegate that is invoked when an exception is thrown. The delegate receives
    /// the <see cref="Exception"/> instance and the <see cref="Type"/> of the packet
    /// being processed. If <see langword="null"/>, no custom error handling is applied.
    /// </param>
    /// <returns>
    /// The current <see cref="PacketDispatchOptions{TPacket}"/> instance, allowing for method chaining.
    /// </returns>
    /// <remarks>
    /// This method allows fine-grained control over error handling behavior in the middleware pipeline.
    /// Use <paramref name="continueOnError"/> with caution, as continuing after exceptions may lead
    /// to inconsistent state depending on middleware implementation.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// Thrown if required dependencies within the pipeline are not initialized.
    /// </exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public PacketDispatchOptions<TPacket> WithErrorHandlingMiddleware(
        bool continueOnError,
        Action<Exception, Type>? errorHandler = null)
    {
        _pipeline.ConfigureErrorHandling(continueOnError, errorHandler);
        return this;
    }

    /// <summary>
    /// Registers a controller type and scans its public methods for packet handler attributes.
    /// The controller is instantiated via <see cref="InstanceManager.CreateInstanceWithInjection{T}"/>,
    /// which auto-resolves constructor parameters from the instance cache.
    /// </summary>
    /// <typeparam name="TController">The type of the controller to register.</typeparam>
    /// <returns>The current <see cref="PacketDispatchOptions{TPacket}"/> instance for chaining.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public PacketDispatchOptions<TPacket> WithHandler<[
        DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicMethods |
            DynamicallyAccessedMemberTypes.PublicConstructors)] TController>()
        where TController : class
        => this.WithHandler(InstanceManager.Instance.CreateInstanceWithInjection<TController>);

    /// <summary>
    /// Registers a handler using an existing controller instance.
    /// </summary>
    /// <typeparam name="TController">The type of the controller to register.</typeparam>
    /// <param name="instance">An existing instance of <typeparamref name="TController"/>.</param>
    /// <returns>
    /// The current <see cref="PacketDispatchOptions{TPacket}"/> instance for chaining.
    /// </returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public PacketDispatchOptions<TPacket> WithHandler<[
        DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicMethods |
            DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TController>(
        TController instance)
        where TController : class => this.WithHandler(() => ThrowIfNull(instance, nameof(instance)));

    /// <summary>
    /// Registers a controller factory and scans the produced instance for packet handlers.
    /// </summary>
    /// <typeparam name="TController">
    /// The type of the controller to register.
    /// </typeparam>
    /// <param name="factory">
    /// A function that returns an instance of <typeparamref name="TController"/>.
    /// </param>
    /// <returns>The current <see cref="PacketDispatchOptions{TPacket}"/> instance for chaining.</returns>
    /// <remarks>
    /// During registration the dispatcher does more than just compile delegates:
    /// it also records the concrete packet type expected by each handler so it can
    /// warn about runtime type mismatches before the handler body runs.
    /// <para>
    /// This is especially important for legacy direct-packet handlers, because a
    /// mismatch there would otherwise surface later as an expression-tree cast
    /// failure with very little context.
    /// </para>
    /// <para>
    /// Context-style handlers do not need that extra mapping because the packet is
    /// carried inside <c>PacketContext&lt;TPacket&gt;</c> and the handler can decide
    /// how to inspect it.
    /// </para>
    /// </remarks>
    /// <exception cref="InternalErrorException"></exception>
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public PacketDispatchOptions<TPacket> WithHandler<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] TController>(Func<TController> factory)
        where TController : class
    {
        ArgumentNullException.ThrowIfNull(factory);

        Type controllerType = typeof(TController);

        if (!PacketHandlerRegistry.TryBuildHandlers<TPacket>(controllerType, this, () => factory()))
        {
            throw new InternalErrorException($"Controller '{controllerType.Name}' was not processed by the Source Generator. Ensure it is marked with [PacketHandler].");
        }

        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Information))
        {
            DiagnosticsEvents.Write(
                DiagnosticsEvents.Internal.Information,
                new DiagnosticLog(
                    "RT.PacketDispatchOptions:Internal",
                    $"reg-controller name={controllerType.Name}"));
        }

        return this;
    }

    /// <summary>
    /// Registers a controller type directly, primarily for static classes.
    /// </summary>
    /// <param name="controllerType">The type of the controller to register.</param>
    /// <returns>The current <see cref="PacketDispatchOptions{TPacket}"/> instance for chaining.</returns>
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public PacketDispatchOptions<TPacket> WithHandler([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] Type controllerType) => this.WithHandler(controllerType, () => null!);

    /// <summary>
    /// Registers a controller type directly with a factory for instantiation.
    /// </summary>
    /// <param name="controllerType">The type of the controller to register.</param>
    /// <param name="factory">The factory to create instances of the controller.</param>
    /// <returns>The current <see cref="PacketDispatchOptions{TPacket}"/> instance for chaining.</returns>
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public PacketDispatchOptions<TPacket> WithHandler([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] Type controllerType, Func<object> factory)
    {
        ArgumentNullException.ThrowIfNull(controllerType);
        ArgumentNullException.ThrowIfNull(factory);

        if (!PacketHandlerRegistry.TryBuildHandlers<TPacket>(controllerType, this, factory))
        {
            throw new InternalErrorException($"Controller '{controllerType.Name}' was not processed by the Source Generator. Ensure it is marked with [PacketHandler].");
        }

        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Debug))
        {
            DiagnosticsEvents.Write(
                DiagnosticsEvents.Internal.Debug,
                new DiagnosticLog("RT.PacketDispatchOptions:Internal", $"reg-controller name={controllerType.Name}"));
        }

        return this;
    }

    /// <summary>
    /// Registers a new packet handler generated by the source generator.
    /// </summary>
    void IPacketHandlerBuilder<TPacket>.RegisterHandler(
        ushort opCode,
        PacketMetadata metadata,
        string methodName,
        object? instance,
        Type returnType,
        Type? expectedPacketType,
        Func<object?, PacketContext<TPacket>, ValueTask<object?>> invoker)
    {
        PacketHandler<TPacket> runtimeHandler = new(
            opCode,
            metadata,
            instance,
            methodName,
            returnType,
            invoker,
            expectedPacketType);

        HandlerSlot slot = new(runtimeHandler);
        if (Interlocked.CompareExchange(ref _handlerTable[opCode], slot, null) is not null)
        {
            throw new InternalErrorException($"OpCode '{opCode}' has already been registered.");
        }

        _ = Interlocked.Increment(ref _handlerCount);

        if (expectedPacketType is not null && expectedPacketType != typeof(TPacket))
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Debug))
            {
                DiagnosticsEvents.Write(
                    DiagnosticsEvents.Internal.Debug,
                    new DiagnosticLog(
                        "RT.PacketDispatchOptions:Internal",
                        $"type-map opcode=0x{opCode} -> {expectedPacketType.Name}"));
            }
        }
    }

    /// <summary>
    /// Attempts to resolve a cached packet handler descriptor for the specified opcode.
    /// </summary>
    /// <param name="opCode">The opcode of the packet handler to resolve.</param>
    /// <param name="handler">
    /// When this method returns, contains the cached handler descriptor associated with
    /// <paramref name="opCode"/>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if a handler descriptor was found; otherwise <see langword="false"/>.
    /// </returns>
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    internal bool TryResolveHandler(ushort opCode, out PacketHandler<TPacket> handler)
    {
        HandlerSlot? slot = Volatile.Read(ref _handlerTable[opCode]);
        if (slot is null)
        {
            handler = default;
            return false;
        }

        handler = slot.Handler;
        return true;
    }

    /// <summary>
    /// Executes a resolved handler with a pooled packet context and returns the context
    /// to the object pool afterward.
    /// </summary>
    /// <remarks>
    /// This method takes ownership of <paramref name="packet"/> as soon as it is called: the packet is released
    /// exactly once when the pooled context is returned (or immediately if the context cannot be initialized).
    /// Callers must not dispose the packet again, because a pooled packet that is disposed twice can be handed to
    /// a second renter while the first one is still using it.
    /// </remarks>
    /// <param name="descriptor">Resolved handler descriptor.</param>
    /// <param name="packet">Incoming packet.</param>
    /// <param name="connection">Source connection.</param>
    /// <param name="reliable">Indicates whether the packet was received via a reliable transport.</param>
    /// <param name="encryptedOnWire">Indicates whether the inbound frame arrived encrypted on the wire.</param>
    /// <param name="token">Cancellation token for dispatch.</param>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal ValueTask ExecuteResolvedHandlerAsync(
        in PacketHandler<TPacket> descriptor,
        TPacket packet,
        IConnection connection,
        bool reliable,
        bool encryptedOnWire,
        CancellationToken token = default)
    {
        PacketContext<TPacket> context = _objectPool.Get<PacketContext<TPacket>>();
        try
        {
            // Use the UNRELIABLE flag as the source of truth for transport logic.
            context.Initialize(
                packet: packet,
                connection: connection,
                descriptor: descriptor.Metadata,
                reliable: reliable,
                encryptedOnWire: encryptedOnWire,
                ownsPacket: true, token: token);
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
#pragma warning disable CA1849 // Non-async rollback path
            context.Dispose();
#pragma warning restore CA1849

            // The context never took ownership, so release the packet here (see remarks).
            if (packet is IDisposable disposablePacket)
            {
                disposablePacket.Dispose();
            }

            throw;
        }

        ValueTask pending = this.ExecuteHandlerAsync(descriptor, context);
        if (pending.IsCompletedSuccessfully)
        {
            try
            {
#pragma warning disable CA1849 // Completed-success fast path; GetResult observes synchronous exceptions without blocking or allocating an async state machine.
                pending.GetAwaiter().GetResult();
#pragma warning restore CA1849
            }
            finally
            {
#pragma warning disable CA1849 // Synchronous fast-path cleanup without async state machine allocation
                context.Dispose();
#pragma warning restore CA1849
            }

            return ValueTask.CompletedTask;
        }

        return AwaitAndDisposeAsync(pending, context);

        static async ValueTask AwaitAndDisposeAsync(ValueTask operation, PacketContext<TPacket> pooledContext)
        {
            await using (pooledContext.ConfigureAwait(false))
            {
                await operation.ConfigureAwait(false);
            }
        }
    }

    #region Private

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void LOG_ERROR_HANDLER_SET()
    {
        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Debug))
        {
            DiagnosticsEvents.Write(
                DiagnosticsEvents.Internal.Debug,
                new DiagnosticLog(
                    "RT.PacketDispatchOptions:WithErrorHandling",
                    "error-handler-set"));
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void LOG_MIDDLEWARE_ADDED(IPacketMiddleware<TPacket> middleware)
    {
        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Debug))
        {
            DiagnosticsEvents.Write(
                DiagnosticsEvents.Internal.Debug,
                new DiagnosticLog(
                    "RT.PacketDispatchOptions:WithMiddleware",
                    $"middleware-added type={middleware.GetType().Name}"));
        }
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void THROW_LOOP_COUNT_OUT_OF_RANGE() => throw new ArgumentOutOfRangeException("loopCount", "Dispatch loop count must be between 1 and 64.");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void LOG_DISPATCH_LOOP_COUNT(int? loopCount)
    {
        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Debug))
        {
            string loops = loopCount.HasValue ? loopCount.Value.ToString(CultureInfo.InvariantCulture) : "auto";
            DiagnosticsEvents.Write(
                DiagnosticsEvents.Internal.Debug,
                new DiagnosticLog(
                    "RT.PacketDispatchOptions:WithDispatchLoopCount",
                    $"loops={loops}"));
        }
    }

    #endregion Private
}
