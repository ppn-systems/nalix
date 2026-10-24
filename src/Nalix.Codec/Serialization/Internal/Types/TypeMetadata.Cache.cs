// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Serialization;

namespace Nalix.Codec.Serialization.Internal.Types;

internal static partial class TypeMetadata
{
    private const BindingFlags Flags =
        BindingFlags.Static |
        BindingFlags.Public |
        BindingFlags.Instance |
        BindingFlags.NonPublic |
        BindingFlags.FlattenHierarchy;

    public const DynamicallyAccessedMemberTypes PropertyAccess =
        DynamicallyAccessedMemberTypes.PublicProperties |
        DynamicallyAccessedMemberTypes.NonPublicProperties |
        DynamicallyAccessedMemberTypes.PublicFields |
        DynamicallyAccessedMemberTypes.NonPublicFields;

    private static class Cache<[DynamicallyAccessedMembers(PropertyAccess)] T>
    {
        public static bool IsUnmanaged;
        public static bool IsNullable;
        public static bool IsReference;
        public static bool IsFixedSizeSerializable;
        public static bool IsCompositeSerializable;

        public static int SerializableFixedSize;

        static Cache()
        {
            Type type = typeof(T);

            try
            {
                IsReference = !type.IsValueType;
                IsNullable = Nullable.GetUnderlyingType(type) != null;
                IsUnmanaged = !RuntimeHelpers.IsReferenceOrContainsReferences<T>();

                if (typeof(IFixedSizeSerializable).IsAssignableFrom(type))
                {
                    PropertyInfo? prop = type.GetProperty(nameof(IFixedSizeSerializable.Size), Flags);

                    // NALIX078: Intentional one-time metadata inspection during type cache initialization.
                    // The type parameter T is annotated with [DynamicallyAccessedMembers(PropertyAccess)],
                    // ensuring properties are preserved by the trimmer. This reads a static constant
                    // (IFixedSizeSerializable.Size) and runs once per type, not on serialization hot paths.
#pragma warning disable NALIX078
                    if (prop?.GetValue(null) is int size)
#pragma warning restore NALIX078
                    {
                        IsFixedSizeSerializable = true;
                        SerializableFixedSize = size;
                    }
                }
            }
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
            {
                IsFixedSizeSerializable = false;
                IsCompositeSerializable = false;
            }
        }
    }
}
