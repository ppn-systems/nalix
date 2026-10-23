// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Serialization;
using Nalix.Analyzers.CodeFixes;
using Nalix.Analyzers.Tests.TestSupport;
using Xunit;
using System.Threading.Tasks;

namespace Nalix.Analyzers.Tests.CodeFixes;

public sealed class SerializeOrderMissingCodeFixTests
{
    [Fact]
    public async Task Fix_MissingSerializeOrderWithSecondAction_AssignsNextOrder()
    {
        const string source = """
namespace Demo;
using Nalix.Abstractions.Serialization;

[SerializePackable(SerializeLayout.Explicit)]
public sealed class Example
{
    [SerializeOrder(0)]
    public int A { get; set; }

    public int Value { get; set; }
}
""";

        const string fixedSource = """
namespace Demo;
using Nalix.Abstractions.Serialization;

[SerializePackable(SerializeLayout.Explicit)]
public sealed class Example
{
    [SerializeOrder(0)]
    public int A { get; set; }
    [SerializeOrder(1)]
    public int Value { get; set; }
}
""";

        await Verifier<SerializeOrderMissingCodeFixProvider>.VerifyCodeFixAsync(
            source,
            fixedSource,
            "NALIX013",
            actionIndex: 1);
    }

    [Fact]
    public async Task Fix_MissingSerializeOrder_AddsSerializeIgnore()
    {
        const string source = """
namespace Demo;
using Nalix.Abstractions.Serialization;

[SerializePackable(SerializeLayout.Explicit)]
public sealed class Example
{
    public int Value { get; set; }
}
""";

        const string fixedSource = """
namespace Demo;
using Nalix.Abstractions.Serialization;

[SerializePackable(SerializeLayout.Explicit)]
public sealed class Example
{
    [SerializeIgnore]
    public int Value { get; set; }
}
""";

        await Verifier<SerializeOrderMissingCodeFixProvider>.VerifyCodeFixAsync(
            source,
            fixedSource,
            "NALIX013");
    }
}
