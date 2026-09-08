# Codebase design review: findings & remediation plan (parent)

## Goal

Remediate the findings of the 2026-09-08 full-codebase design review (deep-module analysis +
`.trellis/spec/backend/*` compliance audit) across three workstreams: correctness fixes (P0),
behavior-zero structural refactors (P1), and dead-surface/hygiene cleanup (P2).

## Source requirement set

- Review evidence: `research/00-synthesis.md` (priority table) + area reports `research/01`–`05`
  (all findings carry file:line evidence).
- Overall verdict that frames scope: the codebase is textbook-quality deep-module design with
  localized, non-systemic issues — remediation must not damage the strengths listed in
  `research/00-synthesis.md` ("Strengths worth preserving").

## Task map

| Child | Deliverable | Complexity |
|---|---|---|
| `09-08-p0-correctness` | 5 behavioral risk fixes, each with a regression test | complex (design.md + implement.md before start) |
| `09-08-p1-structure` | Behavior-zero refactors: coordinator/benchmark splits, file re-homing, edge normalization | complex (design.md + implement.md before start) |
| `09-08-p2-hygiene` | Dead-surface deletion, TestHelpers convergence, diagnostics & Cli coverage | lightweight (PRD-only) |

## Ordering & dependencies

Recommended execution order: **P0 → P2 → P1**.

- P0 is independent (behavior fixes in different files than the refactor targets) and highest value.
- P2 should precede P1: deleting FlowTable's dead members before Domain.cs's split shrinks the
  surface that P1 physically moves; TestHelpers convergence reduces fake churn during P1 test moves.
- P1's UdpProxyCoordinator split includes the tombstone→cooldown rename; its spec update must land
  with the code change, not before.

Parent/child is not a dependency system: if a child must wait, the ordering lives here and in the
child PRDs.

## Cross-child acceptance criteria

- [ ] All three children planned, executed, checked, and archived independently.
- [ ] Full solution builds with zero warnings (`TreatWarningsAsErrors` on).
- [ ] `dotnet test` green; final test count = recorded pre-remediation baseline (463 as of
      2026-08-29) + P0 regressions + P2 additions; each child records its own before/after totals
      (P1 batches must be behavior-zero: totals unchanged within the child).
- [ ] No `.cs` file in `src/` or `benchmarks/` exceeds 400 effective lines (non-blank, non-comment).
- [ ] file-name = main-type convention holds repo-wide (spec-exempted `ConfigurationModels.cs`
      excepted).
- [ ] Spec updates landed via trellis-update-spec: sanctioned-edge set current (UdpProxy→Socks5
      resolution), tombstone naming unified, any new lessons from P0 debugging captured.

## Integration review (parent responsibility)

- [ ] After children complete: verify P2 deletions removed nothing P1 later needed (rg re-check of
      every deleted symbol against final tree).
- [ ] Re-run the design-review spot checks from `research/00-synthesis.md` P0/P1/P2 tables and
      confirm each row is resolved or explicitly waived with rationale.
