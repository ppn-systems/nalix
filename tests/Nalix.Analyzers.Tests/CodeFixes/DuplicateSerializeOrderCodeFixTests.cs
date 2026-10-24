// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Serialization;
using Nalix.Analyzers.CodeFixes;
using Nalix.Analyzers.Tests.TestSupport;
using Xunit;
using System.Threading.Tasks;

namespace Nalix.Analyzers.Tests.CodeFixes;

public sealed class DuplicateSerializeOrderCodeFixTests
{
    [Fact]
    public async Task Fix_DuplicateSerializeOrder_RenumbersDuplicateMember()
    {
        const string source = """
namespace Demo;
using Nalix.Abstractions.Serialization;

[SerializePackable(SerializeLayout.Explicit)]
public sealed class Example
{
    [SerializeOrder(0)]
    public int A { get; set; }

    [SerializeOrder(0)]
    public int B { get; set; }
}
""";

        const string fixedSource = """
namespace Demo;
using Nalix.Abstractions.Serialization;

[SerializePackable(SerializeLayout.Explicit)]
public sealed class Example
{
    [SerializeOrder(1)]
    public int A { get; set; }

    [SerializeOrder(0)]
    public int B { get; set; }
}
""";

        await Verifier<DuplicateSerializeOrderCodeFixProvider>.VerifyCodeFixAsync(
            source,
            fixedSource,
            "NALIX014");
    }
}
