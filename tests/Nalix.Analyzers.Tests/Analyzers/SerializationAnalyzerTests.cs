// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Serialization;
using Nalix.Analyzers.CodeFixes;
using Nalix.Analyzers.Tests.TestSupport;
using Xunit;
using System.Threading.Tasks;

namespace Nalix.Analyzers.Tests.Analyzers;

public sealed class SerializationAnalyzerTests
{
    [Fact]
    public async Task Analyze_NegativeSerializeOrderMember_ReportsNalix021()
    {
        const string source = """
namespace Demo;
using Nalix.Abstractions.Serialization;

[SerializePackable(SerializeLayout.Explicit)]
public sealed class Example
{
    [SerializeOrder(-1)]
    public int Value { get; set; }
}
""";

        await Verifier<SerializeOrderMissingCodeFixProvider>.VerifyAnalyzerAsync(
            source,
            "NALIX021");
    }

    [Fact]
    public async Task Analyze_ExplicitMemberWithoutSerializeOrder_ReportsNalix013()
    {
        const string source = """
namespace Demo;
using Nalix.Abstractions.Serialization;

[SerializePackable(SerializeLayout.Explicit)]
public sealed class ExplicitPacket
{
    public int A { get; set; }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(source, "NALIX013");
    }

    [Fact]
    public async Task Analyze_DuplicateSerializeOrder_ReportsNalix014()
    {
        const string source = """
namespace Demo;
using Nalix.Abstractions.Serialization;

[SerializePackable(SerializeLayout.Explicit)]
public sealed class ExplicitPacket
{
    [SerializeOrder(1)]
    public int A { get; set; }

    [SerializeOrder(1)]
    public int B { get; set; }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(source, "NALIX014", "NALIX014");
    }

    [Fact]
    public async Task Analyze_HeaderInBaseAndOrderInDerived_ReportsNoDiagnostic()
    {
        const string source = """
namespace Demo;
using Nalix.Abstractions.Serialization;

[SerializePackable(SerializeLayout.Explicit)]
public abstract class BasePacket
{
    [SerializeHeader(0)]
    public int Header { get; set; }
}

[SerializePackable(SerializeLayout.Explicit)]
public sealed class DerivedPacket : BasePacket
{
    [SerializeOrder(0)]
    public int Payload { get; set; }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(source);
    }

    [Fact]
    public async Task Analyze_SerializeIgnoreWithOrder_ReportsNalix015()
    {
        const string source = """
namespace Demo;
using Nalix.Abstractions.Serialization;

[SerializePackable(SerializeLayout.Explicit)]
public sealed class ExplicitPacket
{
    [SerializeIgnore]
    [SerializeOrder(2)]
    public int A { get; set; }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(source, "NALIX015");
    }

    [Fact]
    public async Task Analyze_DynamicSizeOnFixedSizeMember_ReportsNalix016()
    {
        const string source = """
namespace Demo;
using Nalix.Abstractions.Serialization;

[SerializePackable(SerializeLayout.Explicit)]
public sealed class ExplicitPacket
{
    [SerializeOrder(1)]
    [SerializeDynamicSize]
    public int A { get; set; }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(source, "NALIX016");
    }

    [Fact]
    public async Task Analyze_NegativeSerializeOrder_ReportsNalix021()
    {
        const string source = """
namespace Demo;
using Nalix.Abstractions.Serialization;

[SerializePackable(SerializeLayout.Explicit)]
public sealed class ExplicitPacket
{
    [SerializeOrder(-1)]
    public int A { get; set; }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(source, "NALIX021");
    }

    [Fact]
    public async Task Analyze_LargeSerializeOrderGap_ReportsNalix046()
    {
        const string source = """
namespace Demo;
using Nalix.Abstractions.Serialization;

[SerializePackable(SerializeLayout.Explicit)]
public sealed class ExplicitPacket
{
    [SerializeOrder(1)]
    public int A { get; set; }

    [SerializeOrder(200)]
    public int B { get; set; }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(source, "NALIX046");
    }

    [Fact]
    public async Task Analyze_FixedSizeTypeWithDynamicMember_ReportsNalix051()
    {
        const string source = """
namespace Demo;
using Nalix.Abstractions.Serialization;

[SerializePackable(SerializeLayout.Explicit)]
public sealed class FixedType : IFixedSizeSerializable
{
    [SerializeOrder(1)]
    public string Name { get; set; } = string.Empty;

    public static int Size => 10;
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(source, "NALIX051");
    }
}
