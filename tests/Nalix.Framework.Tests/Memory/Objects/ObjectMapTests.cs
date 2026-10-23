// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Generic;
using Nalix.Framework.Memory.Objects;

namespace Nalix.Framework.Tests.Memory.Objects;

public sealed class ObjectMapTests
{
    [Fact]
    public void Add_WhenEntriesAdded_UpdatesCollectionMembers()
    {
        ObjectMap<string, int> map = new()
        {
            { "one", 1 },
            new KeyValuePair<string, int>("two", 2)
        };

        map["three"] = 3;
        bool containsPair = map.Contains(new KeyValuePair<string, int>("two", 2));
        bool containsKey = map.ContainsKey("one");
        bool tryGetValue = map.TryGetValue("three", out int value);
        KeyValuePair<string, int>[] copied = new KeyValuePair<string, int>[4];
        map.CopyTo(copied, 1);

        Assert.Equal(3, map.Count);
        Assert.False(map.IsReadOnly);
        Assert.Equal(3, map["three"]);
        Assert.True(containsPair);
        Assert.True(containsKey);
        Assert.True(tryGetValue);
        Assert.Equal(3, value);
        Assert.Equal(3, map.Keys.Count);
        Assert.Equal(3, map.Values.Count);
        Assert.Equal(3, map.ToArray().Length);
        Assert.Contains(copied, static item => item.Key == "one");
    }

    [Fact]
    public void ResetForPool_WhenMapHasEntries_ClearsMap()
    {
        ObjectMap<string, int> map = ObjectMap<string, int>.Rent();
        map.Add("one", 1);
        map.Add("two", 2);

        bool removedByKey = map.Remove("one");
        bool removedByPair = map.Remove(new KeyValuePair<string, int>("two", 2));
        map.ResetForPool();
        map.Return();

        Assert.True(removedByKey);
        Assert.True(removedByPair);
        Assert.Empty(map);
    }
}
