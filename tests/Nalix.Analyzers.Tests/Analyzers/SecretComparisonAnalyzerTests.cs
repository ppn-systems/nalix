// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Threading.Tasks;
using Nalix.Analyzers.Tests.TestSupport;
using Xunit;

namespace Nalix.Analyzers.Tests.Analyzers;

public sealed class SecretComparisonAnalyzerTests
{
    private static string Source(string members) => $$"""
namespace Demo;
using System.Security.Cryptography;

public sealed class Validator
{
    private readonly byte[] _challenge = new byte[8];
    private readonly byte[] _macAddress = new byte[6];

    {{members}}
}
""";

    [Theory]
    [InlineData("public bool Check(ReadOnlySpan<byte> data) => data.SequenceEqual(_challenge);")]
    [InlineData("public bool Check(ReadOnlySpan<byte> data) => _challenge.AsSpan().SequenceEqual(data);")]
    [InlineData("public bool Check(ReadOnlySpan<byte> computed, ReadOnlySpan<byte> expectedHmac) => computed.SequenceEqual(expectedHmac);")]
    [InlineData("public bool Check(ReadOnlySpan<byte> computed, byte[] sessionToken) => computed.SequenceEqual(sessionToken);")]
    [InlineData("public bool Check(byte[] buffer, byte[] authTag) => buffer.AsSpan(0, 16).SequenceEqual(authTag);")]
    [InlineData("public bool Check(byte[] buffer, byte[] mac) => buffer[..16].AsSpan().SequenceEqual(mac);")]
    [InlineData("public bool Check(byte[] a, byte[] passwordHash_Tag) => a.SequenceEqual(passwordHash_Tag);")]
    [InlineData("public bool Check(byte[] a, byte[] HMACKey) => a.AsSpan().SequenceEqual(HMACKey);")]
    public async Task Analyze_SequenceEqualOnSecret_ReportsNalix117(string members)
    {
        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(Source(members), "NALIX117");
    }

    [Theory]
    [InlineData("public bool Check(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => a.SequenceEqual(b);")]
    [InlineData("public bool Check(ReadOnlySpan<byte> data, byte[] payload) => data.SequenceEqual(payload);")]
    [InlineData("public bool Check(ReadOnlySpan<byte> data) => data.SequenceEqual(_macAddress);")]
    [InlineData("public bool Check(ReadOnlySpan<byte> data, byte[] vendorAddrTag) => data.SequenceEqual(vendorAddrTag);")]
    [InlineData("public bool Check(ReadOnlySpan<byte> data, byte[] stagger) => data.SequenceEqual(stagger);")]
    [InlineData("public bool Check(ReadOnlySpan<byte> data, byte[] macro) => data.SequenceEqual(macro);")]
    [InlineData("public bool Check(ReadOnlySpan<int> data, int[] tag) => data.SequenceEqual(tag);")]
    [InlineData("public bool Check(ReadOnlySpan<byte> data) => CryptographicOperations.FixedTimeEquals(data, _challenge);")]
    public async Task Analyze_NonSecretOrFixedTimeComparison_ReportsNoDiagnostic(string members)
    {
        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(Source(members));
    }
}
