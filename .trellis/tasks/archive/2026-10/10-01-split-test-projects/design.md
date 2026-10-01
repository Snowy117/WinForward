# Technical Design — Split monolithic test project into layered test projects

## 1. Target layout

```
tests/
├── Directory.Build.props                 # shared test-project configuration (new)
├── WinForward.TestSupport/               # non-test library: 25 fakes/builders (new)
├── WinForward.Core.Tests/                #  6 files — reuses the existing directory
├── WinForward.Configuration.Tests/       #  2
├── WinForward.Protocols.Tests/           #  8
├── WinForward.NdisApi.Tests/             #  9
├── WinForward.Windows.Tests/             #  8
├── WinForward.Runtime.Capture.Tests/     # 14
├── WinForward.Runtime.Flow.Tests/        # 15
├── WinForward.Runtime.TcpRedirect.Tests/ # 20
├── WinForward.Runtime.UdpProxy.Tests/    # 18
├── WinForward.Runtime.Socks5.Tests/      #  8
├── WinForward.Integration.Tests/         #  5
├── WinForward.Performance.Tests/         # 13
└── WinForward.Analyzers.Tests/           # unchanged, gains Directory.Build.props
```

126 test classes and 25 support files are assigned by `research/file-map.json`; the map was
machine-verified to cover the current tree exactly once with no duplicates and no omissions.

`WinForward.Core.Tests` keeps its directory and name, so its six files need no move — only the
120 files that leave it are relocated.

## 2. Project references

References are derived from each project's actual `using` directives over its own files, not
inherited from the old blanket list. This is the table the implementation must reproduce:

| Project | Project references |
|---|---|
| `WinForward.Core.Tests` | Core, Protocols, Runtime, TestSupport |
| `WinForward.Configuration.Tests` | Configuration, Runtime, TestSupport |
| `WinForward.Protocols.Tests` | Core, Protocols, Runtime, TestSupport, Windows |
| `WinForward.NdisApi.Tests` | Benchmarks, NdisApi, Runtime, TestSupport, Windows |
| `WinForward.Windows.Tests` | Windows, TestSupport |
| `WinForward.Runtime.Capture.Tests` | Configuration, Core, NdisApi, Runtime, TestSupport, Windows |
| `WinForward.Runtime.Flow.Tests` | Configuration, Core, NdisApi, Protocols, Runtime, TestSupport, Windows |
| `WinForward.Runtime.TcpRedirect.Tests` | Configuration, Core, NdisApi, Runtime, TestSupport, Windows |
| `WinForward.Runtime.UdpProxy.Tests` | Benchmarks, Configuration, Core, NdisApi, Protocols, Runtime, TestSupport |
| `WinForward.Runtime.Socks5.Tests` | Configuration, Core, Protocols, Runtime, TestSupport |
| `WinForward.Integration.Tests` | Cli, Configuration, Core, NdisApi, Protocols, Runtime, TestSupport, Windows |
| `WinForward.Performance.Tests` | Benchmarks, Configuration, Core, NdisApi, Protocols, Runtime, TestSupport |
| `WinForward.TestSupport` | Configuration, Core, NdisApi, Protocols, Runtime, Windows |

Three tests pull the benchmark host into a non-performance project (`CapturePumpReadCallTests` →
NdisApi, `UdpSessionRetentionTests` → UdpProxy), which is why `Benchmarks` appears three times
rather than once. That is a real dependency of those files, not an accident of the old project.

Because the repository's `dotnet format --severity info` gate is already green, every `using` in
these files is load-bearing; the derived set is therefore a necessary set, not an over-approximation.

## 3. TestSupport extraction

All 25 files under `TestHelpers/` move to `tests/WinForward.TestSupport/` and change namespace from
`WinForward.Core.Tests` to `WinForward.TestSupport`. They stay flat in one project: the helpers
reference each other freely within the namespace today, and splitting them further would force
cross-assembly visibility changes for no reader benefit.

`TestSupport` is a library, **not** a test project, and batch 1 measured three things that
distinction costs:

- It needs `<PackageReference Include="xunit" />` because `ConfigurationAssert.cs`,
  `TcpCoordinatorFakes.cs`, and `Socks5TestServer.cs` use `Assert`.
