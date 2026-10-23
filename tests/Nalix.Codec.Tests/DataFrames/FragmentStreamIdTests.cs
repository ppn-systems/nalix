// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Reflection;
using Nalix.Environment.Fragments;

namespace Nalix.Codec.Tests.DataFrames;

public sealed class FragmentStreamIdTests
{
    private static readonly Lock s_gate = new();
    private static readonly FieldInfo s_counterField =
        typeof(FragmentStreamId).GetField("s_counter", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static uint GetCounter() => (uint)s_counterField.GetValue(null)!;

    private static void SetCounter(uint value) => s_counterField.SetValue(null, value);

    [Fact]
    public void Next_WhenCounterStartsAtZero_StartsAtOneAndIncrements()
    {
        lock (s_gate)
        {
            uint old = GetCounter();
            try
            {
                SetCounter(0);

                ushort first = FragmentStreamId.Next();
                ushort second = FragmentStreamId.Next();
                ushort third = FragmentStreamId.Next();

                Assert.Equal((ushort)1, first);
                Assert.Equal((ushort)2, second);
                Assert.Equal((ushort)3, third);
            }
            finally
            {
                SetCounter(old);
            }
        }
    }

    [Fact]
    public void Next_WhenCounterWrapsPastUshortMax_SkipsZero()
    {
        lock (s_gate)
        {
            uint old = GetCounter();
            try
            {
                // Interlocked.Increment -> 65536, ushort cast -> 0, should skip to 1.
                SetCounter(65535);

                ushort wrapped = FragmentStreamId.Next();
                ushort next = FragmentStreamId.Next();
                ushort third = FragmentStreamId.Next();

                Assert.Equal((ushort)1, wrapped);
                Assert.Equal((ushort)1, next);
                Assert.Equal((ushort)2, third);
            }
            finally
            {
                SetCounter(old);
            }
        }
    }

    [Fact]
    public void Next_WhenCalledManyTimes_NeverReturnsZero()
    {
        lock (s_gate)
        {
            uint old = GetCounter();
            try
            {
                SetCounter(0);
                for (int i = 0; i < 5000; i++)
                {
                    Assert.NotEqual((ushort)0, FragmentStreamId.Next());
                }
            }
            finally
            {
                SetCounter(old);
            }
        }
    }

    [Fact]
    public void Next_WhenCalledRepeatedly_NeverReturnsZero()
    {
        ushort[] values = new ushort[128];

        for (int index = 0; index < values.Length; index++)
        {
            values[index] = FragmentStreamId.Next();
        }

        Assert.DoesNotContain((ushort)0, values);
    }
}
