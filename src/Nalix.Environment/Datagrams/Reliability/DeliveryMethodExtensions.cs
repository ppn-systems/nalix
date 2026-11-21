// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Networking.Datagrams;

namespace Nalix.Environment.Datagrams.Reliability;

/// <summary>
/// Properties of <see cref="DeliveryMethod"/> values used by the engine.
/// </summary>
internal static class DeliveryMethodExtensions
{
    public static bool IsReliable(this DeliveryMethod method) => method >= DeliveryMethod.ReliableUnordered;

    public static bool HasOrderIndex(this DeliveryMethod method)
        => method is DeliveryMethod.UnreliableSequenced or DeliveryMethod.ReliableOrdered or DeliveryMethod.ReliableSequenced;
}
