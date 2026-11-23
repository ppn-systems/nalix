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
| `Nalix.Integration.Tests` | End-to-end scenarios across several source projects (`EndToEnd/`: real Hosting server + Nalix.SDK client over TCP, UDP and WebSocket) |

Each project mirrors the folder structure of the source project it tests, so
`src/Nalix.Network/Internal/Security/SlidingWindow.cs` is covered by
`tests/Nalix.Network.Tests/Internal/Security/SlidingWindowTests.cs`.
`Shared/` holds source files linked into several test projects (not a test project itself).

## Running

CI runs the suite in Release; Debug-only behavior (`#if DEBUG`) must still pass locally in Debug.

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

CI uploads coverage to Codecov from the Linux test shards.

## Continuous integration

`CI` (`.github/workflows/ci.yml`) splits the work into shards that run in parallel, one runner each. The shards
are defined in `.github/scripts/ci_plan.py`:

| Shard | OS | What it does |
| :--- | :--- | :--- |
| `base`, `network`, `hosting`, `sdk`, `analyzers` | Linux | Groups of the test projects in `tests/` (with coverage, except `analyzers`) |
| `extensions` | Linux | The test projects in `extensions/tests/` |
| `windows-client` | Windows | Client-side projects only: SDK, Codec, Environment, Abstractions |
| `build` | Linux | `src/Nalix.sln` and `extensions/*.sln` for every target framework, analyzers on |
| `lint` | Linux | `actionlint` over the workflows |

A test shard builds its test projects and what they reference for `net10.0` only, without analyzers; the `build`
shard is where a `net11.0` break, an analyzer error or a public API change is caught.

Each shard has a key: a hash of every tracked file that can change its result (its projects, everything they
reference, and the shared build files). A shard that passes saves a marker under that key, and a later run skips
a shard whose marker exists, when it was saved from `master` or from the same pull request. A change therefore
only runs the shards it can affect, and a documentation-only change runs none. "Run workflow" on the Actions
page runs every shard regardless. A new test project must be added to a shard in `ci_plan.py`; the plan fails
until it is.

`CI OK` is the single required status check. Test builds are never strong-name signed (a signed assembly cannot
grant `InternalsVisibleTo` by simple name), so CI does not decode the signing key for test runs.

## Extension tests

Tests for the extensions under `extensions/` live in `extensions/tests/`, one project per extension
(`Nalix.Observability.Tests`, `Nalix.Tunneling.Tests`, `Nalix.Traversal.Tests`, `Nalix.SDK.Traversal.Tests`). Each
project is listed in its extension's solution, so `dotnet test extensions/Nalix.<Extension>.sln -c Release` runs it.
They follow the same conventions as the core suite, and `extensions/tests/Directory.Build.props` supplies the same
test packages. Internal members are reached through `InternalsVisibleTo` in the extension project.

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
