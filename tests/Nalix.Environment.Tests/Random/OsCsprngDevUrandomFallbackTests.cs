// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Reflection;
using Nalix.Environment.Random;

namespace Nalix.Environment.Tests.Random;

public sealed class OsCsprngDevUrandomFallbackTests
{
    private delegate void FillSpan(Span<byte> buffer);

    [Fact]
    public void DevUrandomFallback_WhenInvokedDirectly_FillsBufferWithoutThrowing()
    {
        if (!File.Exists("/dev/urandom"))
        {
            return;
        }

        // D is the /dev/urandom fallback used when getrandom is unavailable (ENOSYS), on Apple platforms
        // and on other Unix systems. It is private, so bind a delegate to it.
        MethodInfo method = typeof(OsCsprng).GetMethod("D", BindingFlags.NonPublic | BindingFlags.Static)!;
        FillSpan fill = method.CreateDelegate<FillSpan>();

        byte[] buffer = new byte[64];
        fill(buffer);

        Assert.Contains(buffer, b => b != 0);
    }
}
