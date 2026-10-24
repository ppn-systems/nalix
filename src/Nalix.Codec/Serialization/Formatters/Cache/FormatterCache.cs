// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

#if DEBUG

#endif

namespace Nalix.Codec.Serialization.Formatters.Cache;

/// <summary>
/// Provides a static cache for storing and retrieving formatters for specific types.
/// </summary>
/// <typeparam name="T">The type for which the formatter is stored.</typeparam>
[System.Diagnostics.DebuggerStepThrough]
internal static class FormatterCache<
    [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors |
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties |
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.NonPublicProperties)] T>
{
    /// <summary>
    /// The cached formatter instance for the specified type <typeparamref name="T"/>.
    /// </summary>
    public static IFormatter<T> Instance = null!;
}
