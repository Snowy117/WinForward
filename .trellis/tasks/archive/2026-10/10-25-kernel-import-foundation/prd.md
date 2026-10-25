# Batch 1: import the Kernel repository and land the monorepo foundation

Parent: `.trellis/tasks/10-25-kernel-backend-integration`

## Goal

Import `WinForward.Kernel` `main` at `7ba4e6e` under `kernel/` with history, migrate the live
Kernel specs plus its research/verification/decision documents, adapt the native quality gates to
the new root, and prove the existing WinForward .NET build/test/format gates still pass. No product
code and no C# Kernel project enters the solution in this batch.

## Requirements

- **B1-R1 Import with history.** The imported tree is reachable through `git log -- kernel/...` and
  records the source commit/tag and branch tips in `kernel/MIGRATION.md`.
- **B1-R2 Product-only native tree.** Keep `kernel/src`, `kernel/tests`, `kernel/tools`, the native
  build files, `.clang-format`, `.clang-tidy`, the nested `.editorconfig`, `flake.nix`/lock and the
  native README/AGENTS. Remove the imported `.agents/`, `.dsh/`, `.trellis/` (after migration),
  build outputs, local configs and other non-product scaffolding.
- **B1-R3 Specs and research.** Move the live Kernel backend specs into
  `.trellis/spec/backend/kernel/` and add a Kernel family to the backend spec index. Copy every
  Kernel `.trellis/tasks/**/research/**` document plus every `verification.md` and
  `d7-decision.md` (live and archived) into `.trellis/spec/backend/kernel/research/`, preserving
  source task names and adding an index. Do not copy journals/workspaces or task bookkeeping.
- **B1-R4 Native gates.** Adapt `kernel/tools/check.sh` and the native paths to the new root; the
  host tests configure and build from `kernel/`. Do not weaken the format/line/cppcheck/
  forbidden-pattern checks.
- **B1-R5 No product change.** No `src/WinForward.*` or existing `tests/WinForward.*` edits, no new
  C# project in `WinForward.slnx`, no BackendDriver/backend implementation. The root .NET build,
  tests and format gate must be unchanged and green.

## Acceptance Criteria

- [x] B1-AC1: `kernel/` contains the driver, common ABI, host tests and tools; no tracked build
      outputs, local configs, journals or duplicate Trellis tree; the source history is reachable
      through the import merge's second parent (`git rev-list --count 7ba4e6e`).
- [x] B1-AC2: `.trellis/spec/backend/kernel/` holds the live specs and
      `.trellis/spec/backend/kernel/research/` holds the research/verification/decision documents
      with an index; the backend spec index links the Kernel family.
- [x] B1-AC3: `kernel/tools/check.sh --fast` passes; the CMake host tests configure and the
      `wf_check` target passes; the root `dotnet build WinForward.slnx -c Release`,
      `dotnet test WinForward.slnx -c Release` and the format gate pass.
- [x] B1-AC4: `kernel/MIGRATION.md` records the source commit `7ba4e6e`, branch tips, imported
      paths, retired paths and the spec/research mapping.
- [x] B1-AC5: `git diff --stat` shows only `kernel/**`, `.trellis/**`, root docs/config (if needed);
      no product code changed.

## Out of Scope

- `WinForward.Kernel.Contracts`, `WinForward.Kernel.Tests`, backend selection, capability gate,
  host TCP/UDP, forwarded, ARM64, production signing/installer and CI wiring for WDK.
- Rewriting or modernizing the Kernel code; only path/build adaptation is allowed.
- Copying the Kernel workspace, journals or archived task bookkeeping.