- It must set **`<IsTestProject>false</IsTestProject>` explicitly.** `xunit.core.props`
  unconditionally sets `IsTestProject=true` even without the test SDK, so the SDK's `VSTest` target
  launched a host against this library; the host died on a missing `xunit.abstractions` dependency
  and aborted the entire `dotnet test` run with exit 1. The override is evaluated after the package
  props and therefore wins, and `VSTestTask` is conditioned on `IsTestProject == 'true'`, so the
  target becomes a no-op.
- It needs `InternalsVisibleTo` from exactly two production projects — see §5 — **and it must grant
  friend access in the other direction.** The helpers are `internal`, so every test project that
  compiles against them needs its own entry in `TestSupport.csproj`. Batch 1 added
  `WinForward.Core.Tests`; batch 2 adds each of the eleven new projects.

Consumers need more than the `using static` rewrite. The helper files also declare top-level
`internal` types (`FakeListener`, `FakeTransport`, `ScriptedSocks5UdpServer`,
`RecordingRuntimeLogger`, `CaptureRunnerHarness`, …) that consumers previously reached through
same-namespace lookup, and each now needs a plain `using WinForward.TestSupport;`. Batch 1 measured
the true consumer-side set: **98 of 126 test files** changed, covering 85 `using static` lines in 55
files plus the plain using in 91 files, with every changed line a `using` line.

## 4. Namespace move invalidates implicit parent-namespace visibility

This is the finding that shapes the migration, and it is easy to miss by reading the diff.

C# resolves an unqualified type by walking outward through enclosing namespaces. Every one of the
151 files currently sits in `WinForward.Core.Tests`, whose parent is `WinForward.Core` — so
`Endpoint`, `FlowKey`, `AddressFamilyKind`, `NativeBufferPool` and friends resolve with **no `using`
directive at all**. Zero of the 25 helpers declare `using WinForward.Core;`, and they compile today
only because of that outward walk.

Moving a file to `WinForward.Runtime.Capture.Tests` replaces the implicitly visible ancestor with
`WinForward.Runtime.Capture`/`WinForward.Runtime`, and moves `WinForward.Core` out of reach. The
planning spike measured the effect by moving the 25 helpers to `WinForward.TestSupport` against the
same project references: **116 `CS0246` errors** — 44× `Endpoint`, 16× `FlowKey`, 14×
`AddressFamilyKind`, 12× `NativeBufferPool`, 6× each of `PolicySnapshot`, `MacAddress`,
`FlowContext` — plus cascading `CS0535`/`CS0738` interface-implementation failures. Batch 1 then
measured the consumer side for real, and the figure was an order of magnitude larger: rewriting the
126 test files raised **1237 compiler errors** (1360× `CS0246`, 768× `CS0103`, plus cascades). The
spike's number had covered only the errors *inside* the moved helpers.

Two consequences:

- **R5 still holds**, but "only the `namespace` line changes" does not. Each file's `using` block
  gains the namespaces the new ancestry no longer supplies. Usings the new ancestry makes redundant
  are left in place: `IDE0005` is **not** enforced by the repository's `dotnet format --severity info`
  gate — probe-verified on 2026-10-01, a deliberately unused `using System.Text.Json;` produced no
  diagnostic while `RCS1251` and `S2094` raised by the same probe file did — so deleting them would
  be churn beyond what the rename forces. The compiler reports every addition that is needed.
- **The reference table in §2 is unaffected.** Using directives are the *evidence* for the table,
  and they are re-derived after re-balancing, not before. Redundant usings keep naming a project that
  is genuinely referenced, so set equality still holds in both directions.

The alternative — a `GlobalUsings.cs` per test project — was rejected: it would make a file's
dependencies invisible at the file and leave existing explicit usings redundant anyway.

## 5. `InternalsVisibleTo` changes

Friend access was determined by compiler evidence during the planning spike, not by blanket grant.

**Known minimum for `WinForward.TestSupport`: `{ Runtime, Windows }`.** The spike granted access to
all six referenced production projects, then removed grants one group at a time and rebuilt:
dropping `Core`, `Protocols`, and `NdisApi` still builds clean, so the only internals the helpers
actually touch are `UdpAssociationPool` (`Runtime`) and `OwnerTable`/`OwnerTableKind` (`Windows`).
Expected edits: `src/WinForward.Runtime/WinForward.Runtime.csproj` and
`src/WinForward.Windows/Properties/AssemblyInfo.cs`.

