// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Linq;
using Nalix.Analyzers.Generators;
using Nalix.Analyzers.Tests.TestSupport;
using Xunit;

namespace Nalix.Analyzers.Tests.Generators.Internal;

/// <summary>
/// Layer 1 tests for member resolution in <c>SerializationMember</c>, observed through the
/// <see cref="SerializeFormatterGenerator"/> output (the formatter's Serialize body lists the members in wire order).
/// </summary>
public sealed class SerializationMemberTests
{
    [Fact]
    public void Resolve_IgnoredStringPropertySharingTypeWithOrderedField_OmitsPropertyFromSerialize()
    {
        const string source = """
            using Nalix.Abstractions.Serialization;

            namespace GenHarness.Member;

            [GenerateFormatter]
            public sealed class Login
            {
                public static Login Create() => new();

                [SerializeOrder(0)]
                public string Username = "";

                [SerializeIgnore]
                public string PasswordHash { get; set; } = "";
            }
            """;

        string body = SerializeBody(source, "LoginFormatter");

        Assert.Contains("value.Username", body);
        Assert.DoesNotContain("PasswordHash", body);
    }

    [Fact]
    public void Resolve_IgnoredIntPropertySharingTypeWithOrderedField_OmitsPropertyFromSerialize()
    {
        const string source = """
            using Nalix.Abstractions.Serialization;

            namespace GenHarness.Member;

            [GenerateFormatter]
            public sealed class Counter
            {
                public static Counter Create() => new();

                [SerializeOrder(0)]
                public int Id;

                [SerializeIgnore]
                public int Cached { get; set; }
            }
            """;

        string body = SerializeBody(source, "CounterFormatter");

        Assert.Contains("value.Id", body);
        Assert.DoesNotContain("Cached", body);
    }

    [Fact]
    public void Resolve_IgnoredPropertyWithOwnSerializeHeader_KeepsHeaderOnWire()
    {
        // Mirrors FrameBase.Header: [SerializeIgnore] and [SerializeHeader] on the same property. The explicit
        // header attribute on the member itself keeps it on the wire, and its private backing field is not emitted twice.
        const string source = """
            using Nalix.Abstractions.Serialization;

            namespace GenHarness.Member;

            [GenerateFormatter]
            public sealed class Framed
            {
                public static Framed Create() => new();

                [SerializeHeader(0)]
                private int _header;

                [SerializeIgnore]
                [SerializeHeader(0)]
                public int Header { get => _header; set => _header = value; }

                [SerializeOrder(0)]
                public int Payload;
            }
            """;

        string body = SerializeBody(source, "FramedFormatter");

        Assert.Contains("value.Header", body);
        Assert.Contains("value.Payload", body);
        Assert.DoesNotContain("_header", body);
    }

    [Fact]
    public void Resolve_UnattributedPropertySharingTypeWithOrderedField_KeepsDeclaredWireOrder()
    {
        // Nickname has no attribute and no backing field; it must not inherit Username's [SerializeOrder(0)].
        const string source = """
            using Nalix.Abstractions.Serialization;

            namespace GenHarness.Member;

            [GenerateFormatter]
            public sealed class Profile
            {
                public static Profile Create() => new();

                [SerializeOrder(0)]
                public string Username = "";

                [SerializeOrder(1)]
                public string Email { get; set; } = "";

                public string Nickname { get; set; } = "";
            }
            """;

        string body = SerializeBody(source, "ProfileFormatter");

        int username = body.IndexOf("value.Username", System.StringComparison.Ordinal);
        int email = body.IndexOf("value.Email", System.StringComparison.Ordinal);
        int nickname = body.IndexOf("value.Nickname", System.StringComparison.Ordinal);

        Assert.True(username >= 0 && email > username && nickname > email, body);
    }

    [Fact]
    public void Resolve_IgnoredWrapperOfOrderedPrivateField_OmitsWrapperFromSerialize()
    {
        const string source = """
            using Nalix.Abstractions.Serialization;

            namespace GenHarness.Member;

            [GenerateFormatter]
            public sealed class Token
            {
                public static Token Create() => new();

                [SerializeOrder(0)]
                private int _secret;

                [SerializeIgnore]
                public int Secret => _secret;

                [SerializeOrder(1)]
                public int Other { get; set; }
            }
            """;

        string body = SerializeBody(source, "TokenFormatter");

        Assert.Contains("value.Other", body);
        Assert.DoesNotContain("value.Secret", body);
        Assert.DoesNotContain("_secret", body);
    }

    [Fact]
    public void Resolve_ManualWrapperOfPrivateField_UsesItsOwnOrderNotSameTypeSibling()
    {
        // Second wraps _second (order 1). First (order 0) is an unrelated same-typed field declared before _second.
        // The wrapper must not borrow First's order; First must be written before Second.
        const string source = """
            using Nalix.Abstractions.Serialization;

            namespace GenHarness.Member;

            [GenerateFormatter]
            public sealed class Pair
            {
                public static Pair Create() => new();

                public int Second => _second;

                [SerializeOrder(0)]
                public int First;

                [SerializeOrder(1)]
                private int _second;
            }
            """;

        string body = SerializeBody(source, "PairFormatter");

        int first = body.IndexOf("value.First", System.StringComparison.Ordinal);
        int second = body.IndexOf("value.Second", System.StringComparison.Ordinal);

        Assert.True(first >= 0 && second > first, body);
    }

    private static string SerializeBody(string source, string formatterName)
    {
        GeneratorDriverHarness.Result result = GeneratorDriverHarness.Run(new SerializeFormatterGenerator(), source);

        string text = result.GeneratedSources
            .Single(s => s.HintName.Contains(formatterName)).Text;

        int start = text.IndexOf("public void Serialize(", System.StringComparison.Ordinal);
        int end = text.IndexOf("public ", start + 1, System.StringComparison.Ordinal);
        return end < 0 ? text[start..] : text[start..end];
    }
}
