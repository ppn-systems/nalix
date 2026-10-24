// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Nalix.Abstractions.Primitives;

namespace Nalix.Codec.Serialization.Internal.Types;

#pragma warning disable IDE0072 // Populate switch

internal static partial class TypeMetadata
{
    [StackTraceHidden]
    [DebuggerStepThrough]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsReferenceOrContainsReferences(Type type) => IsReferenceOrContainsReferencesFallback(type, []);

    [StackTraceHidden]
    [DebuggerStepThrough]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int UnsafeSizeOf(Type type)
    {
        return Type.GetTypeCode(type) switch
        {
            TypeCode.Boolean or TypeCode.Byte or TypeCode.SByte => 1,
            TypeCode.Char or TypeCode.Int16 or TypeCode.UInt16 => 2,
            TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Single => 4,
            TypeCode.Int64 or TypeCode.UInt64 or TypeCode.Double => 8,
            TypeCode.Decimal => 16,
            TypeCode.DateTime => 8,
            TypeCode.Object when type == typeof(Guid) => 16,
            TypeCode.Object when type == typeof(TimeSpan) => 8,
            TypeCode.Object when type == typeof(TimeOnly) => 8,
            TypeCode.Object when type == typeof(DateOnly) => 4,
            TypeCode.Object when type == typeof(DateTimeOffset) => 16,
            TypeCode.Object when type == typeof(Bytes32) => Bytes32.Size,
            TypeCode.Object when type == typeof(PacketHeader) => PacketHeader.Size,
            _ when type.IsEnum => UnsafeSizeOf(Enum.GetUnderlyingType(type)),
            _ => IntPtr.Size
        };
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "Inspects value-type fields for unmanaged-ness. Value-type field metadata is always preserved for layout.")]
    private static bool IsReferenceOrContainsReferencesFallback(
        Type type,
        System.Collections.Generic.HashSet<Type> visited)
    {
        if (!type.IsValueType)
        {
            return true;
        }

        if (type.IsPrimitive || type.IsEnum || type == typeof(decimal) || type == typeof(Guid) ||
            type == typeof(DateOnly) || type == typeof(TimeOnly) || type == typeof(DateTime) ||
            type == typeof(TimeSpan) || type == typeof(DateTimeOffset))
        {
            return false;
        }

        if (!visited.Add(type))
        {
            return false;
        }

        foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (IsReferenceOrContainsReferencesFallback(field.FieldType, visited))
            {
                return true;
            }
        }

        return false;
    }
}
