# Implementation plan: spec revision

Execution record for `prd.md` under the rules of `design.md`. Written before the revision waves
started and updated as they land.

## Waves

| Wave | Work | Agent | Status |
|---|---|---|---|
| 0 | Recon: measure the library, build the identifier/link/size checker | lead | done |
| 1 | Read-only analysis of the five oversized documents (`research/plan-*.md`) | 5 agents | done |
| 2a | Revise `async-lifetime.md`, `logging-guidelines.md`, `test-stability.md`, `quality-guidelines.md` in place | 4 agents | running |
| 2b | Split the five oversized documents into families | 5 agents | running |
| 2c | Revise `directory-structure.md`, `traffic-policy-lifecycle.md` + new `idle-expiry-sweep.md`, both guides | lead | done |
| 3 | Integration: indexes, cross-document moves, error-handling dedup, comment sweep, verification | lead | pending |

Wave 1 produced one plan per oversized document, each carrying verified stale claims with `file:line`,
verbosity cuts with line ranges, a proposed family and the cross-references the split breaks. The lead
adjudicated every judgement call before dispatch; the adjudications are recorded in the dispatch
prompts and summarised here:

- **`hot-path.md`** → hub + 8 children (1404 → ~1190 effective). The ≤8,200 band loses its number if it
  cannot be attributed to one shape. Dead names are reworded, never allowlisted. The methodology child
  is `benchmark-methodology.md` so it cannot be confused with the harness family's `measurement-*`
  names. Cross-document moves (owner-table coalescer, SOCKS5 redirect half) are deferred to wave 3.
- **`udp-relay.md`** → hub + 6 children. The two smallest stay separate: a merged
  `udp-association-lifecycle.md` would read as a near-duplicate of `udp-session-lifecycle.md` in the
  index. The superseded 2026-09-20 design is deleted where the surviving design restates it.
- **`tcp-local-redirect.md`** → hub + 5 children. The capacity-rejection RST splits into its *shape*
  (close injection) and its *trigger* (SYN admission), linked in both directions. The `ContinueWith`
  rule is deleted: the mechanism is gone and `WF0002` forbids the pattern. The close-drain residual is
  stated as today's contract only — task `10-07-tcp-close-drain` owns its redesign.
- **`windows-ndisapi.md`** → hub + 3 children, with the adapter identity contract folded into the hub
  (a 33-line hub is a stub). The duplicated section is deleted. Frame-lifetime rules stay put rather
  than moving into `hot-path.md` mid-revision. No `windows-ip-helper.md` in this pass.
- **`measurement-harness.md`** → hub + 6 children, not 9: 40-line children are over-fragmentation.
  The analyzer's output file names are **live**, not stale — the seeded checker hits were wrong. The
  `check-readme-contract.py` archive-path bug is documented, not fixed (see Open items).

## Integration checklist (wave 3)

1. `backend/index.md`: list every document, hub and child, grouped by family; refresh the index
   descriptions; keep the history line truthful.
2. `guides/index.md`: trim the template preamble; retarget the `measurement-harness.md §3.x` citations
   to the new children.
3. Cross-document moves reported by wave 2, in this order: dedup `error-handling.md` against
   `udp-relay.md` / `tcp-local-redirect.md` / `windows-ndisapi.md`; dedup `hot-path.md`'s host-lumps
   child against `test-stability.md` §4; retarget every `§`-citation in the library.
4. Comment sweep: `rg -n '<doc>\.md' src tests benchmarks -g '!benchmarks/results/**'`, and confirm
   each citation still resolves.
5. Move tables: every hub carries one, and the old numbers cited from `benchmarks/results/**` appear
   in it.
6. Verify: `research/verify-specs.py` green for size, structure, links, identifiers and language; then
   `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` and
   `dotnet build WinForward.slnx -c Release`, needed only because comments were touched.
7. Journal entry and the report to the user, including the open items below.

## Open items carried forward

- **`check-readme-contract.py:47` hard-codes an archived task path** (`.trellis/tasks/10-07-e2e-harness-refactor/research/contract-rename.json`), so the README contract gate exits 2 instead of running. `check-fixture-drift.py:40` has the same bug; `tests/WinForward.E2E.Tests/RepoPaths.cs:49-71` shows the archive-aware lookup the .NET side already uses. Out of scope for a spec revision — the documents describe the gate's real behaviour, and the fix is one line per script when the user wants it.
- **`UdpAssociationTable.RemoveExpired`** and its table-level siblings survive as test-only surfaces; the coordinator-level sweeps superseded them.
- **`test-stability.md` cites `hot-path.md §6`**; the citation is retargeted to the surviving section title once the hot-path family lands.
- **Run counts and hardware figures** that live only in session evidence (`/tmp/wf-*`) stay verbatim: they are the record, and nothing in the repo contradicts them.
