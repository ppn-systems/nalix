// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Codec.DataFrames;
using Nalix.Runtime.Routing;

namespace Nalix.Runtime.Tests.TestSupport;

public interface IScopedEchoService
{
    Guid Id { get; }
}
