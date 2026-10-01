# Split monolithic test project into layered test projects

## Goal

The test tree must be navigable and dependency-honest: a change to one production layer should
rebuild and re-run only that layer's tests, and a reader should be able to find a layer's tests
without scanning 126 unrelated files. Today every test lives in one project that references all
seven `src/` projects plus `benchmarks/`, so the dependency graph tells a reader nothing about why
any given test file is there.

The split is a pure structural refactor: **zero change to assertion semantics, zero change to the
production public API, identical test count before and after.**

## Confirmed facts

All verified against the working tree on 2026-10-01.

- `tests/WinForward.Core.Tests/` holds 126 `*Tests.cs` files plus 25 files under `TestHelpers/`
  (151 `.cs` total). Its `.csproj` references all 7 `src/` projects and
  `benchmarks/WinForward.Benchmarks` (`tests/WinForward.Core.Tests/WinForward.Core.Tests.csproj:20-31`).
- The suite reports **1141 passed / 0 failed** in `WinForward.Core.Tests` and **18 passed** in
  `WinForward.Analyzers.Tests`; full-solution `dotnet test -c Release` takes 42.7 s wall clock, of
  which the `Core.Tests` assembly itself runs in 6 s.
- Every test file declares `namespace WinForward.Core.Tests` (126/126); helpers do too, and are
  reached with `using static WinForward.Core.Tests.<Helper>`.
- `InternalsVisibleTo("WinForward.Core.Tests")` is granted by `WinForward.Core.csproj:8`,
  `WinForward.Cli.csproj:38`, `WinForward.Protocols.csproj:10`, `WinForward.Runtime.csproj:7`,
  `src/WinForward.Windows/Properties/AssemblyInfo.cs:3`, `src/WinForward.NdisApi/NdisApiAbi.cs:6`,
  and `benchmarks/WinForward.Benchmarks/WinForward.Benchmarks.csproj:13`.
- `TieredCompilation=false` is set on the test project alone, with a recorded rationale about
  allocation-gate soundness (`tests/WinForward.Core.Tests/WinForward.Core.Tests.csproj:5-19`).
- Allocation measurement (`GC.GetAllocatedBytesForCurrentThread`) is used by 15 files spread over
  five different target layers, not only by files named `*GateTests`.
- Every file resolves production types through its **enclosing** namespace: not one of the 25
  helpers declares `using WinForward.Core;`, because `WinForward.Core.Tests` sits inside
  `WinForward.Core`. Changing a file's namespace therefore re-balances its `using` block as well
  (measured in design §4: 116 `CS0246` errors from moving the helpers alone).
- `GcSoakScenario`, `UdpSessionBudgetScenario`, and `UdpSessionBudgetRun` are `internal` in
  `benchmarks/WinForward.Benchmarks/Stability/`; `TcpRedirectDataPathBenchmarks` is `public`.
- No `TestHelpers/` file references `WinForward.Benchmarks`, so the shared support library needs no
  benchmark dependency.
- `tests/Directory.Build.props` does not exist; `src/Directory.Build.props` exists and imports the
  repo-root props explicitly.
- No `[Collection]`, `[assembly:]` attribute, or global-using file governs the test project.
- `.trellis/spec/backend/directory-structure.md` documents the current single-test-project layout
  and the `TestHelpers/` conventions; `.trellis/spec/backend/quality-guidelines.md:26` records the
  test-count baseline discipline for behavior-zero refactors.

## Requirements

**R1 — Split by production layer, sub-split `Runtime` by its documented sub-namespaces.**
Twelve xunit projects replace the single one: `WinForward.Core.Tests`,
`WinForward.Configuration.Tests`, `WinForward.Protocols.Tests`, `WinForward.NdisApi.Tests`,
`WinForward.Windows.Tests`, `WinForward.Runtime.Capture.Tests`, `WinForward.Runtime.Flow.Tests`,
`WinForward.Runtime.TcpRedirect.Tests`, `WinForward.Runtime.UdpProxy.Tests`,
`WinForward.Runtime.Socks5.Tests`, `WinForward.Integration.Tests`, `WinForward.Performance.Tests`.
`WinForward.Runtime.Flow.Tests` carries the `WinForward.Runtime` root-namespace surface
(dispatcher, flow attribution, sweeper, health monitor, quiescence, counters, logging).

