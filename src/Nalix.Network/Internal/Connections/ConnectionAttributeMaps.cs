// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Runtime.CompilerServices;
using System.Threading;
using Nalix.Abstractions;
using Nalix.Framework.Memory.Objects;

namespace Nalix.Network.Internal.Connections;

/// <summary>
/// Creates a connection's attribute map once, even when several threads ask for it at the same time.
/// </summary>
/// <remarks>
/// The hub registers a new connection while its receive loop may already dispatch the first frame.
/// A plain <c>??=</c> let each thread rent its own map, and the last write discarded the other's
/// entries (for example the handshake slot), which rejected the handshake with STATE_VIOLATION.
/// </remarks>
internal static class ConnectionAttributeMaps
{
    /// <summary>
    /// Returns the map stored in <paramref name="field"/>, publishing a newly rented one if it is empty.
    /// </summary>
    /// <param name="field">The field that holds the map.</param>
    /// <returns>The single map every caller shares.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static IObjectMap<AttributeKey, object> GetOrCreate(ref IObjectMap<AttributeKey, object>? field)
    {
        IObjectMap<AttributeKey, object>? current = Volatile.Read(ref field);
        if (current is not null)
        {
            return current;
        }

        ObjectMap<AttributeKey, object> rented = ObjectMap<AttributeKey, object>.Rent();
        IObjectMap<AttributeKey, object>? winner = Interlocked.CompareExchange(ref field, rented, null);
        if (winner is null)
        {
            return rented;
        }

        rented.Return();
        return winner;
    }
}
