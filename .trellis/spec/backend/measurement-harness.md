# Measurement Harness Contract (benchmarks/WinForward.E2E)

> Scope: the end-to-end measurement harness — the records it publishes, the plans it loads, the lane
> seam its arms run behind, how two runs are compared, and the tools that gate it. Read this family
> when you add, rename or remove a published field, change what a plan may declare, touch a JSONL
> writer or a teardown path, or judge whether a harness change moved the numbers.

## One definition point

Field names live in `benchmarks/WinForward.E2E.Contracts` (a public project). Both binaries reference
it — the harness (`benchmarks/WinForward.E2E/WinForward.E2E.csproj:11`) and the analyzer
(`benchmarks/WinForward.E2E.Analysis/WinForward.E2E.Analysis.csproj:10`) — so a rename is a compile
error on both sides, not a silently empty table cell. Every rule in this family hangs off that one
invariant.

## Read these four rules first

1. **One spelling per published key, owned by `Contracts`.** A writer that spells a key as a string
   literal stops being renamed with the contract it belongs to, which is why `JsonKeyLiteralGateTests`
   scans the writers. → [the record contract](./measurement-record-contract.md)
2. **Teardown books no data point.** A `catch (ObjectDisposedException)` is either empty or books the
   explicit non-observation its site publishes; a counter, a verdict or an observation written there
   is a fabricated measurement and fails `ObjectDisposedCatchGateTests`.
   → [the run lifecycle](./measurement-run-lifecycle.md)
3. **`achievedRate` means requests successfully sent per elapsed second**, in every arm; the
   completion caliber is published under its own name, never this one.
   → [judging a run](./measurement-judgement.md)
4. **A key the README documents is gated, not trusted.** The analysis resolves most paths by arm kind,
   so a misspelt key renders an empty cell and the report still builds.
   → [the tooling](./measurement-tooling.md)

## Topic map

| Document | The question it answers |
|---|---|
| [measurement-record-contract.md](./measurement-record-contract.md) | How is a key spelled, written, published and loaded? |
| [measurement-run-lifecycle.md](./measurement-run-lifecycle.md) | What may a run publish when it fails or is torn down? |
| [measurement-lane-seam.md](./measurement-lane-seam.md) | Who owns each counter between the engine, the policy and the transport? |
| [measurement-judgement.md](./measurement-judgement.md) | Did a change move the numbers, and what does each rate name mean? |
| [measurement-udp-census.md](./measurement-udp-census.md) | What do `udpSummary.sources[]` and `sourceOverflow` count? |
| [measurement-tooling.md](./measurement-tooling.md) | Which script or gate checks what, and what does the analyzer emit? |

## Where things moved

The 607-line single document was split at the section boundaries below. This table is also what keeps
the numbered citations outside the family resolvable: `backend/index.md` cites §3.9 and §3.10 by
number today, and `guides/index.md` cites §3.8.

| Old section | Where it went |
|---|---|
| §1 Scope / Trigger | the scope line above |
| §2 Signatures | the write path in the record contract; the script inventory in the tooling |
| §3.1–§3.4, §3.7 | [record contract](./measurement-record-contract.md) |
| §3.5, §3.8, §3.10 | [judgement](./measurement-judgement.md) |
| §3.6 | [lane seam](./measurement-lane-seam.md) |
| §3.9 | [run lifecycle](./measurement-run-lifecycle.md) |
| §3.11 | [tooling](./measurement-tooling.md) |
| §3.12 | [UDP census](./measurement-udp-census.md) |
| §4 Validation & Error Matrix | plan rows → record contract; run rows → run lifecycle; the rename-table row → judgement; the gate rows → tooling |
| §5 Good / Base / Bad cases | split the same way as §4 |
| §6 Tests Required | each child names the tests that hold its own rules |
| §7 Wrong vs Correct | record contract, run lifecycle, tooling |
| §8 The analyzer | [tooling](./measurement-tooling.md) |

The analyzer's own artifacts — `tables.md`, `verdict.json` and `<out>/plots/SKIPPED.md` — are live
outputs, not history: they are written unconditionally by
`benchmarks/WinForward.E2E.Analysis/Cli/AnalysisRunner.cs:38-48`. `analyze.py`, the Python reference
they were once compared against, is retired and survives only as frozen output
(`benchmarks/WinForward.E2E.Analysis/verification/FROZEN.md:8`).
