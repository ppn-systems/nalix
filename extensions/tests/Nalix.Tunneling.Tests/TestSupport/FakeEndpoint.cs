// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Networking;

namespace Nalix.Tunneling.Tests.TestSupport;

internal sealed class FakeEndpoint(string address) : INetworkEndpoint
{
    public string Address { get; } = address;

    public int Port => 12345;

    public bool HasPort => true;

    public bool IsIPv6 => false;
}
