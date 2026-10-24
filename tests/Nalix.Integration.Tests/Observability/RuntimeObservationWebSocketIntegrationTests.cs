// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using System.Net.Sockets;
using System.Text;
using Nalix.Abstractions.Middleware;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Security;
using Nalix.Codec.DataFrames;
using Nalix.Codec.Serialization;
using Nalix.Environment.Configuration;
using Nalix.Hosting;
using Nalix.Hosting.Protocols;
using Nalix.Runtime.Dispatching;
using Nalix.Network.Options;
using Nalix.Observability;
using Nalix.Observability.Contracts;
using Nalix.SDK.Options;
using Nalix.SDK.Transport;
using Nalix.SDK.Transport.Extensions;
using Nalix.Integration.Tests.TestSupport;
using Nalix.Tests.Shared;

namespace Nalix.Integration.Tests.Observability;

/// <summary>
/// Real WebSocket integration test for the RuntimeObservation roundtrip.
/// Starts a full Nalix server in-process, connects through the SDK WebSocket
/// client path, completes the X25519 handshake, authenticates via
/// ObservabilityAccess, then sends RuntimeObservation and traces every
/// boundary from client send through server handler invocation and back.
/// </summary>
[Collection(TestCollections.NetworkConfig)]
public sealed class RuntimeObservationWebSocketIntegrationTests : IDisposable
{
    private readonly NetworkConfigSnapshot _configSnapshot = new();

    #region Constants

    private const int TimeoutMs = 5_000;

    #endregion

    #region Fields

    private readonly string _certificatePath;
    private readonly ITestOutputHelper _output;

    #endregion

    #region Constructor

    public RuntimeObservationWebSocketIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _certificatePath = Path.Combine(
            Path.GetTempPath(), $"nalix-obs-ws-{Guid.NewGuid():N}.private");
        WriteCertificate();
    }

    #endregion

    #region Test

    [Fact]
    public async Task RuntimeObservation_WithWebSocketEndToEnd_ReachesHandler()
    {
        // -- Boundary counters (shared between test and server middleware) --
        BoundaryCounters counters = new();

        // -- 1. Build and start the real server --
        ushort port = GetFreePort();

        ConfigurationManager.Instance.Get<NetworkWebSocketOptions>().Host = "127.0.0.1";

        var builder = NetworkApplication.CreateBuilder();

        builder.UseSecureConnections(_certificatePath);
        builder.UseSystemControl();
        builder.UseTimeSync();
        builder.UseSessions();
        builder.UseObservability();

        // Register boundary-tracking middleware so the test can see
        // which opcodes reach the handler and which produce responses.
        builder.ConfigureDispatchOptions(options =>
        {
            // EARLY middleware at order -200: runs BEFORE PermissionMiddleware (-50).
            // Counts ALL packets entering the dispatch pipeline regardless of permission.
            options.WithMiddleware(new EarlyBoundaryMiddleware(counters));
            // LATE middleware at order 100: runs AFTER permission/validation.
            // Wraps the actual handler invocation.
            options.WithMiddleware(new BoundaryTrackingMiddleware(counters));
        });

        builder.ListenWebSocket<DefaultProtocol>()
               .OnPort(port)
               .WithPath("/ws/")
               .WithFactory(dispatch => new InstrumentedProtocol(dispatch, counters));

        await using NetworkApplication app = builder.Build();
        await app.ActivateAsync();

        // Give the listener time to bind.
        await Task.Delay(500);

        StringBuilder diagnostics = new(4096);

        try
        {
            // -- 2. Client connects via real WebSocket path --
            var transportOptions = new TransportOptions
            {
                Address = "127.0.0.1",
                Port = port,
                CompressionEnabled = false,
                ConnectTimeoutMillis = TimeoutMs
            };

            var wsOptions = new WebSocketTransportOptions { Path = "/ws/" };

            using WebSocketSession client = new(transportOptions, wsOptions);

            await client.ConnectAsync();
            Interlocked.Increment(ref counters.ClientWsConnected);

            Assert.True(client.IsConnected, "Client failed to connect via WebSocket.");

            // -- 3. X25519 handshake (establishes encryption) --
            using CancellationTokenSource handshakeCts = new(TimeoutMs);

            await client.HandshakeAsync(handshakeCts.Token);
            Interlocked.Increment(ref counters.ClientHandshakeCompleted);

            Assert.True(client.State.EncryptionEnabled,
                "Handshake completed but encryption was not enabled on the client.");

            // -- Print comparison info requested by the task --
            AppendComparisonInfo(diagnostics);

            // -- 4. Send ObservabilityAccess --
            Bytes32 adminKey = ObservabilityAccessHandlers.AdminKey;

            ObservabilityAccess accessRequest = PacketBase<ObservabilityAccess>.Create();
            accessRequest.Initialize(
                ObservabilityAccessStage.REQUEST,
                accessKey: adminKey);

            diagnostics.AppendLine();
            diagnostics.AppendLine("-- ObservabilityAccess --");
            diagnostics.AppendLine($"  StaticOpCode     = 0x{ObservabilityAccess.StaticOpCode:X4}");
            diagnostics.AppendLine($"  Stage            = {accessRequest.Stage}");
            diagnostics.AppendLine($"  AccessKey.IsZero = {accessRequest.AccessKey.IsZero}");
            diagnostics.AppendLine($"  Length           = {accessRequest.Length}");
            diagnostics.AppendLine($"  Validate         = {accessRequest.Validate(out string? accessValReason)} ({accessValReason ?? "ok"})");

            Interlocked.Increment(ref counters.ClientObsAccessSent);

            RequestOptions accessOpts = RequestOptions.Default
                .WithTimeout(TimeoutMs)
                .WithEncrypt();

            ObservabilityAccess accessResponse;
            try
            {
                accessResponse = await client.RequestAsync<ObservabilityAccess>(
                    accessRequest, accessOpts);
            }
            catch (Exception ex)
            {
                diagnostics.AppendLine();
                diagnostics.AppendLine($"-- DEBUG EXCEPTION on ObservabilityAccess: {ex.GetType().Name}: {ex.Message} --");
                AppendBoundaryReport(diagnostics, counters);
                Assert.Fail(diagnostics.ToString());
                return;
            }

            Interlocked.Increment(ref counters.ClientObsAccessReceived);

            diagnostics.AppendLine($"  Response.Stage       = {accessResponse.Stage}");
            diagnostics.AppendLine($"  Response.Reason      = {accessResponse.Reason}");
            diagnostics.AppendLine($"  Response.AccessLevel = {accessResponse.AccessLevel}");

            Assert.Equal(ObservabilityAccessStage.RESPONSE, accessResponse.Stage);
            Assert.Equal(ProtocolReason.NONE, accessResponse.Reason);
            Assert.Equal(PermissionLevel.SUPERVISOR, accessResponse.AccessLevel);

            // -- 5. Build RuntimeObservation request --
            RuntimeObservation runtimeRequest = PacketBase<RuntimeObservation>.Create();
            runtimeRequest.Initialize(
                RuntimeObservationStage.REQUEST,
                RuntimeObservationTarget.INSTANCES,
                ProtocolReason.NONE);

            diagnostics.AppendLine();
            diagnostics.AppendLine("-- RuntimeObservation (pre-send assertions) --");
            diagnostics.AppendLine($"  StaticOpCode               = 0x{RuntimeObservation.StaticOpCode:X4}");
            diagnostics.AppendLine($"  PacketSchema.StaticSize    = {PacketSchema<RuntimeObservation>.StaticSize}");
            diagnostics.AppendLine($"  Formatter                  = {FormatterProvider.Get<RuntimeObservation>().GetType().FullName}");

            bool validateResult = runtimeRequest.Validate(out string? valReason);
            diagnostics.AppendLine($"  Validate                   = {validateResult} ({valReason ?? "ok"})");
            diagnostics.AppendLine($"  Stage                      = {runtimeRequest.Stage}");
            diagnostics.AppendLine($"  Target                     = {runtimeRequest.Target}");
            diagnostics.AppendLine($"  Reason                     = {runtimeRequest.Reason}");
            diagnostics.AppendLine($"  ObservationData.Length     = {runtimeRequest.ObservationData.Length}");
            diagnostics.AppendLine($"  OpCode                     = 0x{runtimeRequest.OpCode:X4}");
            diagnostics.AppendLine($"  Length                     = {runtimeRequest.Length}");

            // Required pre-send assertions.
            Assert.True(validateResult, valReason);
            Assert.Equal(RuntimeObservationStage.REQUEST, runtimeRequest.Stage);
            Assert.NotEqual(RuntimeObservationTarget.NONE, runtimeRequest.Target);
            Assert.Equal(0, runtimeRequest.ObservationData.Length);
            Assert.Equal(RuntimeObservation.StaticOpCode, runtimeRequest.OpCode);
            Assert.True(runtimeRequest.Length >= PacketSchema<RuntimeObservation>.StaticSize,
                $"packet.Length ({runtimeRequest.Length}) < StaticSize ({PacketSchema<RuntimeObservation>.StaticSize})");

            // Serialize for diagnostic hex dump.
            byte[] serialized = runtimeRequest.Serialize();
            diagnostics.AppendLine($"  Serialized.Length           = {serialized.Length}");
            diagnostics.AppendLine($"  Serialized[0..32]          = {FormatHex(serialized.AsSpan(0, Math.Min(32, serialized.Length)))}");

            // -- 6. Send RuntimeObservation through real SDK path --
            Interlocked.Increment(ref counters.ClientRuntimeObsSent);

            RequestOptions runtimeOpts = RequestOptions.Default
                .WithTimeout(TimeoutMs)
                .WithEncrypt();

            RuntimeObservation runtimeResponse;
            try
            {
                runtimeResponse = await client.RequestAsync<RuntimeObservation>(
                    runtimeRequest, runtimeOpts);
            }
            catch (TimeoutException tex)
            {
                // -- Boundary analysis on timeout --
                diagnostics.AppendLine();
                diagnostics.AppendLine("-- TIMEOUT: RuntimeObservation response not received --");
                diagnostics.AppendLine($"  Exception: {tex.Message}");
                AppendBoundaryReport(diagnostics, counters);

                string? firstFailing = DetermineFirstFailingBoundary(counters);
                Assert.Fail(
                    $"RuntimeObservation TIMEOUT.\n" +
                    $"First failing boundary: {firstFailing}\n\n" +
                    diagnostics.ToString());
                return;
            }
            catch (OperationCanceledException oce)
            {
                diagnostics.AppendLine();
                diagnostics.AppendLine("-- CANCELED: RuntimeObservation request canceled --");
                diagnostics.AppendLine($"  Exception: {oce.Message}");
                AppendBoundaryReport(diagnostics, counters);

                string? firstFailing = DetermineFirstFailingBoundary(counters);
                Assert.Fail(
                    $"RuntimeObservation CANCELED.\n" +
                    $"First failing boundary: {firstFailing}\n\n" +
                    diagnostics.ToString());
                return;
            }
            catch (Exception ex)
            {
                diagnostics.AppendLine();
                diagnostics.AppendLine($"-- EXCEPTION: {ex.GetType().Name} --");
                diagnostics.AppendLine(FormatExceptionChain(ex));
                AppendBoundaryReport(diagnostics, counters);

                string? firstFailing = DetermineFirstFailingBoundary(counters);
                Assert.Fail(
                    $"RuntimeObservation EXCEPTION.\n" +
                    $"First failing boundary: {firstFailing}\n\n" +
                    diagnostics.ToString());
                return;
            }

            Interlocked.Increment(ref counters.ClientRuntimeObsReceived);

            // -- 7. Print response diagnostics --
            diagnostics.AppendLine();
            diagnostics.AppendLine("-- RuntimeObservation response --");
            diagnostics.AppendLine($"  Response.Stage               = {runtimeResponse.Stage}");
            diagnostics.AppendLine($"  Response.Target              = {runtimeResponse.Target}");
            diagnostics.AppendLine($"  Response.Reason              = {runtimeResponse.Reason}");
            diagnostics.AppendLine($"  Response.ObservationData.Length = {runtimeResponse.ObservationData.Length}");
            diagnostics.AppendLine($"  Response.OpCode              = 0x{runtimeResponse.OpCode:X4}");

            // -- 8. Assert all boundaries were crossed --
            AppendBoundaryReport(diagnostics, counters);

            string? boundaryFailure = DetermineFirstFailingBoundary(counters);
            if (boundaryFailure is not null)
            {
                Assert.Fail(
                    $"RuntimeObservation response received but a boundary was not crossed.\n" +
                    $"First missing boundary: {boundaryFailure}\n\n" +
                    diagnostics.ToString());
            }

            // All boundaries passed.
            Assert.Equal(RuntimeObservationStage.RESPONSE, runtimeResponse.Stage);
            Assert.Equal(RuntimeObservationTarget.INSTANCES, runtimeResponse.Target);

            // Assert handler was invoked at least once on the server.
            Assert.True(
                Volatile.Read(ref counters.ServerHandlerInvoked_RuntimeObs) > 0,
                $"Server handler invocation count is 0.\n{diagnostics}");

            // Assert response was sent by the server.
            Assert.True(
                Volatile.Read(ref counters.ServerResponseSent_RuntimeObs) > 0,
                $"Server response-sent count is 0.\n{diagnostics}");

            // Print full diagnostics for CI log visibility.
            diagnostics.AppendLine();
            diagnostics.AppendLine("-- ALL BOUNDARIES PASSED --");
            _ = diagnostics.AppendLine();
        }
        finally
        {
            await app.DeactivateAsync();

            // Always dump diagnostics to test output.
            _output.WriteLine(diagnostics.ToString());
        }
    }

    #endregion

    #region Instrumented Protocol

    /// <summary>
    /// A protocol that uses the real DefaultFrameProcessor and dispatch pipeline,
    /// but adds frame-level counters at the ProcessMessage boundary.
    /// If ProcessMessage is called, the frame survived decryption, decompression,
    /// and sequence number validation.
    /// </summary>
    private sealed class InstrumentedProtocol : Nalix.Network.Protocols.Protocol
    {
        private readonly IPacketDispatch _dispatch;
        private readonly DefaultFrameProcessor _frameProcessor;
        private readonly BoundaryCounters _counters;
        private static readonly IOpCodeExtractor s_opCodeExtractor = new DefaultOpCodeExtractor();

        public override IFrameProcessor FrameProcessor => _frameProcessor;
        public override IOpCodeExtractor OpCodeExtractor => s_opCodeExtractor;

        public InstrumentedProtocol(IPacketDispatch dispatch, BoundaryCounters counters)
        {
            _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
            _counters = counters ?? throw new ArgumentNullException(nameof(counters));
            _frameProcessor = new DefaultFrameProcessor(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, this);
            this.IsAccepting = true;
            this.KeepConnectionOpen = true;
        }

        public override void ProcessMessage(object? sender, IConnectionEventArgs args)
        {
            if (args.Lease is null)
            {
                return;
            }

            // This runs AFTER DefaultFrameProcessor successfully decoded the frame.
            // Count by opcode to see which packets survive the frame pipeline.
            ushort opcode = s_opCodeExtractor.Extract(args.Lease.Span);

            if (opcode == RuntimeObservation.StaticOpCode)
            {
                Interlocked.Increment(ref _counters.ServerFrameDecoded_RuntimeObs);
            }
            else if (opcode == ObservabilityAccess.StaticOpCode)
            {
                Interlocked.Increment(ref _counters.ServerFrameDecoded_ObsAccess);
            }

            Interlocked.Increment(ref _counters.ServerTotalFramesDecoded);

            _dispatch.HandlePacket(args.Lease, args.Connection);
        }
    }

    #endregion

    #region Boundary Tracking Middleware

    /// <summary>
    /// Runs at order -200, BEFORE the PermissionMiddleware (-50).
    /// Counts every packet that enters the dispatch pipeline, regardless
    /// of whether it passes permission checks. This distinguishes between
    /// "packet dropped before dispatch" and "packet rejected by permission".
    /// </summary>
    [MiddlewareOrder(-200)]
    private sealed class EarlyBoundaryMiddleware : IPacketMiddleware<IPacket>
    {
        private readonly BoundaryCounters _c;
        public EarlyBoundaryMiddleware(BoundaryCounters c) => _c = c;

        public async ValueTask InvokeAsync(
            IPacketContext<IPacket> context,
            Func<CancellationToken, ValueTask> next)
        {
            ushort opcode = context.Packet.Header.OpCode;

            if (opcode == RuntimeObservation.StaticOpCode)
            {
                Interlocked.Increment(ref _c.ServerDispatchReached_RuntimeObs);
            }
            else if (opcode == ObservabilityAccess.StaticOpCode)
            {
                Interlocked.Increment(ref _c.ServerDispatchReached_ObsAccess);
            }

            await next(context.CancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs at order 100, AFTER the PermissionMiddleware (-50).
    /// Wraps the actual handler invocation. If this fires, the packet
    /// passed permission checks and all validation.
    /// </summary>
    [MiddlewareOrder(100)]
    private sealed class BoundaryTrackingMiddleware : IPacketMiddleware<IPacket>
    {
        private readonly BoundaryCounters _c;

        public BoundaryTrackingMiddleware(BoundaryCounters c) => _c = c;

        public async ValueTask InvokeAsync(
            IPacketContext<IPacket> context,
            Func<CancellationToken, ValueTask> next)
        {
            ushort opcode = context.Packet.Header.OpCode;

            if (opcode == RuntimeObservation.StaticOpCode)
            {
                Interlocked.Increment(ref _c.ServerHandlerInvoked_RuntimeObs);
            }
            else if (opcode == ObservabilityAccess.StaticOpCode)
            {
                Interlocked.Increment(ref _c.ServerHandlerInvoked_ObsAccess);
            }

            // Execute the actual handler (and any downstream middleware).
            await next(context.CancellationToken).ConfigureAwait(false);

            // After next() returns, the handler has completed and the
            // response has been sent (or enqueued) via PacketSender.
            if (opcode == RuntimeObservation.StaticOpCode)
            {
                Interlocked.Increment(ref _c.ServerResponseSent_RuntimeObs);
            }
            else if (opcode == ObservabilityAccess.StaticOpCode)
            {
                Interlocked.Increment(ref _c.ServerResponseSent_ObsAccess);
            }
        }
    }

    #endregion

    #region Boundary Counters

    /// <summary>
    /// Shared counters for boundary tracking.
    /// All fields are modified with <see cref="Interlocked.Increment(ref int)"/>
    /// and read with <see cref="Volatile.Read(ref int)"/>.
    /// </summary>
    private sealed class BoundaryCounters
    {
        // Server-side - frame-level (InstrumentedProtocol.ProcessMessage)
        // Runs AFTER DefaultFrameProcessor (decryption, decompression, sequence validation).
        public int ServerTotalFramesDecoded;
        public int ServerFrameDecoded_ObsAccess;
        public int ServerFrameDecoded_RuntimeObs;

        // Server-side - early middleware (order -200, before permission check)
        public int ServerDispatchReached_ObsAccess;
        public int ServerDispatchReached_RuntimeObs;

        // Server-side - late middleware (order 100, after permission check, wraps handler)
        public int ServerHandlerInvoked_ObsAccess;
        public int ServerResponseSent_ObsAccess;
        public int ServerHandlerInvoked_RuntimeObs;
        public int ServerResponseSent_RuntimeObs;

        // Client-side (updated by test code)
        public int ClientWsConnected;
        public int ClientHandshakeCompleted;
        public int ClientObsAccessSent;
        public int ClientObsAccessReceived;
        public int ClientRuntimeObsSent;
        public int ClientRuntimeObsReceived;
    }

    #endregion

    #region Boundary Analysis

    /// <summary>
    /// Returns the name of the first boundary that was NOT crossed,
    /// or null if all boundaries were crossed.
    /// </summary>
    private static string? DetermineFirstFailingBoundary(BoundaryCounters c)
    {
        if (Volatile.Read(ref c.ClientWsConnected) == 0)
            return "Client WebSocket connection";

        if (Volatile.Read(ref c.ClientHandshakeCompleted) == 0)
            return "Client X25519 handshake";

        if (Volatile.Read(ref c.ClientObsAccessSent) == 0)
            return "ObservabilityAccess client send";

        if (Volatile.Read(ref c.ServerFrameDecoded_ObsAccess) == 0)
            return "ObservabilityAccess frame not decoded by server";

        if (Volatile.Read(ref c.ServerDispatchReached_ObsAccess) == 0)
            return "ObservabilityAccess frame decoded but dropped before dispatch middleware";

        if (Volatile.Read(ref c.ServerHandlerInvoked_ObsAccess) == 0)
            return "ObservabilityAccess reached dispatch but permission/validation/handler-lookup failed";

        if (Volatile.Read(ref c.ServerResponseSent_ObsAccess) == 0)
            return "ObservabilityAccess server response send";

        if (Volatile.Read(ref c.ClientObsAccessReceived) == 0)
            return "ObservabilityAccess client response";

        if (Volatile.Read(ref c.ClientRuntimeObsSent) == 0)
            return "RuntimeObservation client send";

        if (Volatile.Read(ref c.ServerFrameDecoded_RuntimeObs) == 0)
            return "RuntimeObservation disappeared between client WebSocket write and server frame decode (FramePipeline.TryProcessInbound or sequence validation failed)";

        if (Volatile.Read(ref c.ServerDispatchReached_RuntimeObs) == 0)
            return "RuntimeObservation frame decoded but dropped before dispatch middleware (handler lookup or deserialization failed)";

        if (Volatile.Read(ref c.ServerHandlerInvoked_RuntimeObs) == 0)
            return "RuntimeObservation reached dispatch but permission check, validation, or handler lookup failed";

        if (Volatile.Read(ref c.ServerResponseSent_RuntimeObs) == 0)
            return "RuntimeObservation handler ran but response was not sent or not received";

        if (Volatile.Read(ref c.ClientRuntimeObsReceived) == 0)
            return "RuntimeObservation handler ran but response was not received by client";

        return null; // All boundaries crossed.
    }

    private static void AppendBoundaryReport(StringBuilder sb, BoundaryCounters c)
    {
        sb.AppendLine();
        sb.AppendLine("-- Boundary Report --");
        sb.AppendLine($"  ClientWsConnected                   = {Volatile.Read(ref c.ClientWsConnected)}");
        sb.AppendLine($"  ClientHandshakeCompleted            = {Volatile.Read(ref c.ClientHandshakeCompleted)}");
        sb.AppendLine($"  ClientObsAccessSent                 = {Volatile.Read(ref c.ClientObsAccessSent)}");
        sb.AppendLine($"  ServerTotalFramesDecoded            = {Volatile.Read(ref c.ServerTotalFramesDecoded)}");
        sb.AppendLine($"  ServerFrameDecoded_ObsAccess        = {Volatile.Read(ref c.ServerFrameDecoded_ObsAccess)}");
        sb.AppendLine($"  ServerDispatchReached_ObsAccess     = {Volatile.Read(ref c.ServerDispatchReached_ObsAccess)}");
        sb.AppendLine($"  ServerHandlerInvoked_ObsAccess      = {Volatile.Read(ref c.ServerHandlerInvoked_ObsAccess)}");
        sb.AppendLine($"  ServerResponseSent_ObsAccess        = {Volatile.Read(ref c.ServerResponseSent_ObsAccess)}");
        sb.AppendLine($"  ClientObsAccessReceived             = {Volatile.Read(ref c.ClientObsAccessReceived)}");
        sb.AppendLine($"  ClientRuntimeObsSent                = {Volatile.Read(ref c.ClientRuntimeObsSent)}");
        sb.AppendLine($"  ServerFrameDecoded_RuntimeObs       = {Volatile.Read(ref c.ServerFrameDecoded_RuntimeObs)}");
        sb.AppendLine($"  ServerDispatchReached_RuntimeObs    = {Volatile.Read(ref c.ServerDispatchReached_RuntimeObs)}");
        sb.AppendLine($"  ServerHandlerInvoked_RuntimeObs     = {Volatile.Read(ref c.ServerHandlerInvoked_RuntimeObs)}");
        sb.AppendLine($"  ServerResponseSent_RuntimeObs       = {Volatile.Read(ref c.ServerResponseSent_RuntimeObs)}");
        sb.AppendLine($"  ClientRuntimeObsReceived            = {Volatile.Read(ref c.ClientRuntimeObsReceived)}");

        string? first = DetermineFirstFailingBoundary(c);
        sb.AppendLine($"  First failing boundary              = {first ?? "(none - all passed)"}");
    }

    #endregion

    #region Comparison Info

    private static void AppendComparisonInfo(StringBuilder sb)
    {
        sb.AppendLine("-- Packet Type Comparison --");
        sb.AppendLine($"  ObservabilityAccess.StaticOpCode              = 0x{ObservabilityAccess.StaticOpCode:X4}");
        sb.AppendLine($"  RuntimeObservation.StaticOpCode               = 0x{RuntimeObservation.StaticOpCode:X4}");
        sb.AppendLine($"  PacketSchema<ObservabilityAccess>.StaticSize  = {PacketSchema<ObservabilityAccess>.StaticSize}");
        sb.AppendLine($"  PacketSchema<RuntimeObservation>.StaticSize   = {PacketSchema<RuntimeObservation>.StaticSize}");
        sb.AppendLine($"  FormatterProvider.Get<ObservabilityAccess>()  = {FormatterProvider.Get<ObservabilityAccess>().GetType().FullName}");
        sb.AppendLine($"  FormatterProvider.Get<RuntimeObservation>()   = {FormatterProvider.Get<RuntimeObservation>().GetType().FullName}");
    }

    #endregion

    #region Helpers

    private static ushort GetFreePort() => (ushort)TestPorts.Next();

    private void WriteCertificate()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_certificatePath)!);
        File.WriteAllText(_certificatePath,
            "0000000000000000000000000000000000000000000000000000000000000001");
    }

    private static string FormatHex(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return "(empty)";
        StringBuilder sb = new(bytes.Length * 3);
        for (int i = 0; i < bytes.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(bytes[i].ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    private static string FormatExceptionChain(Exception? ex)
    {
        StringBuilder sb = new();
        for (Exception? cur = ex; cur is not null; cur = cur.InnerException)
        {
            sb.AppendLine($"[{cur.GetType().Name}] {cur.Message}");
        }
        return sb.ToString().TrimEnd();
    }

    #endregion

    #region IDisposable

    public void Dispose()
    {
        _configSnapshot.Dispose();
        try
        {
            if (File.Exists(_certificatePath))
                File.Delete(_certificatePath);
        }
        catch
        {
            // Best-effort cleanup.
        }

        Nalix.Framework.Injection.InstanceManager.Instance.Clear(dispose: false);
    }

    #endregion
}
