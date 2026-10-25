# Batch 1 implementation plan

Precondition: the main session has completed the history-preserving import into `kernel/` from
`7ba4e6e` (or the documented two-parent equivalent). The implementer works on that tree and does
not commit.

## Steps

1. **Prune the imported tree.** Remove `kernel/.agents`, `kernel/.dsh`, `kernel/.trellis`,
   build/local outputs and any duplicate assistant scaffolding. Keep the driver, `src/common`,
   `tests/host`, `tools`, native build files, `.clang-format`, `.clang-tidy`, nested
   `.editorconfig`, `.gitignore`, `.githooks`, `flake.nix`/lock, `CMakeLists.txt`,
   `wf.local.example.json`, `README.md` and `AGENTS.md`.
2. **Migrate specs.** Copy the live Kernel backend specs listed in `design.md` into
   `.trellis/spec/backend/kernel/`. Do not copy `frontend/`, `database-guidelines.md`, `guides/` or
   the template index. Add a Kernel Driver section to `.trellis/spec/backend/index.md`.
3. **Migrate research and evidence.** Copy every Kernel `.trellis/tasks/**/research/**` file plus
   every `verification.md` and `d7-decision.md` into
   `.trellis/spec/backend/kernel/research/<source-task>/...`. Write `research/README.md` with the
   source task, topic and the spec it feeds. Do not copy journals/workspaces or task bookkeeping.
4. **Write `kernel/MIGRATION.md`.** Record the source commit `7ba4e6e`, the source branch tips
   (`main` and `feat/block-sink` at `7ba4e6e`; `feat/host-rule-arrays-process-match` at `0504f12`;
   `experiment/connected-udp-ale-nat` at `625cb80`), the imported paths, the retired paths, the
   spec/research mapping, and the fact that the source repository is not yet archived.
5. **Adapt the native paths.** Fix `kernel/tools/check.sh` root/path discovery if needed and the
   native CMake configure path. Do not weaken any check. Keep the Windows scripts under
   `kernel/tools/windows/`; do not add WDK CI in this batch.
6. **Update `kernel/README.md` / `kernel/AGENTS.md`** only where the new monorepo root, paths or
   retired directories make the old text wrong. Do not rewrite the native guidance.
7. **Verify** with the commands in `design.md`; report environment gaps separately from failures.
8. **Do not touch** `src/WinForward.*`, existing `tests/WinForward.*`, `WinForward.slnx`,
   `Directory.Build.props`, CI files, or any product code. Do not `git commit`/`push`.

## Risky files and rollback

- `kernel/tools/check.sh`: keep its exit-code contract (0 pass, 1 findings, 2 environment/argument)
  and staged-blob semantics; revert path changes if the fast gate's scope changes.
- `.trellis/spec/backend/index.md`: keep the existing family table intact; add the Kernel family
  without moving or renaming existing rows.
- `.trellis/spec/backend/kernel/research/`: this is a copy, not a move of the Kernel tasks; the
  source repository remains untouched until the parent milestone retires it.
- The import commit is the rollback point; do not rewrite it.

## Report

1. Imported/pruned paths and any unexpected tracked files.
2. Spec/research files copied and the index entries.
3. Native path changes and the exact verification commands/results.
4. Any environment gap (missing clang-format/cppcheck/cmake/ninja) and what could not be verified.
5. Anything the parent plan missed; do not fix it outside this batch.
