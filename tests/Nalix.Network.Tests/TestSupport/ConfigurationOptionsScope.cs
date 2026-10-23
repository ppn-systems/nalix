// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Reflection;
using Nalix.Environment.Configuration;
using Nalix.Environment.Configuration.Binding;

namespace Nalix.Network.Tests.TestSupport;

/// <summary>
/// Captures every public read/write property of a shared <see cref="ConfigurationManager"/> options instance
/// and restores the captured values on dispose, so a test can mutate process-wide options without leaking them.
/// </summary>
internal sealed class ConfigurationOptionsScope<TOptions> : IDisposable
    where TOptions : ConfigurationLoader, new()
{
    private readonly TOptions _options;
    private readonly List<(PropertyInfo Property, object? Value)> _saved = [];

    public ConfigurationOptionsScope()
    {
        _options = ConfigurationManager.Instance.Get<TOptions>();

        foreach (PropertyInfo property in typeof(TOptions).GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.GetIndexParameters().Length == 0 && property.CanRead && property.GetSetMethod() is not null)
            {
                _saved.Add((property, property.GetValue(_options)));
            }
        }
    }

    public TOptions Options => _options;

    public void Dispose()
    {
        foreach ((PropertyInfo property, object? value) in _saved)
        {
            property.SetValue(_options, value);
        }
    }
}
