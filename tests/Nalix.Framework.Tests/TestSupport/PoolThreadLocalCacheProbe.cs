// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Reflection;

namespace Nalix.Framework.Tests.TestSupport;

internal static class PoolThreadLocalCacheProbe
{
    /// <summary>
    /// Clears the calling thread's pool cache slot for <typeparamref name="T"/> and returns what it held.
    /// </summary>
    public static object? Pop<T>()
    {
        try
        {
            var type = typeof(Nalix.Framework.Memory.Objects.ObjectPoolManager).Assembly
                .GetType("Nalix.Framework.Memory.Internal.PoolTypes.ThreadLocalCache`1")
                ?.MakeGenericType(typeof(T));
            FieldInfo? valueField = type?.GetField("t_value", BindingFlags.NonPublic | BindingFlags.Static);
            FieldInfo? ownerField = type?.GetField("t_owner", BindingFlags.NonPublic | BindingFlags.Static);
            object? value = valueField?.GetValue(null);
            valueField?.SetValue(null, null);
            ownerField?.SetValue(null, null);
            return value;
        }
        catch
        {
            return null;
        }
    }
}
