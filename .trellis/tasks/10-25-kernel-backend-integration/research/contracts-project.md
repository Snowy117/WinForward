# Kernel C# contracts extraction: dependency surface

Date: 2026-10-25
Context: Batch 3 planning, after the BackendDriver configuration batch.

## What the Kernel userland contains

`kernel/src/userland/WinForward.Userland` has four areas:

| Area | Use after the merge |
|---|---|
| `Device/` | ABI mirror, `WfpDeviceClient`, IOCTL structs. Keep; the Kernel backend needs it. |
| `Policy/` | Host blob model/compiler, process-path resolver, layout types. Keep as the ABI encoder. |
| `Forwarded/` | Forwarded blob model/compiler and adapter resolver. Keep for the later forwarded milestone. |
| `Configuration/`, `Control/`, `Relay/` | Temporary JSON schema, controller lifecycle and relay scaffolding. Retire in favour of `WinForward.Configuration` / `WinForward.Runtime`; keep only while the native tools still reference them. |

## Native-tool dependencies

`kernel/tools/windows/gate.ps1` builds `kernel/src/userland/WinForward.Userland.slnx` and runs
`WinForward.Userland.Tests`. The test project references both `WinForward.Userland` and
`WinForward.Controller`. The Windows `userland-e2e.ps1` and `forwarded-udp-reject-e2e.ps1` launch
`WinForward.Controller.exe`. `kernel/tools/userland/verify-gates.sh` mutates source files by path,
including `Device/ProbeIoctl.cs` and `Policy/RuleBlobCompiler.cs`.

Consequences:

- Moving `Device/`, `Policy/` and `Forwarded/` out of `WinForward.Userland` breaks all of those tools
  unless the old project keeps working.
- The clean transition is to move those three areas into `src/WinForward.Kernel.Contracts`, make the
  old `kernel/src/userland/WinForward.Userland` project a thin project reference to it, and leave
  the Kernel `Configuration/`, `Control/`, `Relay/` scaffolding in place until the backend/forwarded
  E2E tools no longer need it. The old project is then a compatibility shim, not a second userland.
- `WinForward.Userland.Tests` and `WinForward.Controller` can keep referencing the old project:
  project-reference transitivity exposes the new assembly, measured on the actual builds. The new
  project grants `InternalsVisibleTo` to `WinForward.Userland.Tests` (ABI/layout assertions) and to
  `WinForward.Userland` (the shim's `Configuration/` reads the forwarded blob sentinel); the
  Controller needs no grant.
- `verify-gates.sh` mutates `ProbeIoctl.cs` and `RuleBlobCompiler.cs` at
  `../src/WinForward.Kernel.Contracts/{Device,Policy}/...`; the `Configuration/` and `Relay/`
  mutation paths stay until that scaffolding is retired.
- The new project uses the root `Directory.Build.props` analyzers; its old namespaces can stay
  `WinForward.Userland.*` for the first extraction to keep the tests/controller source stable. A
  namespace rename is a separate, mechanical batch.

## Proposed order

1. Create the new project with `Device/`, `Policy/`, `Forwarded/`, a root-solution entry, and the
   ABI/compiler tests either through the existing native test runner (transitive) or a small xUnit
   suite.
2. Update the old project to a project reference, grant IVT, and update `verify-gates.sh` paths.
3. Run the Kernel native gate (`check.sh --full`, `wf_check`) and the root .NET gates; confirm the
   Controller/test runner still build.
4. Only then retire `Configuration/`, `Control/`, `Relay/` and port their still-useful tests; that
   is a separate batch with the backend seam.

This keeps the userland dedupe incremental and leaves every intermediate state buildable.

## Measured result

The extraction landed as planned. `src/WinForward.Kernel.Contracts` holds `Device/`, `Policy/` and
`Forwarded/` under the unchanged `WinForward.Userland.*` namespaces;
`kernel/src/userland/WinForward.Userland` is a `ProjectReference` plus `Configuration/`, `Control/`
and `Relay/`. Transitivity held on the real build: neither `WinForward.Userland.Tests` nor
`WinForward.Controller` needed a direct reference to the new project. `WinForward.Userland.slnx`
builds and `kernel/tools/check.sh --full` (the `wf_check` runner included) passes, so the move left
the ABI bytes, the layout assertions and the compiler output untouched. The namespace rename and the
`Configuration/`/`Control/`/`Relay/` retirement stay open for the backend batch.

Three follow-ups were measured after the first green build:

- **wfprobe analyzer opt-out.** `tools/wfprobe/wfprobe.csproj` targets `net10.0` like the rest of the
  userland. The root `Directory.Build.props` is found by walking up from the project directory, so
  the tool inherits the four analyzer packages even though it is not in `WinForward.slnx`. With the
  opt-out removed, a Release build reports 500 unique errors over 20 files, led by MA0004 249,
  S6966 36, MA0051 34, MA0048 30, VSTHRD103 27 and S1199 21; MSBuild repeats every diagnostic in
  the failure summary, so the raw log carries 1000 error lines. A Meziantou+Sonar-only build emits
  446 of them, which rules out a per-build cap and matches the full total once VSTHRD and Roslynator
  are added back. The csproj therefore removes Meziantou, VSTHRD, Roslynator and Sonar at the
  project level; the real fix (adopting the root analyzer set for the tool) is a separate mechanical
  batch, and the comment carries the counts plus that deletion condition.
- **Native solution Release mapping.** `WinForward.Userland.slnx` originally listed only the three
  native projects. A `ProjectReference` to a project outside the solution has its configuration
  unset (`ShouldUnsetParentConfigurationAndPlatform`), so `dotnet build
  kernel/src/userland/WinForward.Userland.slnx -c Release` compiled the contracts project into
  `src/WinForward.Kernel.Contracts/bin/Debug/net10.0/`. Listing the new project in the solution
  (`../../../src/WinForward.Kernel.Contracts/WinForward.Kernel.Contracts.csproj`) maps it to Release;
  the Release build now writes `bin/Release/net10.0/`, verified on the real build.
- **Windows layout.** The cross-tree references mean the Windows deployment can no longer be a
  `kernel/`-only subtree. `gate.ps1` still takes `-Root` = the `kernel/` root and still builds
  `WinForward.Userland.slnx`, but the solution reaches `<repo>/src/WinForward.Kernel.Contracts`
  through `../../../src/...`, and the contracts project imports the root `Directory.Build.props`,
  `Directory.Packages.props`, `src/Directory.Build.props` and `analyzers/WinForward.Analyzers`.
  The machine must receive the complete repository layout; `kernel/MIGRATION.md` and
  `kernel/tools/windows/README.md` now state that requirement and no longer claim the gate's build
  surface is self-contained.
