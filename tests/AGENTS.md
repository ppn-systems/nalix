# Nalix Tests — Agent Instructions

Apply the rules in `../AGENTS.md`, `../CLAUDE.md` and this file. This file is the single source of truth for how
tests are organized, named and written. If a rule here conflicts with the style of an existing test, the rule wins.

## Projects

One test project per source project. `Nalix.Integration.Tests` is the only cross-cutting project.

| Test project | Covers |
| :--- | :--- |
| `Nalix.Abstractions.Tests` | `src/Nalix.Abstractions` |
| `Nalix.Environment.Tests` | `src/Nalix.Environment` |
| `Nalix.Codec.Tests` | `src/Nalix.Codec` |
| `Nalix.Framework.Tests` | `src/Nalix.Framework` |
| `Nalix.Runtime.Tests` | `src/Nalix.Runtime` |
| `Nalix.Network.Tests` | `src/Nalix.Network` |
| `Nalix.SDK.Tests` | `src/Nalix.SDK` |
| `Nalix.Hosting.Tests` | `src/Nalix.Hosting` |
| `Nalix.Analyzers.Tests` | `analyzers/Nalix.Analyzers`, `.CodeFixes`, `.Generators` (one top-level folder each) |
| `Nalix.Integration.Tests` | End-to-end scenarios that span several source projects (real sockets, full host) |

A test belongs in the project of the type it exercises. Only put it in `Integration.Tests` when it needs more than
one source project wired together.

## Layout

- The folder tree of a test project **mirrors the folder tree of its source project**.
  `src/Nalix.Network/Internal/Security/SlidingWindow.cs` is tested by
  `tests/Nalix.Network.Tests/Internal/Security/SlidingWindowTests.cs`.
- No test files at the project root except `TestAssemblySetup.cs` and `TestCollections.cs` (both optional), and
  tests whose subject sits directly at the root of its source project (for example `ReportRegistry.cs`).
- Shared helpers, fakes and builders live in `TestSupport/` inside the project. Anything used by several test
  projects goes in `tests/Shared/` and is linked in through `tests/Directory.Build.props`.
- One top-level test class per file. The file name equals the class name.
- Split a class into `<Subject>Tests.cs` + `<Subject>Tests.<Aspect>.cs` partials only when the file would exceed
  800 lines.
- Never commit run output (`TestResults/`, `*.trx`, `test_results.txt`, logs, coverage files).

## Namespaces

- File-scoped namespace, equal to `<TestProjectName>` plus the folder path with dots:
  `tests/Nalix.Network.Tests/Internal/Security/SlidingWindowTests.cs` →
  `namespace Nalix.Network.Tests.Internal.Security;`.
- Files directly in `TestSupport/` use `<TestProjectName>.TestSupport`.
- Every file has a namespace. No global-namespace types.

## Classes

- `public sealed class <Subject>Tests`. `Subject` is the type under test (`SlidingWindow` → `SlidingWindowTests`).
- When one type needs several focused suites, use `<Subject><Scenario>Tests` with a descriptive PascalCase scenario
  (`SocketConnectionFramingTests`, `SocketConnectionFuzzTests`). Common scenarios: `Adversarial`, `Fuzz`,
  `Concurrency`, `Lifecycle`, `Regression`. Keep every scenario suite in the subject's folder.
- Helper types (fakes, protocols, packets) are `private`/`internal` nested types or live in `TestSupport/`.
  A helper is never named `*Tests`.
- Do not use placeholder names (`UnitTest1`, `Class1`, `Test1`).

## Methods

Use **`Method_State_Expectation`**: exactly three PascalCase segments separated by single underscores.

```csharp
[Fact]
public void TryAdd_WhenKeyAlreadyPresent_ReturnsFalse()

[Fact]
public async Task ConnectAsync_WhenServerRefusesConnection_ThrowsSocketException()

[Theory]
[InlineData(0)]
[InlineData(-1)]
public void Ctor_WhenCapacityIsNotPositive_ThrowsArgumentOutOfRangeException(int capacity)
```

