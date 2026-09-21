# Lifetime enforcement analyzers

## Goal

Create `analyzers/WinForward.Analyzers/` with three rules — `WF0001` (discard of an **unawaited**
awaitable), `WF0002` (`.ContinueWith`), `WF0003` (`Task.Run`/`Task.Factory.StartNew` outside the
allowlist) — wire them into `src/**`, and land them with a reason-documented per-file allowlist for
the legacy sites.

## Requirements

- Project at `analyzers/WinForward.Analyzers/` (netstandard2.0, `Microsoft.CodeAnalysis.CSharp`),
  referenced from a new `src/Directory.Build.props` that first `<Import>`s the repo-root one;
  Roslyn packages are pinned in `Directory.Packages.props` (`design.md` §3.1, E3).
- Rules key on **awaitable types**. `WF0001` must not fire on `_ = await X`. `WF0004` is not
  implemented — a bare unawaited awaitable is already `CS4014`, fatal under
  `TreatWarningsAsErrors=true` (`design.md` §3.2, E1).
- `WF0002` needs **no** allowlist entry: `design.md` §4.4 removes both `.ContinueWith` sites
  structurally.
- The escape hatch is `QuiescenceScope.Run` (C1's type), not a `Detach` method or attribute (E2).
- Severity is explicit `error` in `.editorconfig`; add `AnalyzerReleases.Shipped.md` /
  `AnalyzerReleases.Unshipped.md` (E5).
- The six legacy sites are allowlisted per-file with a written reason, glob-scoped (E4).
- Adds the WF rule table to `.trellis/spec/backend/async-lifetime.md` (created by C1).

## Acceptance Criteria

- [ ] Each rule fails the build on a scratch violation and is proven **not** to fire on the benign
      shapes enumerated in `design.md` §3.2.
- [ ] Analyzer tests exist in `tests/WinForward.Analyzers.Tests`.
- [ ] The allowlist contains exactly the six legacy sites, each with a written reason.
- [ ] `tests/**` and `benchmarks/**` are not flagged.
- [ ] Full gates green (format / Release zero-warning / tests / `jb inspectcode`).

## Notes

- The analyzer code is independent of C1. The `async-lifetime.md` rule table is appended **after** C1 has
  created that file, to keep the file single-owner and avoid a conflict.
- This child is a complex task: write its own `design.md` and `implement.md` before `task.py start`,
  referencing the parent `design.md` §3.