**The twelve test projects follow the same procedure**, per project: build without friend access,
collect the `CS0122`/`CS0272` diagnostics, and grant access to the production project owning each
rejected member. Expect entries on `Runtime` for most Runtime-layer projects, on `Cli` for
`Integration.Tests` (`DurableCaptureBundle` internals), and on `NdisApi`/`Windows` where capture
seams are consumed. The existing `WinForward.Core.Tests` entry stays on any project that still has a
consumer among the six Core test files, and is dropped where it no longer does.

One entry is known in advance: `benchmarks/WinForward.Benchmarks/WinForward.Benchmarks.csproj:13`
must also list `WinForward.Performance.Tests`, because `GcSoakScenario`, `UdpSessionBudgetScenario`,
and `UdpSessionBudgetRun` are `internal` (`benchmarks/WinForward.Benchmarks/Stability/`).

A grant is kept only when removing it breaks the build. This keeps the friend lists honest without
per-member auditing, which would need re-derivation on every test addition.

## 6. `TieredCompilation` policy

Today `TieredCompilation=false` lives on the single test project with a rationale about allocation
gates. After the split, allocation measurement exists in five layers: Core, NdisApi, Flow, Socks5,
UdpProxy, plus the new Performance project. Only 15 of 126 files measure allocations, and which
files do so is not a stable property of a layer.

**Decision: set `TieredCompilation=false` once in `tests/Directory.Build.props`, for every test
project.**

- Tracking it per project would need re-derivation every time a test gains an allocation assertion,
  and a missed update produces a flaky gate whose failure mode looks like a product regression.
- The measured cost is negligible: the whole 1141-test assembly compiles and runs in 6 s with
  tiering already off, so tiering is not what makes this suite fast.
- One declaration matches how the repository already treats the setting — as a property of "tests",
  not of a particular project.

`WinForward.Analyzers.Tests` inherits the setting through the same file. Its 18 tests run in 4 s
today and are not allocation-sensitive; implementation verifies its duration does not regress, and
if it does, that project gets an explicit `TieredCompilation=true` override.

`tests/Directory.Build.props` must import the repository-root `Directory.Build.props` explicitly,
mirroring `src/Directory.Build.props:3-5`, because MSBuild stops at the nearest such file and the
test projects would otherwise lose `net10.0`, `Nullable`, `TreatWarningsAsErrors`, and
`AnalysisLevel`.

## 7. Analyzer posture

The root `Directory.Build.props:14` gates the four product analyzer packages on
`'$(IsTestProject)' != 'true'`. That condition is **always true in practice**: `Directory.Build.props`
is evaluated before NuGet's generated props, so `IsTestProject` has not yet been set by
`Microsoft.NET.Test.Sdk` when the condition runs. Confirmed by inspecting the restore assets and the
`csc` command line — the test projects load `Meziantou.Analyzer`, `SonarAnalyzer.CSharp`,
`Roslynator.Analyzers`, and `Microsoft.VisualStudio.Threading.Analyzers` exactly like production
projects.

The practical consequence for this task: **`TestSupport` inherits no analyzer governance that the
test projects do not already have.** The spike built it under all four suites with zero warnings.
This task does not change the always-true condition — fixing it would newly exempt every test
project and is well outside a behavior-zero split.

`.editorconfig` needs two adjustments, both verified during the spike:

- `[tests/**.cs]` (`:233`, `:417`) keeps covering every moved file, since all new projects stay under
  `tests/`.
- `[tests/WinForward.Core.Tests/TestHelpers/**.cs]` (`:278`, `:421`) becomes a **dead glob** when the
  directory moves, and can be deleted rather than migrated: the new path's namespace
  (`WinForward.TestSupport`) matches its project's `RootNamespace`, so `IDE0130` and
  `resharper_check_namespace_highlighting` no longer fire. The spike proved this by building with
  that glob already inert — zero warnings.
- `[tests/WinForward.Analyzers.Tests/TestHelpers/**.cs]` (`:431`) is untouched.

## 8. Mechanical migration

The move is script-assisted, never hand-retyped, matching the repository's behavior-zero refactor
discipline (`quality-guidelines.md:26`):