- **Method** — the member under test: a method name, a property name, or `Ctor`. For feature-level scenarios that
  cross members, use the feature name (`Handshake`, `SessionResume`).
- **State** — the precondition or input, starting with `When…`, `With…`, `After…`, `Given…` or a plain noun phrase.
  It must not repeat the expectation.
- **Expectation** — the observable outcome: `ReturnsFalse`, `ThrowsArgumentNullException`, `RaisesClosedEvent`,
  `LeavesBufferUntouched`.
- Do not prefix with `Test`/`Should`, do not add an `Async` suffix, and do not put underscores inside a segment.
- Names describe behavior, not implementation. Rename a test when its behavior changes.

## Writing tests

- One behavior per test. Arrange / Act / Assert separated by blank lines; no comments that restate the code.
- Use the assertion library already used by the file (`Xunit.Assert` or FluentAssertions); never mix both in
  one class. Do not add NuGet packages.
- Use `[Theory]` with `TheoryData<>` (or `[InlineData]` for trivial cases) for repeated inputs.
- Tests are deterministic and order independent:
  - No fixed ports — bind to port 0 or use the shared free-port helper.
  - No `Thread.Sleep`/fixed `Task.Delay` to "wait for" something. Await a signal (`TaskCompletionSource`,
    `ManualResetEventSlim`) with a bounded timeout, or poll a condition with a deadline.
  - Temp files and directories are unique per test and removed in `Dispose`/`DisposeAsync`.
  - Dispose everything you create (leases, sessions, hosts, pools) and assert on leaks where it matters.
- Never write to the console from a test (`Console.WriteLine`); use `ITestOutputHelper` if output is needed.
- Hot-path code (`Span<T>`, pooled buffers) is tested with pooled buffers returned in `finally`/`using`.
- XML documentation is not required on test types. The two-line license header used in `src/` is required.

### Process-wide state

`ConfigurationManager`, `InstanceManager`, static pools, clocks and `TimingWheel` are process-wide. A test that
mutates or depends on them must:

1. Join a collection from the project's `TestCollections.cs` (`[Collection(TestCollections.Clock)]`). A collection
   is declared there as a `const string` plus a `[CollectionDefinition]` (`DisableParallelization = true` when
   it needs exclusive use of the process). Never use string literals in `[Collection]`.
2. Restore what it changed in a `finally`/`Dispose`, even when the assertion fails.

A test that passes alone but fails in the full run has leaked state; fix the leak, do not retry or skip it.

## Debug and Release

The suite must be green in **both** `Debug` and `Release`, and CI runs both.

- `InternalsVisibleTo` for every test assembly is declared once in `src/Directory.Build.props` and is active in all
  configurations. Never wrap tests in `#if DEBUG` just to reach `internal` members.
- `#if DEBUG` is allowed only for behavior that the source itself compiles into Debug builds only
  (diagnostic poisoning, Debug-only validation, Debug-only members). Wrap the single test, put a one-line comment
  naming the source member, and, when Release has the opposite behavior, add a `#else` branch that asserts it.
- Never skip a test with `Skip =` to make a build green. A genuinely manual or very slow test uses
  `[Fact(Skip = "<reason and how to run it>")]` and an issue link.

## Adding coverage

- Every bug fix ships with a regression test that fails without the fix.
- Cover, for each public or security-relevant behavior: the happy path, boundary values, invalid and hostile input,
  cancellation, and disposal. Hot-path and crypto code additionally need known-answer or round-trip vectors.
- Prefer testing through the public API; reach for `internal` members only to observe state the public API hides.

## Verifying your change

Run both configurations for the project you touched, then the whole suite before finishing:

```powershell
dotnet test tests/Nalix.<Project>.Tests -c Debug
dotnet test tests/Nalix.<Project>.Tests -c Release
dotnet test tests/Nalix.Tests.sln -c Release
```
