// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Serialization;

namespace Nalix.Codec.Tests.TestSupport;

/// <summary>
/// Test-only type: an ordered field next to a [SerializeIgnore] property of the same type.
/// </summary>
[GenerateFormatter]
public sealed class SerializeIgnoreLogin
{
    public static SerializeIgnoreLogin Create() => new();

    [SerializeOrder(0)]
    public string Username = "";

    [SerializeIgnore]
    public string PasswordHash { get; set; } = "";
}
