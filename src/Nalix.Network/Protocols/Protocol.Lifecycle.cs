// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using Nalix.Abstractions.Diagnostics;
using Nalix.Abstractions.Networking;

namespace Nalix.Network.Protocols;

public abstract partial class Protocol
{
    #region Fields

    private int _isDisposed;
    private int _keepConnectionOpen;

    #endregion Fields

    #region Properties

    /// <summary>
    /// Gets or sets the framing mechanism used by this protocol.
    /// Defaults to <see cref="TransportFraming.UInt16LengthPrefixed"/>.
    /// </summary>
    public virtual TransportFraming Framing { get; set; } = TransportFraming.UInt16LengthPrefixed;

    /// <summary>
    /// Gets a value indicating whether <see cref="PostProcessMessage"/> is short and never blocks, so a transport may
    /// run it on the thread that completed the send instead of queuing it to the thread pool.
    /// </summary>
    /// <remarks>
    /// Defaults to <see langword="false"/>. Return <see langword="true"/> only when <see cref="OnPostProcess"/> does
    /// next to nothing and <see cref="KeepConnectionOpen"/> is <see langword="true"/>: otherwise post-processing
    /// disconnects, which must not run on the send path.
    /// </remarks>
    public virtual bool SupportsInlinePostProcessing => false;

    /// <summary>
    /// Gets or sets whether the protocol keeps the connection open after a message is processed.
    /// The flag is stored atomically because it is read during hot-path post-processing.
    /// </summary>
    public virtual bool KeepConnectionOpen
    {
        [DebuggerStepThrough]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Interlocked.CompareExchange(ref _keepConnectionOpen, 0, 0) == 1;

        [DebuggerStepThrough]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected set => Interlocked.Exchange(ref _keepConnectionOpen, value ? 1 : 0);
    }

    #endregion Properties

    #region Disposal

    /// <summary>
    /// Disposes resources used by this protocol instance.
    /// </summary>
    public void Dispose()
    {
        this.Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Core disposal logic. Override to release managed resources.
    /// </summary>
    /// <param name="disposing">True if called from Dispose, false if called from finalizer.</param>
    protected virtual void Dispose(bool disposing)
    {
        // The first caller flips the disposed flag from 0 to 1; later callers are ignored.
        if (Interlocked.CompareExchange(ref _isDisposed, 1, 0) != 0)
        {
            return;
        }

        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Trace))
        {
            DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Trace, new DiagnosticLog("NW.Protocol:Dispose", "disposed"));
        }

        // Derived protocols can release managed resources when disposing == true.
    }

    #endregion Disposal
}

