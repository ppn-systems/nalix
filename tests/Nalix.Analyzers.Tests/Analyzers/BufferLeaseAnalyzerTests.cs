// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Serialization;
using Nalix.Analyzers.CodeFixes;
using Nalix.Analyzers.Tests.TestSupport;
using Nalix.Environment.Memory;
using Xunit;
using System.Threading.Tasks;

namespace Nalix.Analyzers.Tests.Analyzers;

public sealed class BufferLeaseAnalyzerTests
{
    [Fact]
    public async Task Analyze_LeaseParameterNotDisposed_ReportsNalix039()
    {
        const string source = """
namespace Demo;
using Nalix.Abstractions;

public sealed class LeakDemo
{
    public void Process(IBufferLease lease)
    {
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsAsync(source, "NALIX039");
    }
}
