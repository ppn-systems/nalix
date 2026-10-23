// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Reflection;
using Nalix.Environment.Configuration;
using Nalix.Network.Options;

namespace Nalix.Integration.Tests.TestSupport;

/// <summary>
/// Captures the shared network option instances held by <see cref="ConfigurationManager"/> when it is created
/// and writes the captured values back when it is disposed, so a test can raise limits without leaking them.
/// </summary>
internal sealed class NetworkConfigSnapshot : IDisposable
{
    private readonly List<(object Instance, PropertyInfo Property, object? Value)> _saved = [];

    public NetworkConfigSnapshot()
    {
        this.Capture(ConfigurationManager.Instance.Get<ConnectionGuardOptions>());
        this.Capture(ConfigurationManager.Instance.Get<ConnectionQuotaOptions>());
        this.Capture(ConfigurationManager.Instance.Get<NetworkWebSocketOptions>());
    }

    public void Dispose()
    {
        foreach ((object instance, PropertyInfo property, object? value) in _saved)
        {
            property.SetValue(instance, value);
        }

        _saved.Clear();
    }

    private void Capture(object instance)
    {
        foreach (PropertyInfo property in instance.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.CanRead && property.CanWrite && property.GetIndexParameters().Length == 0)
            {
                _saved.Add((instance, property, property.GetValue(instance)));
            }
        }
    }
}
