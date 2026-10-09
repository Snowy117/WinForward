# Spec revision: correct stale claims, cut verbosity, split oversized docs

## Goal

Bring `.trellis/spec/` back in line with the code it describes. Every code-level claim is re-verified
against the current tree, redundant prose is deleted, and any document still oversized after trimming
becomes a hub plus a family of focused children — so a reader who needs one rule lands in one short
file, and the library stays small enough to be read at the start of a task.

## Background

The spec library is this project's only cross-session memory, and both `AGENTS.md` and the Trellis
workflow point agents at it before any implementation. It has drifted.

Size, as of 2026-10-09 (raw lines / effective lines — non-blank, non-heading — across 16 files,
6037 / 4600 total):

| Doc | Raw | Effective |
|---|---|---|
| `hot-path.md` | 1655 | 1404 |
| `udp-relay.md` | 1029 | 790 |
| `measurement-harness.md` | 607 | 480 |
| `windows-ndisapi.md` | 566 | 412 |
| `tcp-local-redirect.md` | 470 | 306 |
| `async-lifetime.md` | 412 | 326 |
| everything else | ≤ 295 | ≤ 236 |

The project caps every `.cs` file at 400 effective lines (`directory-structure.md`) on the grounds
that a file past that size stops being readable in one sitting. The five docs above are past the same
point, and `hot-path.md` is past it by a factor of three and a half.

Drift is real, not hypothetical. An automated cross-check of every backticked identifier in the
library against the source tree (`src/`, `tests/`, `benchmarks/`, `analyzers/`) reported, among
others: the `UdpAssociation*` family that task `10-05-remove-udp-association-sharing` removed;
`tcp-local-redirect.md` refers to `TcpRelayFaultObserver.cs`, a file that no longer exists;
`measurement-harness.md` still routes the reader through the Python `analyze.py`, `tables.md` and
`verdict.json` that commit `b3b4aa4` retired; and `windows-ndisapi.md` carries a **literally
duplicated section** — the 2026-09-17 adapter self-healing contract appears twice (around line 494,
and again mangled around line 532), the second copy having lost its backticks and its `>=` / `->`
operators.

Two facts constrain how the docs may be reorganised:

- Live code links into them. About 45 comments across `src/`, `tests/` and `benchmarks/` name a spec
  document, and several name a *section* — by number (`hot-path.md #3`, `test-stability.md` §2.9) or
  by title (`hot-path.md`, "Allocation-gate stability"; `windows-ndisapi.md`, "Batched reinjection";
  `directory-structure.md`, "文件行数上限").
- `benchmarks/results/**` holds dated evidence records that must never be rewritten. Some of them
  cite `hot-path.md` §3 / §4 / contract 9 / line ranges. Those citations cannot be updated; they can
  only be kept resolvable.

`backend/index.md` declares the library language as English. `directory-structure.md` is the single
outlier: 61 of its 121 lines are Chinese prose.

## Requirements

- **R1 — Accuracy.** Every code-level claim is verified against the current tree before it survives:
  symbol names, file paths, namespaces, signatures, defaults, thresholds, config keys, event names,
  test-class names. Stale claims are corrected from the code, or deleted when the thing they describe
  is gone. Nothing is invented, and nothing is "tidied" into a claim the code does not support.
- **R2 — Concision.** Redundant prose goes: rules stated twice in different words, changelog-style
  narration that no longer informs a decision, template blocks whose only content is their heading,
  incident retellings longer than the rule they justify, tables that restate the paragraph above them.
  Trimming must not lose a rule, a threshold, an evidence pointer, or a "why" that a reader would
  otherwise have to rediscover.
- **R3 — Splitting.** Any doc still over ~400 lines after R1 and R2 becomes a family: the original
  filename survives as a real document (its scope, its core invariants, a topic map linking the
  children) rather than an empty table of contents, and children are named for their topic so they
  read unambiguously in the index table. Split by topic, never by source line range.
- **R4 — Navigability.** `backend/index.md` and `guides/index.md` list and describe every doc after
  the split. Every relative link in the library resolves. A section title that live code cites is
  either preserved on the section where it lands, or the citing comment is updated in the same
  change. Each hub carries a short "where things moved" table so the numbered citations frozen in
  `benchmarks/results/**` stay resolvable by a human.
- **R5 — Provenance discipline.** A date or task id stays when it justifies a rule (the incident that
  produced it, the hardware that verified it, the measurement that set a threshold) and goes when it
  is only a changelog entry. Rules that can silently rot — thresholds, defaults, allowed-exception
  lists, allowlists — keep an explicit anchor to the code that owns them.
- **R6 — Language.** English, per `backend/index.md`. The Chinese prose in `directory-structure.md`
  is translated; isolated non-English terms that name a real artifact (a log field, an existing
  section title cited from code) stay as they are.

## Out of scope

- Rewriting the dated evidence under `benchmarks/results/**`.
- Editing code, except comment references that would otherwise point at a moved or renamed spec
  section (comment text only — no behaviour, no signatures).
- Adding rules the code does not already demonstrate.

## Acceptance Criteria

- [ ] **AC1 — Accuracy.** For every doc, each stale claim found by the reference cross-check is
      either corrected against a cited `file:line` or removed, and the per-doc plan records which.
- [ ] **AC2 — Size.** No doc exceeds 400 lines. `hot-path.md`, `udp-relay.md`,
      `measurement-harness.md`, `windows-ndisapi.md` and `tcp-local-redirect.md` are split into
      indexed families. Total effective lines across the library drop materially, with the drop
      attributable to R2 rather than to lost rules.
- [ ] **AC3 — Structure.** `backend/index.md` describes every resulting doc with a one-line scope;
      each hub links its children; no duplicated section survives anywhere in the library (the
      `windows-ndisapi.md` duplicate is gone).
- [ ] **AC4 — Links.** A link/identifier check over `.trellis/spec/**/*.md` reports zero broken
      relative links and zero unresolved code symbols. Remaining non-code identifiers (BCL types,
      analyzer names, native ABI members) are individually justified.
- [ ] **AC5 — Traced references.** Every live code comment that cites a spec section still resolves:
      either the title survived the move or the comment was updated. The list of updated comments is
      recorded in the task.
- [ ] **AC6 — Consistency.** `directory-structure.md` is English; `logging-guidelines.md` and
      `windows-ndisapi.md` carry no unexplained non-English fragments.
- [ ] **AC7 — Gates.** `dotnet format WinForward.slnx --severity info --verify-no-changes` reports
      nothing (comments touched by AC5 are formatted) and `dotnet build WinForward.slnx -c Release`
      stays zero-warning.

## Risks

- **Trimming away the "why".** The docs are dense with incident evidence that is expensive to
  rediscover. Mitigation: R2's deletion test is "does a reader still know why the rule exists?" —
  when in doubt the evidence pointer stays, compressed.
- **Split-by-anchor.** Moving a section breaks a code comment silently (comments are not compiled
  against the docs). Mitigation: AC5's explicit sweep, plus the hub move tables.
- **Rewriting instead of revising.** A subagent that "improves" prose can introduce claims the code
  does not support. Mitigation: every changed factual claim needs a cited `file:line`; the check
  phase spot-checks citations.
