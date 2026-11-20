// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Threading;
using Nalix.Abstractions.Diagnostics;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Networking;

namespace Nalix.Network.Protocols;

public abstract partial class Protocol
{
    #region Fields

    private int _accepting;

    #endregion Fields

    #region Properties

    /// <summary>
    /// Indicates whether the protocol is currently accepting connections.
    /// </summary>
    public bool IsAccepting
    {
        get => Interlocked.CompareExchange(ref _accepting, 0, 0) == 1;
        protected set => Interlocked.Exchange(ref _accepting, value ? 1 : 0);
    }

    #endregion Properties

    #region Virtual Methods

    /// <summary>
    /// Allows subclasses to execute custom logic after a message has been processed.
    /// This method is called automatically by <see cref="PostProcessMessage"/>.
    /// </summary>
    /// <param name="args">Event arguments containing connection and processing details.</param>
    protected virtual void OnPostProcess(IConnectionEventArgs args)
    {
    }

    /// <summary>
    /// Called when a connection is accepted. Starts receiving data by default.
    /// Override to implement custom acceptance logic, such as IP validation.
    /// </summary>
    /// <param name="connection">The connection to be processed.</param>
    /// <param name="cancellationToken">Identifier for cancellation</param>
    /// <exception cref="ArgumentNullException">Thrown when connection is null.</exception>
    /// <exception cref="ObjectDisposedException">Thrown if this protocol instance has been disposed.</exception>
    public virtual void OnAccept(IConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // Check if accepting connections is enabled
        if (!this.IsAccepting)
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Trace))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Trace, new DiagnosticLog("NW.Protocol:OnAccept", $"reject id=connection-id={connection.ConnectionId} reason=not-accepting"));
            }
            connection.Disconnect();
            return;
        }

        ArgumentNullException.ThrowIfNull(connection);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) != 0, this);

        // CheckLimit cancellation
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (this.ValidateConnection(connection))
            {
                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Trace))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Trace, new DiagnosticLog("NW.Protocol:OnAccept", $"accepted id=connection-id={connection.ConnectionId}"));
                }

                connection.Transport.UseFraming(this.Framing);

                // Start receiving data from the connection
                connection.Transport.BeginReceive(cancellationToken);

                return;
            }

            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Trace))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Trace, new DiagnosticLog("NW.Protocol:OnAccept", $"reject id=connection-id={connection.ConnectionId} reason=validation-failed"));
            }

            // Connections failed validation, close immediately
            connection.Disconnect();
        }
        catch (OperationCanceledException)
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Trace))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Trace, new DiagnosticLog("NW.Protocol:OnAccept", $"accept-canceled id=connection-id={connection.ConnectionId}"));
            }
            connection.Disconnect();
        }
        catch (ObjectDisposedException ex)
        {
            // Only log warning if not already shutting down to reduce noise.
            if (!cancellationToken.IsCancellationRequested)
            {
                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Warning))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Warning, new DiagnosticLog("NW.Protocol:OnAccept", $"accept-disposed id=connection-id={connection.ConnectionId} target=target={ex.ObjectName}", ex));
                }
            }

            connection.Disconnect();
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            // Log exception if a logger is available
            this.OnConnectionError(connection, ex);
            connection.Disconnect();

            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Debug))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Debug, new DiagnosticLog("NW.Protocol:OnAccept", $"accept-error id=connection-id={connection.ConnectionId}", ex));
            }
        }
    }

    /// <summary>
    /// Called when an error occurs during connection handling.
    /// Override to implement custom error handling.
    /// </summary>
    /// <param name="connection">The connection where the error occurred.</param>
    /// <param name="exception">The exception that was thrown.</param>
    protected virtual void OnConnectionError(IConnection connection, Exception exception)
        => _ = Interlocked.Increment(ref _totalErrors);

    /// <summary>
    /// Validates the incoming connection before accepting it.
    /// Override this method to implement custom validation logic.
    /// </summary>
    /// <param name="connection">The connection to validate.</param>
    /// <returns>True if the connection is valid, false otherwise.</returns>
    protected virtual bool ValidateConnection(IConnection connection) => true;

    #endregion Virtual Methods
}

