# Implementation Plan

Pre-flight (once):
- [ ] Baseline: `dotnet test` green, record test count. Record `git status` clean.

Step 1 — Moves (single commit unit):
- [ ] `mkdir Capture TcpRedirect UdpProxy Socks5` under `src/WinForward.Runtime/`
- [ ] `git mv` per design.md table (7 / 14 / 4 / 2 files; root keeps 5)
- [ ] Rewrite `namespace` declaration in each moved file (file-scoped `namespace X;` — preserve style)
- Validation: `dotnet build` → expect ONLY missing-type/missing-using errors (CS0246 etc.); no other kinds.

Step 2 — Using fixup (scripted):
- [ ] Write throwaway python script (inline heredoc, not committed) mapping the 26 moved files' declared types (incl. nested, e.g. `SelfTrafficKey` stays root — only moved files' types) → new namespace; insert missing usings into all .cs files under src/, tests/, benchmarks/
- [ ] `dotnet build` clean; fix stragglers by hand (doc-comment cref warnings → add usings only if warned)
- [ ] Prune unnecessary `using WinForward.Runtime;` in files that no longer reference root types (`dotnet format` or manual pass)
- Validation: `dotnet build` zero warnings delta vs baseline.

Step 3 — Verify:
- [ ] `dotnet test` — same pass count as baseline
- [ ] `dotnet format --verify-no-changes` (or skip if repo doesn't enforce; check for .editorconfig first)
- [ ] `git status`: only expected moves + modified usings; `git diff --stat` sanity

Step 4 — Gate:
- [ ] trellis-check dispatch (full-scope quality check)
- [ ] User review of final layout

Rollback point: everything is one logical change; `git checkout -- . && git clean` (untracked dirs) or revert the single commit.

Review gates: after Step 1 build errors MUST be only CS0246-style; anything else = wrong move, stop.
