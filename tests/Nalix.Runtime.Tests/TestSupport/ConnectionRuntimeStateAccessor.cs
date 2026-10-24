// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Reflection;
using Nalix.Abstractions.Networking;
using Nalix.Runtime.Extensions;

namespace Nalix.Runtime.Tests.TestSupport;

/// <summary>Reaches the internal per-connection runtime state through reflection.</summary>
internal static class ConnectionRuntimeStateAccessor
{
    public static void SetHandshakeEstablished(IConnection connection, bool established)
    {
        object state = GetRuntimeStateObject(connection);
        FieldInfo field = state.GetType().GetField("HandshakeEstablished", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
        field.SetValue(state, established);
    }

    public static void SetHandshakeState(IConnection connection, object? handshakeState)
    {
        object state = GetRuntimeStateObject(connection);
        FieldInfo field = state.GetType().GetField("HandshakeState", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
        field.SetValue(state, handshakeState);
    }

    private static object GetRuntimeStateObject(IConnection connection)
    {
        MethodInfo getRuntimeState = typeof(ConnectionExtensions).GetMethod(
            "GetRuntimeState",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
        return getRuntimeState.Invoke(null, [connection])!;
    }
}
