# Batch 3 design: WinForward.Kernel.Contracts extraction

## Project layout

```text
src/WinForward.Kernel.Contracts/
  WinForward.Kernel.Contracts.csproj
  Device/        (moved from kernel/src/userland/WinForward.Userland/Device)
  Policy/        (host blob model/compiler, process-path resolver, layout types)
  Forwarded/     (forwarded blob model/compiler, adapter resolver)

kernel/src/userland/WinForward.Userland/
  WinForward.Userland.csproj   (thin compatibility project: ProjectReference to the root project)
  Configuration/               (still here for the native tools)
  Control/                     (still here)
  Relay/                       (still here)
```

The new project:

- Uses the root `Directory.Build.props` (net10.0, analyzers, warnings-as-errors).
- `AssemblyName = WinForward.Kernel.Contracts`, `RootNamespace = WinForward.Userland` for the
  transitional period, with an XML comment explaining the mismatch and the planned rename.
- No package references beyond the BCL; `AllowUnsafeBlocks` comes from the root props.
- `InternalsVisibleTo` for `WinForward.Userland.Tests` (the Kernel test runner uses internals).
  Add `WinForward.Controller` or `wfprobe` only if the build proves they need internals; do not
  widen access speculatively.
- Added to `WinForward.slnx` under `/src/`.

## Move and shim

Move `Device/`, `Policy/`, `Forwarded/` with `git mv` to preserve history. Do not edit the byte
layouts, constants, golden tests or compiler logic. Path/namespace comments that point at the old
Kernel `.trellis` locations may be updated to the new spec paths, but no behavior changes.

The old `WinForward.Userland.csproj` becomes a project reference to the new project. Keep its
`AssemblyName`/`RootNamespace` and any IVT lines that still matter; remove compile items that moved
(the default globs will no longer find them once the directories are gone). Verify the Kernel native
solution, the test runner, `WinForward.Controller` and `wfprobe`:

- First try project-reference transitivity (old project → new project; consumers → old project).
- If the compiler cannot see the new types, add a direct `ProjectReference` to the new project in
  the test/controller/wfprobe projects. Do not copy types or add a facade with duplicated logic.

The Kernel `Configuration/`, `Control/` and `Relay/` code keeps compiling against the moved types
through the shim. Keep it in this batch; retiring it is a later batch.

## Native gates

- `kernel/tools/userland/verify-gates.sh`: update the `ProbeIoctl.cs` and `RuleBlobCompiler.cs`
  mutation paths to `../src/WinForward.Kernel.Contracts/...` (relative to the `kernel/` root used by
  the script). Keep the `Configuration/` and `Relay/` paths until those files move. Verify the
  script's backup/restore uses the same path and that a missing file fails loudly rather than
  silently scanning zero files.
- `kernel/tools/windows/gate.ps1` builds `WinForward.Userland.slnx`; the shim must keep that build
  working. The Controller output path and the `wfprobe` references stay as they are.
- `kernel/tools/check.sh --full` → `wf_check` must still run the Kernel test runner and pass the
  same test count. The `check.sh --fast` formatting/cppcheck scopes are unchanged.

## Root analyzers

The moved code was written under the Kernel's conventions. The root build applies Meziantou,
Roslynator, Sonar, VSTHRD and AOT/trim analyzers with `TreatWarningsAsErrors`. Handle findings in
this order:

1. Fix genuine correctness/quality issues (null handling, disposal, culture, allocation where it
   matters).
2. Suppress narrowly with a concrete reason where the code is an ABI/layout primitive or the
   analyzer suggestion would change the validated byte behavior (for example `Unsafe.SizeOf`,
   fixed-size buffers, explicit `Pack`/`StructLayout`).
3. Do not disable an analyzer globally; no `NoWarn` blanket for the project unless the exact
   analyzer is inapplicable and the reason is documented in the csproj/`.editorconfig` scoped to
   the project.

Do not change the byte layout, constants, golden bytes or compiler output to satisfy an analyzer.

## Root solution and tests

- Add the new project to `WinForward.slnx` under `/src/`. Do not add the Kernel test runner to the
  root solution in this batch; it remains a native-gate tool.
- The root build/test does not need a new test project; the existing Kernel `wf_check` is the
  regression oracle for the moved code. A later batch may port the tests to xUnit.
- The new project must not reference `WinForward.Runtime`, `WinForward.NdisApi` or any product
  data-path project; it is a contract/compiler project.

## Verification

```bash
dotnet build WinForward.slnx -c Release
dotnet test WinForward.slnx -c Release
dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore
nix develop ./kernel --command bash -lc 'kernel/tools/check.sh --full'
dotnet build kernel/src/userland/WinForward.Userland.slnx -c Release
# Windows VM (when available): kernel/tools/windows/gate.ps1
```

The `jb inspectcode` global tool is currently broken (missing
`System.Composition.AttributedModel 10.0.0.12`); record it as an environment gap. Do not treat a
tool-loading exception as a code finding. The primary analyzers (`dotnet build` with
warnings-as-errors), `dotnet format`, the Kernel gate and the native `wf_check` are the acceptance
evidence for this batch.

## Risks and rollback

- The shim/transitive-reference behavior is the main risk; verify the actual build, not the theory.
  If transitivity fails, add direct references instead of copying code.
- The root analyzers may surface a large batch of findings in ABI code; narrowly scoped suppressions
  with reasons are acceptable, behavior changes are not.
- The `verify-gates.sh` path update can silently scan zero files if wrong; assert the file exists
  and is mutated (the script's own failure mode).
- Rollback is reverting the move/project-reference/solution/native-path changes; the Kernel source
  history keeps the original paths.
