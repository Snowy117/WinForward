# WinForward.Kernel → WinForward migration

> The native tree under `kernel/` came from the `WinForward.Kernel` repository. WinForward now
> maintains one repository and, eventually, two `BackendDriver.Provider` implementations. This note
> records the source state, what was imported, what was retired, and where the Kernel knowledge now
> lives.

## Source

| Item | Value |
|---|---|
| Repository | `WinForward.Kernel` (sibling checkout `../WinForward.Kernel` on the migration host) |
| Imported commit | `7ba4e6e` (`7ba4e6e626f32bc4a5059cfad32b13f9904e62ba`), `chore: record journal` |
| Tags | none |
| Branch tips at import | `main` = `7ba4e6e`; `feat/block-sink` = `7ba4e6e`; `feat/host-rule-arrays-process-match` = `0504f12`; `experiment/connected-udp-ale-nat` = `625cb80` |
| Imported branches | `main` only; the other three tips are recorded here and were not imported |
| Import commit | `831cde4` — two-parent `git subtree` merge, parents `aa94fba` (WinForward) and `7ba4e6e` (Kernel) |
| Source repository status | not archived; it stays as read-only reference until the parent milestone retires it |

History is preserved through the subtree merge. The source commits keep their historical paths
(`src/...`), so a path-limited log on the imported copy reports the merge itself:

```bash
git log --oneline -- kernel/src/driver   # 831cde4 Add 'kernel/' from commit '7ba4e6e...'
git rev-list --count 7ba4e6e             # 101 commits reachable through the second parent
```

## Imported paths

These are the paths that remain tracked under `kernel/` after the prune. No product code was
modified. The only edits are the path/discovery adaptations described below, the spec pointers in
tool comments, and the `kernel/.gitignore` change that stops ignoring `/AGENTS.md`. `AGENTS.md` was
not in the imported tracked tree (the source repository ignored it); it was rebuilt from the
untracked source file with the stale paths adapted.

| Path | Contents |
|---|---|
| `src/driver/` | WFP callout driver, WDK project, `src/driver/README.md` |
| `src/common/` | Shared C++ ABI headers and the portable blob/matcher core |
| `src/userland/` | C# controller, userland, tests and `check-effective-lines.py`; kept as scaffolding until the backend milestone retires `Relay/`, `Control/`, `Configuration/` |
| `tests/host/` | CMake/gtest host suites and the C# differential fixtures |
| `tools/check.sh`, `tools/lint/` | Host gate entry point, forbidden-pattern checker and rules |
| `tools/linux/`, `tools/windows/`, `tools/socks5/`, `tools/userland/`, `tools/wfprobe/` | Linux gates, Windows gates, SOCKS5 doubles and the driver-parity suite |
| `CMakeLists.txt`, `.clang-format`, `.clang-tidy`, `.editorconfig` | Native build and format inputs |
| `.gitignore`, `.githooks/`, `.envrc`, `flake.nix`, `flake.lock`, `wf.local.example.json` | Repository hygiene and the native devShell |
| `README.md` | Native build/gate documentation, adapted for the `kernel/` root |
| `AGENTS.md` | Subtree-scoped assistant rules, rebuilt from the untracked source file and path-adapted |
| `MIGRATION.md` | This note; new in the import |

## Retired paths

| Path | Why it was removed |
|---|---|
| `.agents/` | Trellis skill scaffolding; the monorepo root owns platform helpers |
| `.dsh/` | Local assistant scaffolding, not product |
| `.trellis/` | Kernel spec/task/workspace tree. Live specs and evidence moved to `.trellis/spec/backend/kernel/`; journals, workspaces, `task.json`, PRDs and designs stay in the source history |
| `.gitattributes` | Duplicate of the root `.gitattributes` Trellis `merge=union` rule |
| `build/`, `cmake-build-debug/`, `bin/`, `obj/`, `x64/`, `.direnv/`, `.idea/`, `local/`, `wf.local.json`, `*.sys`, `*.pdb` | Build outputs or machine state; never tracked in the source repository, not imported |

## Spec mapping

Live Kernel specs → `.trellis/spec/backend/kernel/`:

| Source (`.trellis/spec/backend/`) | Destination |
|---|---|
| `wfp-architecture.md` | `kernel/wfp-architecture.md` |
| `host-rule-blob.md` | `kernel/host-rule-blob.md` |
| `forwarded-rule-blob.md` | `kernel/forwarded-rule-blob.md` |
| `wfp-connected-udp-record-refcount-leak.md` | `kernel/wfp-connected-udp-record-refcount-leak.md` |
| `cpp-conventions.md` | `kernel/cpp-conventions.md` |
| `csharp-conventions.md` | `kernel/csharp-conventions.md` |
| `quality-guidelines.md` | `kernel/quality-guidelines.md` |