- `research/file-map.json` drives every `git mv`, so a file cannot land in two projects or vanish.
- Textual edits per file are confined to the `namespace` line and the `using` block, per §4. The
  six files staying in `WinForward.Core.Tests` need no namespace edit; their using blocks may still
  shrink where the format fixer finds redundancy.
- New `.csproj` files are generated from one template plus the reference table in §2.
- `WinForward.slnx` gains the twelve test projects and `WinForward.TestSupport` under `/tests/`.

Migration order keeps the tree buildable and green after every batch:

1. **Batch 1 — support extraction.** Move `TestHelpers/` to `WinForward.TestSupport`, rewrite helper
   namespaces and internal `using static` lines, add the project (xunit + six references), grant the
   `Runtime`/`Windows` friend access from §5, and make `WinForward.Core.Tests` reference it. All 126
   test files stay put and keep their namespace. Gate: 1141 tests pass.
2. **Batch 2 — eleven projects leave.** Move the 120 non-Core files per the map, create their
   projects from the reference table, rewrite namespaces, and re-balance usings under compiler
   guidance. Friend access for each new project is added from `CS0122` evidence. Gate: 1141 tests
   pass across 12 assemblies.
3. **Batch 3 — trim the leftover project.** Reduce `WinForward.Core.Tests.csproj` to
   Core + Protocols + Runtime + TestSupport, move its shared settings to `tests/Directory.Build.props`,
   and delete the two dead `.editorconfig` globs. Gate: 1141 tests, 12 assemblies.
4. **Batch 4 — gates and docs.** Run the analyzer gates and update the two spec files (R8).

Four batches rather than one keeps a known-good revision one `git checkout` away at every step.

## 9. Verification

| Gate | Command | Expected |
|---|---|---|
| Test count | `dotnet test WinForward.slnx -c Release` | 1159 passed, 0 failed (1141 + 18) |
| Per-assembly | `dotnet test tests/<project> -c Release --no-build` ×13 | each yields its expected non-zero count |
| Build | `dotnet build WinForward.slnx -c Release` | zero warnings |
| Content purity | script diffing each moved file against its `HEAD` revision with `namespace`/`using` lines stripped | byte-identical |
| Production purity | `git diff -- src/ benchmarks/` | only added `InternalsVisibleTo` lines |
| Reference necessity | `using` directives per project vs. its `ProjectReference` list | set equality |
| Friend necessity | remove each `InternalsVisibleTo` entry and rebuild | build breaks |
| Format | `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` | exit 0, empty output |
| Inspector | `jb inspectcode -f=Xml -e=HINT -o=/tmp/jb.xml WinForward.slnx` | zero `<Issue>` |
| Wall clock | `time dotnet test WinForward.slnx -c Release` | < 2 minutes |

The content-purity check is what actually protects R5: it compares each file against the revision it
came from with only the declaration lines normalized, so a silently dropped `[Fact]` or edited
assertion cannot pass review unnoticed.

## 10. Trade-offs

**Chosen: twelve assemblies, serial VSTest startup.** Each xunit assembly costs its own host startup
(~1–3 s), and VSTest has no cross-project parallel switch, so the suite's 6 s execution becomes
roughly a dozen sequential startups plus execution. AC8 bounds that at 2 minutes; the measured
baseline is 42.7 s. The alternative — fewer, larger projects — trades that time back for the
navigability the task exists to create.

**Chosen: a new assembly for helpers rather than per-project source links.** Source-linking
`TestHelpers/*.cs` into each project would avoid the extra `InternalsVisibleTo` grant, but it
recompiles the helpers twelve times, lets the copies drift, and makes "where does this fake live" a
twelve-answer question. One `TestSupport` assembly is the honest shape of a genuinely shared
dependency.

**Chosen: re-balance usings over global usings.** Global usings would shrink the diff but hide each
file's dependencies and convert existing explicit usings into `IDE0005` diagnostics. The repository
has no global-using precedent; this task does not introduce one.

## 11. Rollback

Every batch is a self-contained commit. Rolling back one batch is `git revert` of that commit; the
moves are recorded as renames, so a revert restores the original paths and namespaces with no manual
repair. The production tree changes only by added `InternalsVisibleTo` lines, so no batch can leave
the product unbuildable.
