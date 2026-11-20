// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Networking.Datagrams;
using Nalix.Environment.Memory;

namespace Nalix.Environment.Datagrams;

/// <summary>
/// Receives the output of a <see cref="DatagramEngine"/>. Called synchronously from the engine call that
/// produced the event, under the owner's lock: implementations hand work off instead of blocking.
/// </summary>
public interface IDatagramEngineHandler
{
    /// <summary>
    /// A complete message was delivered.
    /// </summary>
    /// <param name="message">The message; ownership passes to the handler, which disposes it.</param>
    /// <param name="method">The delivery method it was sent with.</param>
    /// <param name="channel">The channel it was sent on.</param>
    void OnMessage(BufferLease message, DeliveryMethod method, byte channel);

    /// <summary>
    /// The session closed, by either side or by idle timeout. Raised once.
    /// </summary>
    /// <param name="code">The close code.</param>
    /// <param name="applicationCode">The application code when <paramref name="code"/> is <see cref="DatagramCloseCode.ApplicationError"/>.</param>
    /// <param name="reason">The reason text, possibly empty.</param>
    /// <param name="remote"><see langword="true"/> when the peer closed the session.</param>
    void OnClosed(DatagramCloseCode code, ulong applicationCode, string reason, bool remote);
}
