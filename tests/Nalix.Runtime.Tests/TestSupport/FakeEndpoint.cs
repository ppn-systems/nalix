// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Networking;

namespace Nalix.Runtime.Tests.TestSupport;

internal sealed class FakeEndpoint : INetworkEndpoint
{
    public string Address => "127.0.0.1";

    public int Port => 12345;

    public bool HasPort => true;

    public bool IsIPv6 => false;
}
