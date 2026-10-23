# Nalix Tests

Automated tests for the Nalix networking framework (.NET 10, xUnit v3). The conventions every test must follow
are in [AGENTS.md](AGENTS.md); this guide covers how to run and extend the suite.

## Layout

| Project | Covers |
| :--- | :--- |
| `Nalix.Abstractions.Tests` | Contracts, primitives, attribute keys |
| `Nalix.Environment.Tests` | Configuration, memory leases, fragments, sequencing, random |
| `Nalix.Codec.Tests` | Serialization, packets, LZ4, transforms |
| `Nalix.Framework.Tests` | Cryptography, pools, tasks, identifiers, injection |
| `Nalix.Runtime.Tests` | Dispatch, middleware, handler compilation, throttling |
| `Nalix.Network.Tests` | Listeners, connections, guards, transports, timing wheel |
| `Nalix.SDK.Tests` | Client sessions, request/response, reconnect, resume |
| `Nalix.Hosting.Tests` | Application builder, startup sequence, handler discovery |
| `Nalix.Analyzers.Tests` | Roslyn analyzers, code fixes and source generators |
| `Nalix.Integration.Tests` | End-to-end scenarios across several source projects |

Each project mirrors the folder structure of the source project it tests, so
`src/Nalix.Network/Internal/Security/SlidingWindow.cs` is covered by
`tests/Nalix.Network.Tests/Internal/Security/SlidingWindowTests.cs`.
`Shared/` holds source files linked into several test projects (not a test project itself).

## Running

The suite must pass in both configurations; CI runs both.

```powershell
# Everything
dotnet test tests/Nalix.Tests.sln -c Release
dotnet test tests/Nalix.Tests.sln -c Debug

# One project
dotnet test tests/Nalix.Network.Tests -c Release

# One class or one test
dotnet test tests/Nalix.Network.Tests -c Release --filter "FullyQualifiedName~SlidingWindowTests"
dotnet test tests/Nalix.Network.Tests -c Release --filter "FullyQualifiedName~SlidingWindowTests.TryAdd_WhenKeyAlreadyPresent_ReturnsFalse"
```

Build output goes to `build/bin/<Configuration>/<Project>/`. Test run output (`TestResults/`) is git-ignored.

### Coverage

```powershell
dotnet test tests/Nalix.Tests.sln -c Release --collect:"XPlat Code Coverage" --results-directory artifacts/coverage
```

CI publishes a coverage summary on the Linux/Release job.

## Continuous integration

`CI` (`.github/workflows/ci.yml`) detects which tier a change touches and runs only the affected jobs:

| Job | Configuration | OS |
| :--- | :--- | :--- |
| Core | Release | Linux, Windows |
| Core | Debug | Linux |
| Extensions | Release | Linux |

`CI OK` is the single required status check. Test builds are never strong-name signed (a signed assembly cannot
grant `InternalsVisibleTo` by simple name), so CI does not decode the signing key for test runs.

## Writing a test

1. Find (or create) the test project and the folder that mirrors the source file.
2. Create `<Subject>Tests.cs` with `namespace Nalix.<Project>.Tests.<Folder>;` and a `public sealed class`.
3. Name methods `Method_State_Expectation`.
4. If the test touches process-wide state (configuration, instance manager, pools, clocks), join a collection from
   the project's `TestCollections.cs` and restore the state afterwards.
5. Run the project in Debug and Release.

## Troubleshooting

- **A test passes alone but fails in the full run** — it depends on state another test leaks or changes. See
  "Process-wide state" in [AGENTS.md](AGENTS.md).
- **`CS0122` on an internal member in Release** — the member is compiled only in Debug builds. Wrap that single test
  in `#if DEBUG` with a comment naming the source member (see "Debug and Release" in [AGENTS.md](AGENTS.md)).
- **`CS0122` on every internal member, or `CS1726`** — the build is strong-name signed (`SignAssembly=true`), which
  turns off the `InternalsVisibleTo` grant. Build tests with `-p:SignAssembly=false`.
