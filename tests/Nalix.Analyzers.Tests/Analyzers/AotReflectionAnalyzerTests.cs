// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Serialization;
using Nalix.Analyzers.CodeFixes;
using Nalix.Analyzers.Tests.TestSupport;
using Xunit;
using System.Threading.Tasks;

namespace Nalix.Analyzers.Tests.Analyzers;

public sealed class AotReflectionAnalyzerTests
{
    [Fact]
    public async Task Analyze_AssemblyGetTypesInCoreAssembly_ReportsNalix078()
    {
        const string source = """
namespace Nalix.Framework.Injection;

public static class Scanner
{
    public static System.Type[] Scan()
    {
        return typeof(Scanner).Assembly.GetTypes();
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsInAssemblyAsync(source, "Nalix.Framework", "NALIX078");
    }

    [Fact]
    public async Task Analyze_AppDomainGetAssembliesInCoreAssembly_ReportsNalix078()
    {
        const string source = """
namespace Nalix.Framework.Injection;

public static class Scanner
{
    public static System.Reflection.Assembly[] GetAll()
    {
        return System.AppDomain.CurrentDomain.GetAssemblies();
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsInAssemblyAsync(source, "Nalix.Framework", "NALIX078");
    }

    [Fact]
    public async Task Analyze_ExpressionCompileInCoreAssembly_ReportsNalix078()
    {
        const string source = """
namespace Nalix.Framework.Injection;

using System.Linq.Expressions;

public static class Factory
{
    public static Func<T> CreateFactory<T>() where T : new()
    {
        Expression<Func<T>> expr = () => new T();
        return expr.Compile();
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsInAssemblyAsync(source, "Nalix.Framework", "NALIX078");
    }

    [Fact]
    public async Task Analyze_MakeGenericTypeInCoreAssembly_ReportsNalix078()
    {
        const string source = """
namespace Nalix.Framework.Injection;

public static class GenericFactory
{
    public static object Create(System.Type itemType)
    {
        System.Type listType = typeof(System.Collections.Generic.List<>).MakeGenericType(itemType);
        return System.Activator.CreateInstance(listType)!;
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsInAssemblyAsync(source, "Nalix.Framework", "NALIX078");
    }

    [Fact]
    public async Task Analyze_MethodInfoInvokeInCoreAssembly_ReportsNalix078()
    {
        const string source = """
namespace Nalix.Framework.Injection;

using System.Reflection;

public static class Reflector
{
    public static object? Call(MethodInfo method, object? target, object?[]? args)
    {
        return method.Invoke(target, args);
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsInAssemblyAsync(source, "Nalix.Framework", "NALIX078");
    }

    [Fact]
    public async Task Analyze_TypeGetTypeStringInCoreAssembly_ReportsNalix078()
    {
        const string source = """
namespace Nalix.Framework.Injection;

public static class TypeResolver
{
    public static System.Type? Resolve(string name)
    {
        return System.Type.GetType(name);
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsInAssemblyAsync(source, "Nalix.Framework", "NALIX078");
    }

    [Fact]
    public async Task Analyze_ReflectionInNonCoreAssembly_ReportsNoDiagnostic()
    {
        const string source = """
namespace MyApp.Reflection;

public static class Scanner
{
    public static System.Type[] Scan()
    {
        return typeof(Scanner).Assembly.GetTypes();
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsInAssemblyAsync(source, "MyApp");
    }

    [Fact]
    public async Task Analyze_ReflectionInTestAssembly_ReportsNoDiagnostic()
    {
        const string source = """
namespace Nalix.Framework.Tests;

public static class Scanner
{
    public static System.Type[] Scan()
    {
        return typeof(Scanner).Assembly.GetTypes();
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsInAssemblyAsync(source, "Nalix.Tests");
    }

    [Fact]
    public async Task Analyze_TypeofExpression_ReportsNoDiagnostic()
    {
        const string source = """
namespace Nalix.Framework.Injection;

public static class TypeHelper
{
    public static System.Type GetMyType()
    {
        return typeof(string);
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsInAssemblyAsync(source, "Nalix.Framework");
    }

    [Fact]
    public async Task Analyze_ActivatorCreateInstanceGeneric_ReportsNoDiagnostic()
    {
        const string source = """
namespace Nalix.Framework.Injection;

public static class Factory
{
    public static T Create<T>() where T : new()
    {
        return System.Activator.CreateInstance<T>();
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsInAssemblyAsync(source, "Nalix.Framework");
    }

    [Fact]
    public async Task Analyze_ActivatorCreateInstanceStaticType_ReportsNoDiagnostic()
    {
        const string source = """
namespace Nalix.Framework.Injection;

public static class Factory
{
    public static object Create()
    {
        return System.Activator.CreateInstance(typeof(System.Text.StringBuilder))!;
    }
}
""";

        await AnalyzerTestHarness.AssertDiagnosticIdsInAssemblyAsync(source, "Nalix.Framework");
    }

    // Activator.CreateInstance(Type) detection is deferred.
    // See research/analyzers/08-nalix078-aot-reflection.md for details.
}
