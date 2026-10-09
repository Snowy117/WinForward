# Design: spec revision

How the library is reorganised, what an authoring agent must obey, and how the result is verified.
The requirement set is `prd.md`; this file is the technical answer to it.

## 1. Target shape

The library stays two layers (`backend/`, `guides/`) with one `index.md` per layer. A document that
outgrows the ceiling becomes a **family**: the original filename survives as the hub (scope, the
invariants that hold across the topic, a topic map), and the detail moves into siblings named for
their subject.

Rules for a family:

- The hub is a document, not a table of contents. It must be worth reading on its own: it states the
  topic's scope, the rules that apply everywhere in the family, and the topic map.
- A child is one topic a reader can finish in one sitting (~400 lines is the ceiling, and a child
  should aim lower). Split by subject, never by source line range.
- A child's filename must be unambiguous when read alone in the index table: `udp-session-lifecycle.md`,
  not `udp-relay-2.md`.
- Children link back to the hub, the hub links to each child, and **`backend/index.md` lists the hub
  *and* every child**, grouped by family with the hub first. An index that hides eight of the layer's
  documents behind one row is not usable, and the mechanical check in `verify-specs.py` (every `.md`
  in a layer appears in that layer's `index.md`) enforces this reading. Nothing is orphaned.
- Every hub carries a **Where things moved** table mapping its old section names — and the old
  section numbers, where the doc numbered them — onto the children. This is what keeps the numbered
  citations frozen in `benchmarks/results/**` resolvable without rewriting dated evidence.

Ceiling: **400 lines per document** (the project's own limit for `.cs` files, applied here for the
same reason — a document past it is no longer read in one sitting). Any exception is argued in this
file.

## 2. House style

Every authoring agent follows this. It is also the text that goes into `backend/index.md` so future
`trellis-update-spec` runs keep the shape.

1. **One topic per document.** The filename says the topic; the first paragraph says the scope and
   when to read it.
2. **A rule is one bullet, imperative, with its reason compressed to a clause.** The reason stays
   when it prevents a rediscovery (the incident behind it, the measurement that set a threshold, the
   hardware that verified it) and goes when it is narration.
3. **The seven-section contract block (`Scope / Trigger`, `Signatures`, `Contracts`,
   `Validation & Error Matrix`, `Good/Base/Bad Cases`, `Tests Required`, `Wrong vs Correct`) is for
   cross-layer and infrastructure contracts**, per `trellis-update-spec`. It is not a per-task
   ritual: a convention, a naming rule or a lifecycle rule states its contract as prose or a table,
   and only the sections that carry information are written. A block whose only content is its
   heading is deleted.
4. **Provenance is compressed, not deleted.** `(wired 2026-09-20, task 09-20-transport-lifecycle)`
   becomes `(2026-09-20)` or `(task 09-20-transport-lifecycle)` where the date or the task is what
   justifies the rule; a changelog line that records only that something changed is deleted.
5. **Anything that can rot silently keeps an anchor to the code that owns it** — a threshold, a
   default, an allowlist, an allowed-exception list names its owning type or file. A number with no
   owner is a bug waiting to happen.
6. **No duplication, inside or across documents.** A rule lives once; other documents link to it.
   Where two documents must both state a fact (a shared threshold), one owns it and the other
   repeats it in a half-line with a link.
7. **Code examples are real.** Every snippet, symbol, path and event name must exist in the tree at
   the time of writing. No invented examples, no `Foo`/`Bar`.
8. **English**, per `backend/index.md`. A non-English fragment survives only when it names a real
   artifact (a log field, a section title cited from code).
9. **No "the previous design" narratives.** A superseded design is mentioned only where the reason
   for its rejection is a live constraint; the retelling is cut.

## 3. Cross-reference policy

Live code cites the library by section title and, in a few places, by number (§2.9, `#3`). Dated
evidence under `benchmarks/results/**` cites numbers and line ranges and must never be rewritten.

Therefore:

1. A section cited by live code keeps its **title**, wherever it lands; if the title must change, the
   citing comment is updated in the same change.
2. Numbered citations in live code (`hot-path.md #3`, `test-stability.md` §2.9) are converted to the
   section title at the same time — a number is not a stable identifier across a split.
3. Numbered citations in `benchmarks/results/**` are left alone; the hub's "Where things moved" table
   is what makes them resolvable.
4. The sweep for (1) and (2) is mechanical: `rg -n 'hot-path\.md|udp-relay\.md|...' src tests benchmarks
   -g '!benchmarks/results/**'`.

## 4. Execution

Work is split one document per agent, in waves:

- **Wave 1 (analysis, read-only)** — one agent per oversized document: verify claims against the
  tree, list stale claims with `file:line`, list verbosity cuts with line ranges, propose the family.
  Reports land in `research/plan-<doc>.md`.
- **Wave 2 (revision)** — one agent per document (or family): apply the plan, write the new files,
  update the hub, update citing comments, run `research/verify-specs.py --doc <doc>` to green.
- **Wave 3 (integration, lead)** — `backend/index.md`, `guides/index.md`, the authoring rules, the
  whole-library verification run, and the final read of every hub.

A revision agent's work is not done until `verify-specs.py` is green for its document and no rule was
lost: the agent reports rules removed on purpose, each with its reason.

## 5. Verification

`research/verify-specs.py` is the mechanical part of AC2/AC3/AC4/AC6: document size, index coverage,
link resolution, unresolved code identifiers (against a corpus of the whole tree, minus an allowlist
of non-repo names), and unexplained CJK.

The remaining criteria are judged, not scripted:

- **AC1** — each revised document reports its stale claims with the `file:line` that settles them;
  the lead spot-checks.
- **AC2** — the trim is only legitimate if no rule, threshold or evidence pointer was lost; the lead
  diffs the before/after rule inventory of each family.
- **AC5** — the comment sweep is a script run plus a read of the diff.
- **AC7** — `dotnet format ... --verify-no-changes` (comments touched by AC5) and a Release build.

## 6. Split plans

The five oversized documents became families; every child is under 400 lines and every hub carries a
move table.

| Family (was) | Hub | Children | Words |
|---|---|---|---|
| `hot-path.md` (1655) | 135 | `warm-path-dispatch`, `packet-shape-and-flow-identity`, `native-lease-and-pool-lifetime`, `allocation-gates`, `allocation-gate-host-lumps`, `udp-datagram-path`, `relay-pump-and-checksums`, `benchmark-methodology` | 17 918 → 14 854 (−17.1 %) |
| `udp-relay.md` (1029) | 136 | `udp-response-reinjection`, `udp-relay-transport`, `udp-flow-setup`, `udp-session-lifecycle`, `udp-association-ownership`, `udp-over-tcp` | 11 173 → 9 878 (−11.6 %) |
| `tcp-local-redirect.md` (470) | 117 | `tcp-redirect-transform`, `tcp-client-close-injection`, `tcp-syn-setup-admission`, `tcp-redirect-teardown-grace`, `tcp-relay-lifecycle` | 7 218 → 9 086 (+25.9 %) |
| `windows-ndisapi.md` (566) | 177 | `ndis-capture-refresh`, `ndis-batched-capture`, `ndis-batched-send` | 9 250 → 8 458 (−8.6 %) |
| `measurement-harness.md` (607) | 69 | `measurement-record-contract`, `measurement-run-lifecycle`, `measurement-lane-seam`, `measurement-judgement`, `measurement-udp-census`, `measurement-tooling` | 5 990 → 6 933 (+15.7 %) |

Two families grew. Both grew by gaining a hub written from scratch (1 345 and ~500 words) while their
carried-over content rose about 7 %: the added words buy corrected facts and precise file/type
references, which is the point of the revision. Trimming is visible where the old text was verbosity
rather than scaffolding — `hot-path` alone deleted ~310 lines of `### 4/5/6/7` sections that restated
`### 3`.

Documents revised in place: `error-handling.md` (−47.9 % words, after the domain documents took over
the mechanism detail), `async-lifetime.md` (−11.8 %), `logging-guidelines.md` (−10.9 %),
`traffic-policy-lifecycle.md` (−7.1 %), `test-stability.md` (−2.5 %), `quality-guidelines.md`
(+3.8 %, one rule per line where a row used to hold five), `directory-structure.md` (+127 %, Chinese
prose translated to English and the project graph corrected).

Two documents are new: `idle-expiry-sweep.md` (split out of `traffic-policy-lifecycle.md`, 1 067
words) and the rewritten `backend/index.md` (+1 022 words: the 45-document inventory and the authoring
rules that keep the library from re-bloating).

Net across the library: 72 510 → 71 297 words (−1.7 %), 552 602 → 538 113 bytes (−2.6 %), while the
longest line fell from 2 475 B to 487 B and the count of lines over 400 B fell from 164 to 13.
