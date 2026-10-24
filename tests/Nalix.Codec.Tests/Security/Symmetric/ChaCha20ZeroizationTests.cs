// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Linq;
using System.Reflection;
using Nalix.Codec.Security.Symmetric;

namespace Nalix.Codec.Tests.Security.Symmetric;

/// <summary>
/// Verifies that <see cref="Nalix.Codec.Security.Symmetric.ChaCha20"/> zeroes its internal key-material buffers on
/// <c>Clear()</c>. Reflection is used to read the private instance fields directly.
/// </summary>
public sealed class ChaCha20ZeroizationTests
{
    private static bool AllFieldBytesAreZero(object instance, string fieldName)
    {
        FieldInfo? field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException($"Field '{fieldName}' not found via reflection on {instance.GetType()} — internals may be inaccessible in this build configuration.");

        object? value = field.GetValue(instance);
        Assert.NotNull(value);

        byte[] bytes = ExtractBytes(value!);
        return bytes.All(b => b == 0);
    }

    private static byte[] ExtractBytes(object structValue)
    {
        Type t = structValue.GetType();
        int size = System.Runtime.InteropServices.Marshal.SizeOf(structValue);
        byte[] buffer = new byte[size];
        nint ptr = System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
        try
        {
            System.Runtime.InteropServices.Marshal.StructureToPtr(structValue, ptr, false);
            System.Runtime.InteropServices.Marshal.Copy(ptr, buffer, 0, size);
            return buffer;
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(ptr);
        }
    }

    // Poly1305 is a `ref struct` — it cannot be boxed to `object` nor targeted by
    // `__makeref`/`TypedReference` (CS1601), so its Clear() zeroization cannot be verified via
    // reflection in this harness. Untestable item; Security/Hashing/Poly1305Tests.cs already covers
    // post-Clear ObjectDisposedException behavior (not byte-level zeroing).

    [Fact]
    public void Clear_AfterKeyBlockGeneration_ZeroesStateAndKeystreamBuffers()
    {
        byte[] key = new byte[32];
        byte[] nonce = new byte[12];
        ChaCha20 cipher = new(key, nonce, 0u);

        // Exercise it once so the buffers are populated with non-zero derived state.
        byte[] block = new byte[64];
        cipher.GenerateKeyBlock(block);

        cipher.Clear();

        Assert.True(AllFieldBytesAreZero(cipher, "_state"), "ChaCha20._state was not fully zeroed after Clear().");
        Assert.True(AllFieldBytesAreZero(cipher, "_keystream"), "ChaCha20._keystream was not fully zeroed after Clear().");
    }
}
