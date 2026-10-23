// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Serialization;
using Nalix.Analyzers.CodeFixes;
using Nalix.Analyzers.Tests.TestSupport;
using Xunit;
using System.Threading.Tasks;

namespace Nalix.Analyzers.Tests.Analyzers;

public sealed class CryptoUsageAnalyzerTests
{
    [Fact]
    public async Task Analyze_Sha256InCoreAssembly_ReportsNalix071()
    {
        const string source = """
namespace Nalix.Codec.Security;

public static class Hasher
{
    public static byte[] Hash(byte[] data)
    {
        return System.Security.Cryptography.SHA256.HashData(data);
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsInAssemblyAsync(source, "Nalix.Codec", "NALIX071");
    }

    [Fact]
    public async Task Analyze_RngFillInCoreAssembly_ReportsNalix071()
    {
        const string source = """
namespace Nalix.Environment.Random;

public static class RngHelper
{
    public static void Fill(System.Span<byte> buffer)
    {
        System.Security.Cryptography.RandomNumberGenerator.Fill(buffer);
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsInAssemblyAsync(source, "Nalix.Environment", "NALIX071");
    }

    [Fact]
    public async Task Analyze_OsCsprngAllowlistInCoreAssembly_ReportsNoDiagnostic()
    {
        const string source = """
namespace Nalix.Environment.Random;

public static class OsCsprng
{
    public static void Fill(System.Span<byte> buffer)
    {
        System.Security.Cryptography.RandomNumberGenerator.Fill(buffer);
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsInAssemblyAsync(source, "Nalix.Environment");
    }

    [Fact]
    public async Task Analyze_NalixInternalCryptoInCoreAssembly_ReportsNoDiagnostic()
    {
        const string source = """
namespace Nalix.Codec.Security.Hashing;

public static class MyHasher
{
    public static byte[] Hash(byte[] data)
    {
        return Nalix.Codec.Security.Hashing.Keccak256.HashData(data);
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsInAssemblyAsync(source, "Nalix.Codec");
    }

    [Fact]
    public async Task Analyze_CryptoInNonCoreAssembly_ReportsNoDiagnostic()
    {
        const string source = """
namespace MyApp.Security;

public static class Hasher
{
    public static byte[] Hash(byte[] data)
    {
        return System.Security.Cryptography.SHA256.HashData(data);
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsInAssemblyAsync(source, "MyApp");
    }

    [Fact]
    public async Task Analyze_CryptoInTestAssembly_ReportsNoDiagnostic()
    {
        const string source = """
namespace Nalix.Codec.Tests;

public static class Hasher
{
    public static byte[] Hash(byte[] data)
    {
        return System.Security.Cryptography.SHA256.HashData(data);
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsInAssemblyAsync(source, "Nalix.Tests");
    }

    [Fact]
    public async Task Analyze_FixedTimeEqualsInCoreAssembly_ReportsNoDiagnostic()
    {
        const string source = """
namespace Nalix.Codec.Security;

public static class Verifier
{
    public static bool Verify(System.ReadOnlySpan<byte> expected, System.ReadOnlySpan<byte> actual)
    {
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsInAssemblyAsync(source, "Nalix.Codec");
    }
}
