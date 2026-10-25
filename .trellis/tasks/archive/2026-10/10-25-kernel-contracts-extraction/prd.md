# Batch 3: extract WinForward.Kernel.Contracts

Parent: `.trellis/tasks/10-25-kernel-backend-integration`

## Goal

Move the reusable Kernel C# contract surface (`Device/`, `Policy/`, `Forwarded/`) into a root
project, `src/WinForward.Kernel.Contracts`, and leave
`kernel/src/userland/WinForward.Userland` as a thin compatibility project for the native tests,
controller and `wfprobe`. The ABI/byte layout and compiler behaviour stay unchanged. The Kernel
`Configuration/`, `Control/` and `Relay/` scaffolding stays in the native tree for this batch; it is
retired when the backend/forwarded E2E tools move.

## Requirements

- **C3-R1 New project.** Create `src/WinForward.Kernel.Contracts/WinForward.Kernel.Contracts.csproj`
  (root `Directory.Build.props`, `net10.0`), move `Device/`, `Policy/` and `Forwarded/` from
  `kernel/src/userland/WinForward.Userland/` without changing their byte-layout constants or
  compiler semantics. Add it to `WinForward.slnx`. Namespaces may stay `WinForward.Userland.*` for
  this extraction; record the project/namespace mismatch and defer the rename.
- **C3-R2 Native compatibility.** Keep `kernel/src/userland/WinForward.Userland` buildable as a
  thin project reference to the new project (it may keep only `Configuration/`, `Control/`,
  `Relay/`). Grant the new project `InternalsVisibleTo` for `WinForward.Userland.Tests` (and any
  other assembly the Kernel tests actually need). Verify `WinForward.Userland.slnx`,
  `WinForward.Userland.Tests`, `WinForward.Controller` and `tools/wfprobe` still build; if
  project-reference transitivity does not expose the new assembly, add direct references rather
  than duplicating types.
- **C3-R3 ABI and tests unchanged.** No changes to the host/forwarded blob layouts, IOCTL structs,
  process-path normalization, compiler outputs or golden bytes. The existing Kernel ABI/layout/
  compiler tests must pass; `wf_check` must stay green.
- **C3-R4 Native gates.** Update `kernel/tools/userland/verify-gates.sh` mutation paths for the
  moved `ProbeIoctl.cs` and `RuleBlobCompiler.cs`; keep the `Configuration/`/`Relay/` paths until
  that scaffolding is retired. Do not weaken the native gate or the Windows script's build surface.
- **C3-R5 Root quality.** The new project must pass the root analyzers, `TreatWarningsAsErrors`,
  `dotnet build`, `dotnet test`, `dotnet format` and the Kernel `check.sh --full`. Fix analyzer
  findings when they improve the code; otherwise suppress narrowly with a concrete reason. Do not
  disable analyzers globally or change ABI behaviour to silence a warning.
- **C3-R6 Scope.** No `BackendDriver`/backend/product data-path changes, no `WinForward.Runtime` or
  `WinForward.NdisApi` changes, no retirement of the Kernel scaffolding, no unrelated dirty files.

## Acceptance Criteria

- [x] C3-AC1: `WinForward.slnx` builds with the new project; root `dotnet build`, `dotnet test` and
      `dotnet format` pass.
- [x] C3-AC2: the Kernel native solution and `wf_check` build/run with the shim; the Kernel
      ABI/layout/compiler tests pass (same count as before, no skipped suite).
- [x] C3-AC3: no byte-layout/golden/compiler-output changes; the moved code's diff is limited to
      namespaces/paths/analyzer suppressions and the project plumbing.
- [x] C3-AC4: `verify-gates.sh` points at the new compiler/IOCTL paths for the moved files, and the
      old paths are not silently scanned as zero files.
- [x] C3-AC5: no product data path, Runtime/NdisApi, BackendDriver or unrelated dirty-file changes.
- [x] C3-AC6: `kernel/MIGRATION.md` and the parent `research/contracts-project.md` record the
      extraction, shim and deferred scaffolding/namespace rename. The known `jb inspectcode`
      environment gap is recorded; primary gates must be green.

## Out of Scope

- Retiring `kernel/src/userland/.../Configuration`, `Control`, `Relay` or the Controller/wfprobe.
- Renaming `WinForward.Userland.*` namespaces or porting the custom test runner to xUnit.
- The backend seam, Kernel host TCP/UDP, forwarded, ARM64, driver packaging.
