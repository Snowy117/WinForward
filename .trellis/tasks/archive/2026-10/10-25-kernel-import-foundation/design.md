# Batch 1 design: import and native-gate adaptation

## Import

Use a history-preserving subtree/filtered merge from `../WinForward.Kernel` `main` into
`kernel/`. The source commit is `7ba4e6e` (same as `feat/block-sink`, clean tree). If a subtree
merge is not possible without committing unrelated work, import the tree with a two-parent merge
(`git merge --allow-unrelated-histories -s ours` + `git read-tree --prefix=kernel/`) and record the
same source commit. Do not use a plain copy when history can be preserved.

After the import, remove from `kernel/`:

- `.agents/`, `.dsh/`, `.trellis/` (the live specs/research are copied to the root first);
- build outputs or local state (`build/`, `x64/`, `obj/`, `result*`, `.direnv/`, `local/`,
  `wf.local.json`, `.idea/`);
- task journals/workspaces and any duplicate assistant scaffolding.

Keep the native product tree, the native README/AGENTS, `.clang-format`, `.clang-tidy`, the nested
`.editorconfig`, `.gitignore`, `.githooks`, `flake.nix`/lock, `CMakeLists.txt`, `wf.local.example.json`
and the tools. The root `.gitignore` already covers most build outputs; add a `kernel/`-scoped rule
only if a tracked/generated path would otherwise escape.

## Specs and research

Live Kernel specs → `.trellis/spec/backend/kernel/`:

| Source | Destination |
|---|---|
| `wfp-architecture.md` | `wfp-architecture.md` |
| `host-rule-blob.md` | `host-rule-blob.md` |
| `forwarded-rule-blob.md` | `forwarded-rule-blob.md` |
| `wfp-connected-udp-record-refcount-leak.md` | `wfp-connected-udp-record-refcount-leak.md` |
| `cpp-conventions.md` | `cpp-conventions.md` |
| `csharp-conventions.md` | `csharp-conventions.md` |
| `quality-guidelines.md` | `quality-guidelines.md` |
| `logging-guidelines.md` | `logging-guidelines.md` |
| `error-handling.md` | `error-handling.md` |
| `directory-structure.md` | `directory-structure.md` |

Do not copy the Kernel `frontend/`, `database-guidelines.md`, `guides/` or the template
`index.md`; the root has its own guides and index. Add a **Kernel Driver** section to
`.trellis/spec/backend/index.md` pointing at the new family.

Research/decision documents → `.trellis/spec/backend/kernel/research/<source-task>/<file>`:

- every `.trellis/tasks/**/research/**` file, live and archived;
- every `verification.md`;
- every `d7-decision.md`;
- for the blocked-rebuild decision, the `10-23-fwd-b6-hardening` verification/decision pair is the
  anchor; do not copy unrelated PRDs/designs unless they are the only place a decision is recorded.

Write a `research/README.md` index: source task, document, topic, and the spec it feeds. Keep the
source task names (for example `10-24-fwd-ipv6-e2e/verification.md`) so the provenance survives.

## Native gates

- `kernel/tools/check.sh` resolves its root from its own location; verify it still checks
  `kernel/src/driver`, `kernel/src/common` and `kernel/tests/host` after the move. If it assumes the
  old repository root, adapt the root computation only.
- Configure the host tests from the root: `cmake -S kernel -B kernel/build/check -G Ninja` then
  `cmake --build kernel/build/check --target wf_check`. Keep the build directory ignored.
- Keep the Windows scripts under `kernel/tools/windows/`; do not wire the WDK CI job in this batch.
- The root .NET solution is untouched; the root `dotnet format`, build and test gates must pass.

## Verification

```bash
git log --oneline -- kernel/src/driver | head
git ls-files kernel | rg -i '(^|/)(build|bin|obj|x64|\.direnv|local)/|\.sys$|wf\.local\.json'  # must be empty
kernel/tools/check.sh --fast
cmake -S kernel -B kernel/build/check -G Ninja
cmake --build kernel/build/check --target wf_check
dotnet build WinForward.slnx -c Release
dotnet test WinForward.slnx -c Release
dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore
```

A missing native toolchain is reported as an environment gap, not a green result; the batch must
not claim the native gate passed if `check.sh` exits 2.

## Rollback

The import commit is additive under `kernel/`; the spec/research moves are under `.trellis/`.
Reverting those commits restores the pre-import WinForward tree. Do not rewrite published history;
the source repository is only archived after the parent milestone completes.
