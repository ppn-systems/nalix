// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Linq;
using System.Reflection;
using Nalix.Codec.Security.Symmetric;
using Xunit;

namespace Nalix.Framework.Tests.Cryptography;

/// <summary>
/// Verifies that key-material-holding disposable/clearable types actually zero their internal
/// buffers on <c>Clear()</c>/<c>Dispose()</c>. Reflection is used to read private instance
/// fields directly (reflection bypasses normal C# accessibility, so this works in Release
/// builds even though the DEBUG-only <c>InternalsVisibleTo</c> grant does not apply here).
/// </summary>
public sealed class ZeroizationTests
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

    [Fact]
    public void ChaCha20ClearZeroesStateAndKeystreamBuffers()
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

    // Poly1305 is a `ref struct` — it cannot be boxed to `object` nor targeted by
    // `__makeref`/`TypedReference` (CS1601), so its Clear() zeroization cannot be verified via
    // reflection in this harness. Untestable item; Poly1305Tests.cs already covers
    // post-Clear ObjectDisposedException behavior (not byte-level zeroing).

    [Fact]
    public void MemorySecurityZeroMemoryClearsSpanCompletely()
    {
        byte[] buffer = new byte[64];
        for (int i = 0; i < buffer.Length; i++)
        {
            buffer[i] = (byte)(i + 1);
        }

        Nalix.Codec.Security.Primitives.MemorySecurity.ZeroMemory(buffer);

        Assert.All(buffer, b => Assert.Equal(0, b));
    }
}
