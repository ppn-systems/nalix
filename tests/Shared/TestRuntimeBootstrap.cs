// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Runtime.CompilerServices;
using Nalix.Abstractions;
using Nalix.Abstractions.Concurrency;
using Nalix.Framework.Injection;
using Nalix.Framework.Memory.Objects;
using Nalix.Framework.Options;
using Nalix.Framework.Tasks;
using Nalix.Network.Connections;

namespace Nalix.Tests.Shared;

/// <summary>
/// Registers the process-wide services that network-facing test assemblies need before any test runs.
/// This file is linked into every test project that references <c>Nalix.Network</c>
/// (see <c>tests/Directory.Build.props</c>); it is not a test project of its own.
/// </summary>
internal static class TestRuntimeBootstrap
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        TaskManager taskManager = new();
        InstanceManager.Instance.Register<ITaskManager>(taskManager);

        try
        {
            ObjectPoolOptions config = new() { EnableMetrics = false, EnableDiagnostics = false };
            ObjectPoolManager poolManager = new(config);
            ObjectPoolManager.Configure(poolManager);
            InstanceManager.Instance.Register<IObjectPoolManager>(poolManager);
            InstanceManager.Instance.Register<ObjectPoolManager>(poolManager);
        }
        catch (System.InvalidOperationException)
        {
            // The pool manager was already configured by another initializer; keep the existing one.
        }

        RuntimeHelpers.RunClassConstructor(typeof(Connection).TypeHandle);
        RuntimeHelpers.RunClassConstructor(typeof(WebSocketConnection).TypeHandle);
        RuntimeHelpers.RunClassConstructor(typeof(PassthroughConnection).TypeHandle);
    }
}
