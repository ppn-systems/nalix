// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Security;
using Xunit;

namespace Nalix.Abstractions.Tests.Security;

/// <summary>
/// Verifies the ordering that the observability permission depends on.
/// </summary>
public sealed class PermissionLevelTests
{
    [Fact]
    public void Observer_IsAtLeastEstablished_SoFragmentsAreAccepted()
    {
        Assert.True(PermissionLevel.OBSERVER >= PermissionLevel.ESTABLISHED);
    }

    [Fact]
    public void Observer_IsBelowGuest_SoItSatisfiesNoGuestOrHigherHandler()
    {
        Assert.True(PermissionLevel.OBSERVER < PermissionLevel.GUEST);
    }
}