**R2 — Extract the 25 shared helpers into a non-test library.**
`tests/WinForward.TestSupport/` holds every file currently in `TestHelpers/` under the namespace
`WinForward.TestSupport`; test projects consume it via `ProjectReference`.

**R3 — Each test project references only the production projects it actually compiles against.**
No project keeps the blanket "reference everything" posture of today's `.csproj`.

**R4 — Namespaces follow the project.**
Every test file's namespace becomes its new project's name, so a stack trace or `--filter` names the
project a reader should open.

**R5 — Zero assertion change.**
For every moved file, the content must be byte-identical to its pre-move content except for the
`namespace` line and `using` lines that the rename forces.

**R6 — Allocation-gate soundness must survive the split.**
The `TieredCompilation=false` guarantee must still cover every test that measures allocations.

**R7 — `InternalsVisibleTo` must be re-established for every consumer.**
Friend access currently granted to `WinForward.Core.Tests` must be granted to whichever new project
now consumes those internals, and to `WinForward.TestSupport` for the helpers it compiles.

**R8 — Project documentation must match the new tree.**
`.trellis/spec/backend/directory-structure.md` (layout, `TestHelpers/` conventions) and the
test-baseline note in `.trellis/spec/backend/quality-guidelines.md` are updated in the same change.

## Acceptance criteria

- **AC1** `dotnet test WinForward.slnx -c Release` reports exactly **1159** passed, 0 failed,
  0 skipped (1141 split across the twelve new projects + 18 from `WinForward.Analyzers.Tests`).
- **AC2** Every one of the 12 new test projects is discovered by the solution and contributes a
  non-zero test count.
- **AC3** `dotnet build WinForward.slnx -c Release` completes with zero warnings.
- **AC4** For every moved test file, a diff against the pre-move revision shows changes confined to
  the `namespace` declaration and `using` directives.
- **AC5** No production behavior changes: the only edits under `src/` and `benchmarks/` are added
  `InternalsVisibleTo` entries.
- **AC6** No new test project carries a `ProjectReference` it does not compile against: for every
  project, the set of `WinForward.*` namespaces appearing in its own `using` directives equals the
  set of production projects it references (plus `TestSupport`). Note that this is a set-equality
  bar, not a remove-one-and-the-build-breaks bar — `ProjectReference` is transitive, so a reference
  can be redundant for the build while still being a false claim about the project's dependencies.
- **AC7** `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` exits 0
  with empty output, and `jb inspectcode -f=Xml -e=HINT WinForward.slnx` reports zero `<Issue>`.
- **AC8** The full-solution `dotnet test` wall clock stays under **2 minutes** on the development
  host (baseline: 42.7 s including build), so the extra per-assembly host startups stay bounded.

## Out of scope

- Splitting `tests/WinForward.Analyzers.Tests` — it is already scoped to one project and shares no
  helpers with the suite being split.
- Any behavior change, new test, deleted test, or assertion edit.
- Changing test framework or runner, or migrating to `Microsoft.Testing.Platform`.
- Introducing solution-level parallel test execution (see Deferred).
- Reorganizing `benchmarks/`.

## Deferred

- **Parallel test execution.** `dotnet test` on this solution drives VSTest, which has no
  cross-project parallel switch; the twelve assemblies therefore start serially, and that startup
  cost is the split's main price. If AC8 is at risk, the follow-up is build-level orchestration that
  runs the test projects concurrently, or a `Microsoft.Testing.Platform` opt-in. Neither is part of
  this task.
- **`WinForward.Runtime.Flow.Tests` naming.** "Flow" is the best available name for the
  `WinForward.Runtime` root surface (dispatcher + attribution + sweeper + diagnostics); if the
  project later outgrows that description the rename is trivial.
- **Per-project `TieredCompilation` minimization.** See design.md — the task deliberately keeps the
  setting uniform across test projects rather than tracking which files measure allocations.