Not migrated: `directory-structure.md`, `error-handling.md` and `logging-guidelines.md` are unfilled
Trellis templates in the source (`Status: To fill` in its index), so they carry no Kernel-specific
rule; the root [Directory Structure](../.trellis/spec/backend/directory-structure.md),
[Error Handling](../.trellis/spec/backend/error-handling.md) and
[Logging Guidelines](../.trellis/spec/backend/logging-guidelines.md) own those subjects. `frontend/`,
`database-guidelines.md`, `guides/` and the template `index.md` were excluded for the same reason the
design excluded them: the monorepo has its own.

The backend index gained a Windows WFP Kernel Driver family; the existing families are unchanged.

## Research mapping

Every `research/**` document, every `verification.md` and every `d7-decision.md` from the Kernel
`.trellis/tasks/**` (all under `archive/2026-10/` at import) was copied to
`.trellis/spec/backend/kernel/research/<source-task>/...`, preserving the source task name:

| Source task | Copied documents |
|---|---|
| `10-20-wfp-csharp-blob` | `research/blob-contract.md` |
| `10-20-wfp-driver-rules` | `research/findings.md` |
| `10-20-wfp-mvp` | `research/perf-mvp.md` |
| `10-20-wfp-phase0-probe` | `research/phase0-findings.md` |
| `10-21-host-udp-classify` | `research/b2b-smoke.md`, `research/vm-crash.md` |
| `10-21-host-udp-route-a-probe` | `research/route-a-findings.md` |
| `10-21-host-udp-spike` | `research/spike-udp.md` |
| `10-21-wfp-userland` | `research/userland-findings.md` |
| `10-22-host-udp-verification` | `research/connected-context.md` |
| `10-23-fwd-b3-l2-spike` | `research/environment.md`, `research/spike-l2.md` |
| `10-23-fwd-b4b-tcp-dnat` | `verification.md` |
| `10-23-fwd-b5-udp-gateway` | `verification.md` |
| `10-23-fwd-b6-hardening` | `verification.md`, `d7-decision.md` |
| `10-24-block-classify-enforced-sink` | `verification.md` |
| `10-24-fwd-ipv6-e2e` | `research/environment.md`, `verification.md` |
| `10-24-host-rule-arrays-process-match` | `research/final-check.md`, `research/plan-review.md`, `research/structural-review.md`, `research/winforward-rule-semantics.md`, `verification.md` |

`.trellis/spec/backend/kernel/research/README.md` indexes each document by topic and by the spec it
feeds. Raw evidence directories referenced by those documents (for example
`10-24-fwd-ipv6-e2e/verification/check-final/`) were not copied; they stay in the source repository
until it is retired.

## Path and gate adaptation

- `kernel/tools/check.sh` already derives its root from its own location (`kernel/`), so the format,
  effective-line, cppcheck, clang-tidy and `wf_check` steps keep their scope. The worktree
  forbidden-pattern step now passes `--root "${repo_root}"` to `tools/lint/forbidden.py`: the script
  would otherwise use the monorepo root, where the `src/driver/**` / `src/common/**` rule globs match
  nothing, and the gate would pass with zero scanned files.
- Scripts under `kernel/tools/windows/` keep deriving `-Root` two levels up from `tools\windows`
  (that is `kernel/`); the WDK build and `gate.ps1` are not wired to CI in this batch. `-Root` means
  the `kernel/` subtree root, not the repository root; since the contracts extraction the C# build
  under it reaches the repository root through relative paths (see Contracts extraction).
- `kernel/.githooks/pre-commit` and `install.sh` still resolve the git toplevel, so this batch leaves
  them untouched; the parent plan installs one root dispatcher instead.
- Configure and run the host tests from the monorepo root:

  ```bash
  cmake -S kernel -B kernel/build/check -G Ninja
  cmake --build kernel/build/check --target wf_check
  ```

## Contracts extraction

The Kernel C# contract surface now lives at the repository root instead of inside the native tree.
`Device/`, `Policy/` and `Forwarded/` moved verbatim from
`kernel/src/userland/WinForward.Userland/` to `src/WinForward.Kernel.Contracts/`, which is a member
of `WinForward.slnx` and therefore subject to the root analyzers and `dotnet format`.

| Item | State |
|---|---|
| Moved | `Device/`, `Policy/`, `Forwarded/` → `src/WinForward.Kernel.Contracts/{Device,Policy,Forwarded}/` |
| Unchanged | Byte layouts, IOCTL structs, blob compilers, golden bytes, process-path normalization |
| New assembly | `WinForward.Kernel.Contracts`; `RootNamespace` stays `WinForward.Userland`, so the type namespaces are still `WinForward.Userland.*` |
| Old project | `kernel/src/userland/WinForward.Userland` is a thin `ProjectReference` to the new project plus `Configuration/`, `Control/` and `Relay/`; its `AssemblyName`/`RootNamespace` are unchanged |
| References | `WinForward.Userland.Tests` and `WinForward.Controller` still reference only the old project; project-reference transitivity exposes the moved types. Measured on the actual builds: `WinForward.Userland.slnx` compiles and `check.sh --full` (including the `wf_check` test runner) passes |
| Internals | The new project grants `InternalsVisibleTo` to `WinForward.Userland.Tests` (ABI/layout assertions) and to `WinForward.Userland` (its `Configuration/` reads the forwarded blob sentinel). The Controller needs no grant |

The old project is a compatibility shim, not a second userland: it adds no types of its own beyond
the scaffolding it still carries.

Deferred, and deliberately not mixed into the extraction:

- Retiring `Configuration/`, `Control/` and `Relay/`. They stay until the Controller and the test
  runner stop using them (and `tools/wfprobe` stops driving the Controller binary in its gate
  suites); the backend seam replaces them with `WinForward.Configuration` / `WinForward.Runtime`.
- Renaming the `WinForward.Userland.*` namespaces to `WinForward.Kernel.Contracts.*`. It is a
  mechanical change across the moved files, the tests and the Controller, and it would bury the
  extraction diff.

Gate and tool effects:

- `kernel/tools/userland/verify-gates.sh` mutates the moved sources by path, so its mutation targets
  are now `../src/WinForward.Kernel.Contracts/Device/ProbeIoctl.cs` and
  `../src/WinForward.Kernel.Contracts/Policy/RuleBlobCompiler.cs` (the script runs from `kernel/`).
  The `Configuration/` and `Relay/` mutation paths stay under `kernel/src/userland/` while that
  scaffolding remains.
- `tools/wfprobe/wfprobe.csproj` targets `net10.0` like the rest of the userland. It had drifted to
  `net11.0`, which the repository SDK (`global.json`) cannot build; the Windows VM's SDK 11 builds
  `net10.0` as well. The project also removes the four root analyzer packages it inherits through
  the filesystem walk to the root `Directory.Build.props`: the tool is not in the product solution
  and predates those rules, and enabling them makes a Release build report 500 analyzer errors over
  20 files immediately. The csproj comment carries the measured rule counts (MSBuild prints each
  diagnostic again in the failure summary, so the raw log has twice as many error lines) and the
  removal condition.
- `tools/windows/gate.ps1` still builds `WinForward.Userland.slnx`, and the Controller output path is
  unchanged, but its build surface is no longer self-contained: the solution now includes
  `src/WinForward.Kernel.Contracts` through a cross-tree path (`../../../src/...` from
  `kernel/src/userland`), and the shim references the same project with `../../../../src/...`. A
  Windows machine deployed with only the `kernel/` subtree cannot build it.
- The Windows deployment therefore has to carry the complete repository layout: `<repo>/kernel`
  plus `<repo>/src/WinForward.Kernel.Contracts`, and the root build files the contracts project
  imports (`Directory.Build.props`, `Directory.Packages.props`, `src/Directory.Build.props`, and
  `analyzers/WinForward.Analyzers`). `gate.ps1 -Root` keeps pointing at `<repo>/kernel`; the
  relative paths inside the solution and the project references do the rest.

## Known follow-ups

- `kernel/src/userland/**` comments and `kernel/src/userland/Directory.Build.props` still name the
  pre-import spec path `.trellis/spec/backend/<file>.md`; the evidence task pointer in
  `PolicyLimits.cs` points at a task directory that was not migrated. Product code was out of scope
  for this batch.
- The Kernel devShell is still `kernel/flake.nix`; merging it into the root devShell, one root
  pre-commit dispatcher, the WDK CI job and the C# backend projects are later phases.
- The Kernel documents are Chinese, as their source index declares. The root library's English rule
  governs new documents; translating this imported family is a separate decision.
