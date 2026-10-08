# E4-b4 — §4 headline, §6 cpu, §7 memory, §10 persist, §11 tcp, and the six metrics

Batch E4-b4 of `.trellis/tasks/10-07-e2e-e4-analyzer`: the five sections the batch table assigns to
§4/§6/§7/§10/§11 plus the six `metrics/*` members `BATCH_SECTIONS` splits off to batch 4. Authority:
`design-decisions.md` **D21** (semantic comparison replaces byte equality; the wording is still
required to match), **D21.1** (the tolerance direction), **D21.2** (the two registrations that are
E4-c's, not this batch's), **D20.2** (slicing and batch ownership), **D20.8 #2** (§4 is batch 4 because
it prints all twenty-one metric columns) and
`.trellis/tasks/10-07-e2e-harness-refactor/research/python-oracle-changes.md` §5 (the rendering quirks
that must survive unchanged).

Its criterion is `python3 benchmarks/WinForward.E2E/scripts/oracle-diff.py --mode semantic --batch 4`
⇒ **rc=0**, with `--batch 1a/1b/1c/2/3` still **rc=0** and `--batch 5` still **rc=2**.

**Result: batch 4 rc=0 (11/11 slices), batch 1a/1b/1c/2/3 rc=0 (7/2/3/4/18 slices), batch 5 rc=2
(3 missing slices).** `--mode semantic --tolerance 0 --batch 4` and `--mode byte --batch 4` are **both
rc=0 as well**, and so are byte-mode `1a/1b/1c/3`. The frozen tree, the frozen golden and the reference
`analyze.py` were not touched (`git status --short benchmarks/WinForward.E2E.Analysis/verification`
prints nothing).

## 1. What the batch is, and what it deliberately is not

- **§4 `Headline matrix`** — one column per metric for all twenty-one, one row per measured program,
  plus the pass-count table behind every cell. It is the only section that prints every metric, which
  is why it is a batch of its own: by the time it is compared, the batch that renders the other fifteen
  columns has already passed.
- **§6 `CPU detail`** — one line per (row, arm): the proxy and generator share of one vCPU, the
  `%machine` and headroom columns, the two per-work figures, and the reading's own shape (identities
  used, restarts, rejected samples, the denominator names). It carries the `#19` CPU-scope disclosure.
- **§7 `Memory detail`** — one line per row: the steady-state private-bytes and working-set quantiles,
  the peak, and two OLS leak slopes (all arms, and `MIX` alone) with their confidence intervals.
- **§10 `Long-lived connection detail`** — the `PERSIST` arm's requests, responses, rate, reconnects,
  the per-pass `survivedIdle` facts, the idle window as scheduled and as observed, the round trips
  either side of it, and the one-sentence verdict.
- **§11 `TCP reliability detail`** — the `REL` arm's seven outcome marginals, the two surprise rates,
  the attempt counts, the in-flight check, and the two mean durations.
- **The six metrics** — `rel.unexpectedEofRate`, `rel.fidelityRate`, `persist.responseRate`,
  `persist.reconnects`, `mem.privateBytes.p50` and `cpu.proxy.vcpuPct`: each one's per-row per-pass
  values, medians and IQRs, and the bootstrap comparison of every pair of rows that can be compared.

What it deliberately does **not** do: §12/§13/§14 and `verdict.json`'s
`control_blocks`/`dual_phase`/`ledger` (batch 5), the truncation caveat (§14.7, which is E4-c), the
`--zero-denominator` edge flag and the empty-cell unit tests E4-c owes (D21.2 #2), and the byte-mode
§3 blank-line fix (D21.2 #1). No rule the reference does not have was added.

## 2. The criterion

Command and output on the final tree (`--raw /tmp/wf-synth/raw`, the tree the differ extracts from
`verification/synthetic-tree.tar.gz`):

| batch | semantic | semantic `--tolerance 0` | byte | slices |
|---|---|---|---|---|
| `1a` | **0** | – | **0** | 7 |
| `1b` | **0** | – | **0** | 2 |
| `1c` | **0** | – | **0** | 3 |
| `2` | **0** | – | 1 (pre-existing, §3 blank line) | 4 |
| `3` | **0** | – | **0** | 18 |
| `4` | **0** | **0** | **0** | 11 |
| `5` | 2 (3 missing) | – | 2 | 6 |

```text
-- batch 4
   tables.md:4: equal
   tables.md:6: equal
   tables.md:7: equal
   tables.md:10: equal
   tables.md:11: equal
   verdict.json:metrics/rel.unexpectedEofRate: equal
   verdict.json:metrics/rel.fidelityRate: equal
   verdict.json:metrics/persist.responseRate: equal
   verdict.json:metrics/persist.reconnects: equal
   verdict.json:metrics/mem.privateBytes.p50: equal
   verdict.json:metrics/cpu.proxy.vcpuPct: equal
compared 11 slice(s): 0 differ(ent), 0 missing
differences: 0 structure, 0 value, 0 missing
rc=0: every slice of this batch is equal
```

**`--tolerance 0` is rc=0, and byte mode is rc=0.** Every number in the eleven slices — the per-identity
CPU percentages, the MiB medians, the OLS slopes and their interval ends, the rule-of-three bounds, and
all six metrics' bootstrap estimates, intervals and p-values — is reproduced **exactly**, not merely
inside one unit of the reference's printed last digit. Two details of the reference had to be
reproduced for that, and both are recorded in §7: CPython's `sum` is **Neumaier-compensated**
(`DescriptiveStats.Sum`), and the least-squares sums run over that same compensated sum rather than
over a plain accumulator.

Byte mode's batch 2 result is pre-existing and not this batch's: §3 has one extra blank line before
`### 3.2` (D21.2 #1, registered for E4-c). Byte-mode `1a/1b/1c/3/4` are all rc=0, so this batch added
no byte-mode difference of its own anywhere.

## 3. The module map

Every file carries the reference function beside it. Effective lines are
`benchmarks/WinForward.E2E/scripts/effective-lines.py`'s own count.

| file | effective lines | what it carries |
|---|---|---|
| `Tables/TableHeadline.cs` | 93 | `table_headline` — §4, both tables |
| `Tables/TableCpu.cs` | 221 | `table_cpu` — §6, the scope disclosure and the denominator note |
| `Tables/TableMemory.cs` | 198 | `table_memory` — §7, both leak fits |
| `Tables/TablePersist.cs` | 158 | `table_persist` + `_arm_stat` + `_arm_latency_stat` — §10 |
| `Tables/TableTcp.cs` | 181 | `table_tcp` — §11 |
| `Tables/ArmDenominatorTable.cs` | 70 | `arm_denominators` — the per-arm kind → denominator table |
| `Stats/CpuDetail.cs` | 143 | `cpu_detail` + `_identity_points`, with the diagnostics §6 prints |
| `Stats/OlsSlope.cs` | 73 | `ols_slope` + `t_critical_95` + the verdict sentences |
| `Metrics/MetricCatalogue.cs` | 195 (was 125) | `+` the six batch-4 specs, their definitions, `metric_memory_private_p50`, `metric_cpu_proxy_vcpu`, the spec-based `cell_text` |
| `Metrics/MetricSpec.cs` | 32 | `+ Digits` (the headline matrix's own rounding) |
| `Model/RunSamples.cs` | 192 (was 83; 190 before the check pass) | `+` `arm_samples`, `primary_product_process`, `process_private_bytes`, `present_product_samples`, `steady_samples`, `sample_identities`, `IsProcess`, `Mebibyte` |
| `Model/ArmAccess.cs` | 121 (was 78) | `+ arm_flag`, `+ arm_ratio` |
| `Model/ArmRecords.cs` | 68 | `+ Kind` (result first, armSummary only when there is no result) |
| `Model/RunClocks.cs` | 85 | `+ arm_start_ticks` |
| `Stats/DescriptiveStats.cs` | 129 | `+ Sum` (CPython's compensated `sum`) |
| `Tables/TablesWriter.cs` | 87 | §4/§6/§7/§10/§11 wired to their bodies |

The project was 69 files and 8631 effective lines by that counter when this section was written, and
is 69 files and 8633 after the check pass (§10); the largest is
`Tables/TableEnvironment.cs` at 321, and the largest this batch wrote is `TableCpu.cs` at 221 — every
one under the 400-line rule (`effective-lines.py` over the four paths is silent, §8).

Four implementation notes, because each is a rule rather than a port:

1. **The metric family is the tested pairs, not the candidate pairs** (b3 §3.1) — unchanged, and batch
   4 inherits it: the six new metrics go through the same `Candidates`/`HolmAdjust` path.
2. **`arm_denominators` is keyed on the arm's `kind`, not its name.** `ArmRecords.Kind` reads the
   `result` record and falls back to `armSummary` only when there is **no** result — an arm that wrote a
   result carrying no `kind` is not silently re-described by its header. A kind this table does not know
   yields four nulls, which renders `n/a (no denominator)` rather than guessing.
3. **A row that cannot carry UDP gets no datagram denominator at all.** `RowProfiles.UdpIncapableRows`
   nulls the UDP side of the latency, DNS and MIX denominators, so the column reads
   `n/a (no datagram denominator)` instead of dividing by a datagram count the row never produced.
   `proxifier`'s whole §6 datagram column is that case, and the `proxifier` mutant tree of batch 3
   covers it.
4. **`proxy %machine` and `headroom` share one guard.** Both divide by `run.json`'s
   `logicalProcessors` and both are dropped when it is missing, null **or zero** — the
   `logical-processors-null` mutant exercises the missing and the null spelling in two passes of one
   row and both sides agree.

## 4. §4 and the twenty-one columns

`MetricSpec.Digits` came back **with its consumer**, which is the rule b1a's batch set and b3 §8.7
recorded: every metric now carries the digit count §4 rounds it with (`1` for the `us` percentiles and
the DNS round trips, `4` for most rates, `0` for the LOSS foreign-connection count, `3` for goodput,
`2` for the MiB and `%vcpu` metrics), and the spec-based `MetricCatalogue.CellText(cell, spec)` is the
overload that reads it. §9's answer-rate cell keeps passing `digits: 4, unit: "pp"` where it is used,
which is the same rendering path.

The wiring is one assertion short of trivial, and that is deliberate: §4 renders its cells through
**the same** `PerPassValues` and **the same** `CellText` that `verdict.json`'s `metrics` object and the
other sections use, so the matrix and the verdict cannot disagree about a metric's unit, scale or
status. The two `Pending` remarks on `MetricCatalogue` were updated rather than deleted: the constant
now names batch 5's keys, and the six extractors are in `Specs` where the differ can find them as
existing slices rather than as missing ones.

The six members are inserted at their reference positions, not appended: `rel.*` sits between
`loss.foreignConnection` and `dns.answerRate`, which is what the column order of the frozen golden
says, and the first run of the differ caught exactly that (`tables.md:4.table1[…].REL fidelityRate`
reading the `DNSALT dns-rtt` column). Appending them would have compared every column against its
neighbour and still "passed" nothing.

## 5. The mutation trees

Ten mutants, each one edit on a fresh extraction of the frozen tarball at the hardcoded `/tmp/wf-synth`.
Both implementations are run on the mutated tree (`analyze.py --raw /tmp/wf-synth/raw` and
`WinForward.E2E.Analysis --raw /tmp/wf-synth/raw`) and the batch-4 slices are compared **byte for byte**
by the differ itself: `oracle-diff.py --mode byte --batch 4 --golden <mutant ref> --cs-out <mutant cs>`.
Each mutant is also compared against the **frozen golden**, which is how "this mutation moved the
compared surface at all" is told from "this mutation was vacuous".

Recipe: `/tmp/e4b4/run_mutants.py` (extract → mutate → run both → differ twice); verification of each
mutation's shape against the reference's own output: `/tmp/e4b4/verify_mutants.py`, summary
`/tmp/e4b4/mutants/summary.tsv`. **Both suites were re-run against the final build** after the gates'
last refactors, and the table below is that run.

| mutant | the edit | ref vs C# | vs frozen golden |
|---|---|---|---|
| `cpu-samples-missing` | wf-aot-opt's LAT product samples in pass2 lose their `cpuSeconds` and `privateBytes` | **rc=0** | rc=1 |
| `process-restart` | wf-aot-opt's LATLOAD product is a second `(pid, startUtc)` identity in pass1's second half | **rc=0** | rc=1 |
| `memory-counter-wrap` | proxifyre's MIX has one `readError` tick per pass, and pass2's private bytes wrap back down | **rc=0** | rc=1 |
| `persist-all-answered` | proxifier's PERSIST answers every request, never reconnects, holds the idle gap | **rc=0** | rc=1 |
| `persist-none-answered` | proxybridge's PERSIST answers nothing, breaks the idle connection, reconnects three times | **rc=0** | rc=1 |
| `tcp-histogram-empty` | proxifyre's PERSIST loses its `tcp-rtt` histogram in all three passes | **rc=0** | rc=1 |
| `null-metrics` | three batch-4 fields come back JSON `null` in one pass, and one rate is `null` in every pass of a row | **rc=0** | rc=1 |
| `logical-processors-null` | proxifyre's `run.json` reports a null processor count in pass2 and none in pass3 | **rc=0** | rc=1 |
| `rel-in-flight` | proxifier's REL publishes no schedule in pass2 and ends pass3 with work in flight | **rc=0** | rc=1 |
| `rel-zero-attempts` | proxybridge's REL pass1 attempted nothing, so a rate's denominator is exactly zero | **rc=0** | rc=1 |

**Ten of ten byte-equal, and every one moved the batch-4 surface.** Each mutant was also verified to
have moved the campaign *in the shape it claims*, reading the reference's own output
(`/tmp/e4b4/mutants/verify.log`, one assertion per mutant over §4/§6/§7/§10/§11 and the metric slices):

- `cpu-samples-missing` → §6's wf-aot-opt LAT proxy cell is `47.33 [47.28–47.37] % (n=2)` while its
  generator cell keeps `(n=3)`: the product's counter is gone, the sampler's is not.
- `process-restart` → §6's LATLOAD line reads `1/1, 2/2` in the identities column and `1` in restarts.
- `memory-counter-wrap` → §7's proxifyre `steady samples/pass` is `209 [209–209] (n=3)`: the unreadable
  tick is rejected in every pass rather than counted as a zero. The wrapped pass drags the MIX slope to
  `0.234 [-65.205–0.234] MiB/min (n=3)`.
- `persist-all-answered` → §10's proxifier verdict flips to `held the connection across the idle gap`
  with `pass1: yes; pass2: yes; pass3: yes`.
- `persist-none-answered` → §10's proxybridge verdict is
  `BROKE THE IDLE CONNECTION in pass1, pass2, pass3; reconnects>0`, and its rate is a **real zero**
  (`0.0 [0.0–0.0] % (n=3)`), not a rule-of-three bound: §10 prints no bound for this column.
- `tcp-histogram-empty` → §10's `tcp-rtt p50` and `p99` are both `n/a` (the histogram nobody published
  is a different category from a row that does not carry the arm).
- `null-metrics` → §4's wf-fdd-opt REL rate reads `(n=2 of 3; 1 null)`, §4's and §10's wf-aot-opt
  response rate cells are **empty** (a rate whose denominator was zero, not a zero), and §11's
  wf-fdd-opt `passes` drops to `2`.
- `logical-processors-null` → §6's proxy `%machine` and `headroom` cells are `(n=1)` for the row: one
  pass published the count, one published it as null and one did not publish it, and all three are the
  same answer.
- `rel-in-flight` → §11's in-flight cell carries both halves,
  `pass2: n/a (REL metrics.scheduledAttempts missing); pass3: connectAttempts 150 != scheduledAttempts 153, work in flight at teardown`,
  and `scheduledAttempts` aggregates `(n=2)`.
- `rel-zero-attempts` → §11's `passes` is `3` (the pass ran) while every outcome rate is `(n=2)`, and
  `rel.unexpectedEofRate`'s `unavailable_passes` is `{"pass1": "REL metrics.connectAttempts is zero"}`.

**No mutant made the reference crash or become unreachable**, so no D20.1 exception was needed. The
reference ran to completion on every one of the ten trees.

## 6. Negative controls

Ten controls, each one edit on a copy of the **clean produced** directory, compared with
`oracle-diff.py --mode semantic --batch 4 --cs-out <copy>` (`/tmp/e4b4/negative_controls.py`). Every one
is red where it must be; the base copy is green.

| control | edit | rc | what the differ names |
|---|---|---|---|
| `headline-header` | §4's `LAT tcp-rtt p50 (us)` → `(ms)` | 1 | `[structure] tables.md:4.table1.header` |
| `headline-cells` | one cell dropped from a twenty-two-cell §4 row | 1 | `[structure] tables.md:4.table1[row=wf-aot-opt].cells: expected '22 cell(s)' vs actual '21 cell(s)'` |
| `headline-reason-to-number` | §4's `n/a (reason)` cell → `0` | 1 | `[structure] …[row=control-pre].LAT tcp-rtt p50 (us): expected 'n/a (reason): …' vs actual 'number: 0'` |
| `headline-marker-to-number` | §4's `not carried (UDP bypassed)` → `0` | 1 | `[structure] …[row=proxifier].LAT udp-rtt p50 (us): expected 'text: not carried (UDP bypassed)' vs actual 'number: 0'` |
| `cpu-reason-to-number` | §6's `n/a (no product process was sampled)` → `0` | 1 | `[structure] …[row=control-pre&arm=BASE].proxy %vCPU: expected 'n/a (reason): …' vs actual 'number: 0'` |
| `memory-value` | §7's all-arms slope `0.034` → `0.4` | 1 | `[value] tables.md:7.table1[row=wf-aot-opt].slope all arms (MiB/min)` |
| `tcp-number-to-marker` | §11's `scheduledAttempts` number → `n/a (not published)` | 1 | `[structure] …[row=wf-aot-opt].scheduledAttempts: expected 'number: 150.0 …' vs actual 'n/a (reason): …'` |
| `persist-number` | §10's `responseRate` median `100.0` → `96.6667` | 1 | `[value] tables.md:10.table1[row=wf-aot-opt].responseRate` |
| `metric-number` | a batch-4 metric's per-row median `100.0` → `50.0` | 1 | `[value] verdict.json:metrics/persist.responseRate.rows.proxybridge.median` |
| `metric-missing-member` | a batch-4 metric's `threshold` member deleted | **2** | `[missing] verdict.json:metrics/cpu.proxy.vcpuPct.threshold: expected '{3 key(s)}' vs actual <absent>` |

Five structural controls cover the four category directions the task names — header, cell count,
`n/a` ↔ number, marker ↔ number — across §4 and §6, and one of them is the **empty cell's own**
direction in reverse (the "number → reason" control above is exactly the shape the `null-metrics`
mutant produces). The three value controls show the tolerance is not swallowing a slope, a percentage
rate or a metric median; the missing-member control shows a deleted JSON member is a missing slice
(rc=2), not a tolerated value.

The empty cell itself cannot be a *control* on the clean tree — the frozen golden has none, which is
why `python-oracle-changes.md` §5.3 records that rule — so it is held by the `null-metrics` mutant
instead: there both implementations produce `''` for `wf-aot-opt`'s response rate in §4 and §10, and the
differ's category for it is `empty`, not `number: 0.0`.

## 7. Deviations and observations

**7.1 CPython's `sum` is compensated, and the port had to say so.** CPython 3.12+ accumulates floats
with Neumaier compensation (`sum([1e16, 1.0, -1e16]) == 1.0`, and the same holds for the generator and
list comprehensions the slopes are built from), which a plain `total += value` loop does not reproduce
(`0.0`). A least-squares slope is a difference of large sums, so §7's slope cells and their interval
ends depend on it; `DescriptiveStats.Sum` is that algorithm and `OlsSlope` runs its three sums through
it. `CpuDetail` and `RunSamples.ProcessPrivateBytes` keep a plain loop where the reference keeps a plain
loop (`total += delta`, `total += value`), so the two kinds of sum are both faithful, while §6's
per-transaction numerator (`CpuDetail.IdentityCpuSeconds`) runs the reference's `sum(...)` over the
per-identity deltas and is compensated like it. This is registered as an implementation note rather
than a deviation: on the frozen tree nothing about the output changes.

> **Corrected by the check pass.** The claim that `--tolerance 0` "is what proves it was needed" does
> not hold: an analyzer built with a **plain** `DescriptiveStats.Sum` and nothing else changed produces
> **byte-identical** output on the frozen tree, so all three modes of `--batch 4` stay green with the
> compensation removed. The compensation is a faithfulness rule, and what proves it is a *divergence
> tree*, not the frozen one. Two were built for the check (both: realized analyzer byte-equal to the
> reference, naive-sum analyzer rc=1 in byte and semantic mode):
>
> * `sum-divergence-memory` — `proxifier`'s per-process `privateBytes` × 1e14 in every pass. Naive
>   `tables.md:7` slopes: `26766145717514.477 / -7863985997626.710 / 3398878714021.590`; compensated:
>   `…14.461 / …26.704 / …21.592` — the median cell differs at its printed third decimal.
> * `sum-divergence-cpu` — `wf-aot-opt` pass 1 `LAT` product identity deltas `(1e16, 1, 1)`. The naive
>   §6 `CPU ms / 1000 tx` prints `66666666666666663936.0000`, the compensated one
>   `66666666666666680320.0000`.

**7.2 `MetricCatalogue.Pending` now names batch 5.** It is printed on stdout only (`stdout is not part
of the contract`) and said "4 …" while batch 4 was outstanding, exactly as b3 §8.3 recorded.

**7.3 `MetricSpec.Digits` and the spec-based `CellText` came back, as b3 §8.7 said they would.** b3
removed both because nothing read them; §4 is the consumer, so they are back in the same commit as
their reader rather than ahead of it.

**7.4 `RunSamples.IsProcess` is a new seam that b3 did not need.** The rejected-sample count and the
primary-process filter both compare a sample's `process` member against a name, and Python's `==`
against a *string* is false for a sample that matched nothing (its member is absent, i.e. `None`). A
`ProcessName(sample) == primary` comparison would have called two absences equal when the primary name
is the empty string. The helper is the faithful comparison, and `ProcessName` stays for the ranking.

**7.5 `DescriptiveStats.SummariseVerdicts` is reused, not duplicated.** §7's two slope-verdict columns
are the same "distinct verdicts, sorted, joined with `; `, or `mixed across passes: …`" rendering the
member already published, so batch 4 adds no second copy of it.

**7.6 §6's `ProxyCpu` metric takes the row alone.** Python's `metric_cpu_proxy_vcpu(ctx, row)` takes the
context and never reads it; the C# extraction is registered as `(_, row) => ProxyCpu(row)` so the
unused parameter is not a member of the method. `metric_memory_private_p50` genuinely needs the
campaign (the warmup window) and takes it.

**7.7 The formatter rewrote the long prose blocks' alignment, once.** The reference's paragraphs were
injected mechanically (`/tmp/e4b4/inject_prose.py`, which lifts each paragraph out of `analyze.py` by
AST rather than retyping it, because `--mode byte` compares them byte for byte). The first injection
left the continuations mis-aligned; `dotnet format` normalised them and the re-run of
`--verify-no-changes` is empty. No literal's *content* moved — the differ's byte-mode rc=0 is what
proves that.

## 8. The gates

All six, on the final tree, in the repository's own order (`/tmp/e4b4/gates/`):

| gate | command | result |
|---|---|---|
| build | `dotnet build WinForward.slnx -c Release` | **0 warnings, 0 errors** (`build.log`) |
| tests | `dotnet test WinForward.slnx -c Release -m:1` | **1655 passed, 0 failed, 0 skipped** in 14 assemblies, rc=0 (`test.log`) |
| format | `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` | **0 bytes of output**, rc=0 (`format-final.log`) |
| inspector | `jb inspectcode -f=Xml -e=HINT -o=… WinForward.slnx`, on a **cleared** `InspectCode`/`Daemon`/`Transient` cache | **`<Issue>` 0**, no `CSharpErrors` (`inspectcode-cold.xml`) |
| lines | `effective-lines.py` over the four paths | **0 bytes of output**, rc=0 (largest 321; project 69 files / 8631 effective lines) |
| oracle | `--mode semantic --batch 1a/1b/1c/2/3/4` (+`--tolerance 0`), `--mode byte --batch 1a/1b/1c/3/4` | rc=0/0/0/0/0/**0** (**0** at tolerance 0, **0** in byte mode); `5` rc=2 |

The first format run reported **25 diagnostics** in this batch's new files and every one was real: six
whitespace alignments the prose injection had left wrong, one imports ordering, one local function
whose name did not begin with a capital (`rate`), two parameters that could be `List<>` rather than
`IReadOnlyList<>`, one `string.Create` whose every hole was culture-invariant, twelve missing named
arguments in two record constructors, one `<c>null</c>` that wanted `<see langword="null"/>`, one
local that could be `const`, and one LINQ call that had a `List<>`-shaped optimisation. Each was fixed
rather than suppressed.

The cold inspector round then reported **8 findings**, again all in this batch's new files and all real:
two `using` directives the last refactors had left redundant (`System.Text.Json` in
`MetricCatalogue` — it is a global using — and `Globalization` in `TablePersist`), five `CpuDetail`
constants that only its own method default read, and one `if`/`return` that wanted to be a `return`
conditional. All eight were fixed and the **second cold run reports `<Issue>` 0**. The two
`#pragma warning disable S1244` sites are the exact-zero tests the reference itself makes (`sxx == 0.0`
in `OlsSlope`, `total != 0.0` in `CpuDetail`, `denominator.Value == 0.0` in `ArmAccess.Ratio`), each
carrying its reason on the pragma and matching the precedent `RunClocks.TickFrequency` set in b1a.
Three sites rather than two: the check pass found `ArmAccess.Ratio`'s copy, added by this batch's
`arm_ratio` port, left out of this paragraph.

## 9. For check

1. **`--tolerance 0` is the regression to keep.** §2 shows batch 4 is rc=0 at tolerance zero, so if a
   later change turns it red the cause is an arithmetic change, not a formatting question. §7.1 names
   the two sums that make it work — but see the correction there: on the frozen tree the compensated
   and the naive sum print the same bytes, so this mode is *not* what pins the compensation down.
2. **The six metrics are in `Specs` at their reference positions** (§4). Confirm the `rel.*` pair sits
   between `loss.foreignConnection` and `dns.answerRate`, and that §4's second table prints the
   `coverage` cell (`K of 21 metrics have >= 2 passes`) from the same pass counts the first table's
   cells came from — the two tables must not be computed twice.
3. **`arm_denominators`' kind table** (`Tables/ArmDenominatorTable.cs`, §3.2): the `_ => new(…)` arm is
   the "kind nobody declared" case and renders `n/a (no denominator)`. Check that `ArmRecords.Kind`
   really prefers the result over the summary — a run whose result carries no `kind` must **not** fall
   through to the header.
4. **The §6 scope disclosure is byte-identical to the reference's** (§1) and is a paragraph of the
   section, not a note in a different file. `#19`'s requirement is that the C# side render it
   equivalently; the differ compares it as prose, and byte mode compares it as text.
5. **The empty cell is held by a mutant, not by a control** (§6, §5's `null-metrics`): the frozen tree
   has no empty cell in the compared surface, and D21.2 #2 assigns the `--zero-denominator` flag and the
   unit tests for "empty cell" and "MIX arm-level fallback" to **E4-c**. This batch reaches both rules
   through the `null-metrics` and `mix-class-missing` mutants and claims nothing more.

## 10. Independent check (`trellis-check`)

A second pass rebuilt the batch from scratch: its own criterion sweep, its own thirteen mutation trees,
its own nine produced-output controls, a `--mode byte` re-run of the ten stored mutant pairs under
`/tmp/e4b4/mutants`, and an analyzer built with a **plain** `DescriptiveStats.Sum` to test §7.1's claim.
Everything the batch claims held. Five things changed, all of them by this pass and all of them inside
the batch's own files:

1. **`RunSamples.IdentityPart` no longer collapses JSON kinds** (`Model/RunSamples.cs`). A summed CPU
   counter's identity is `(pid, startUtc)`, and the port encoded each component as a bare string, so a
   numeric `1203` and a string `"1203"` produced one key where the reference's tuple key keeps two. The
   check built the diverging tree (`string-pid-versus-number-pid`: `wf-aot-opt`'s pass 1 `LAT` product
   samples carry the pid as a number in the arm's first half and as its string form in the second) and
   the **unfixed** tree was rc=1 in both modes — the reference printed `1/1, 2/2` identities and a
   different §4 `proxy CPU`, §6 `proxy %vCPU` and `metrics/cpu.proxy.vcpuPct` — while the fixed tree is
   byte-equal. Components are now tagged by JSON kind (`s:`/`n:`/`j:`, plus `<absent>`/`<null>` for the
   two null spellings), which keeps `1203` and `1203.0` one identity (the reference's `1203 == 1203.0`)
   while keeping `"1203"` and `true` out of it.
2. **§7.1's `--tolerance 0` claim was wrong** and is corrected there, with the two divergence trees that
   replace it as evidence.
3. **§8 said "two" `S1244` sites; this batch added three** (`ArmAccess.Ratio` was missing); corrected
   there.
4. **Two prose paragraphs were re-indented** (`Tables/TableTcp.cs`, `Tables/TablePersist.cs`): the
   mechanical prose injection had left the first line of each at column 25 while its continuations sat
   at column 13. No literal's content moved — `--mode byte` is still rc=0.
5. **Two comments stopped narrating the port's progress**: `MetricCatalogue`'s remark ("Completed in
   batches 3 and 4") and one `<summary>` ("Every metric this batch publishes") now state the permanent
   fact, so they cannot rot when batch 5 lands.

### 10.1 The criterion sweep, re-run on the checked tree

| batch | semantic | semantic `--tolerance 0` | byte |
|---|---|---|---|
| `1a` | **0** | – | **0** |
| `1b` | **0** | – | **0** |
| `1c` | **0** | – | **0** |
| `2` | **0** | – | 1 (§3 blank line, E4-c's, pre-existing) |
| `3` | **0** | – | **0** |
| `4` | **0** (11/11 slices) | **0** | **0** |
| `5` | 2 (3 missing) | – | – |

### 10.2 The check's own trees and controls

Thirteen mutation trees, none a copy of §5's ten. Each is a fresh extraction of the frozen tarball at
the hardcoded `/tmp/wf-synth`, both implementations are run on it, and the batch-4 slices are compared
with `oracle-diff.py --mode byte --batch 4 --golden <mutant ref> --cs-out <mutant cs>` (semantic mode
too; both agree on all thirteen). Every tree also moves the batch-4 surface against the frozen golden,
which is how a vacuous mutation is told from a live one.

| mutant | the edit | ref vs C# | vs frozen |
|---|---|---|---|
| `cpu-arm-samples-dropped` | `proxifyre`'s pass 2 `LAT` loses every product sample | rc=0 | rc=1 |
| `product-restart-mid-arm` | `proxifyre`'s pass 2 `MIX` product becomes a second `(pid, startUtc)` with a reset counter | rc=0 | rc=1 |
| `persist-idle-window-unpublished` | `proxybridge`'s pass 2 `PERSIST` drops `idleSecondsObserved` | rc=0 | rc=1 |
| `thru-frames-zero` | `wf-aot-opt`'s pass 2 `THRU` reports `frames: 0` | rc=0 | rc=1 |
| `rel-mean-duration-missing` | `proxybridge`'s pass 2 `REL` drops `meanConnectMs` | rc=0 | rc=1 |
| `rel-outcomes-shifted` | `wf-fdd-opt`'s pass 2 `REL` turns a zero `timeout` outcome into 5 | rc=0 | rc=1 |
| `persist-null-members` | `wf-fdd-opt`'s `reconnects` is null in all three passes and `proxifier`'s pass 2 `responseRate` is null | rc=0 | rc=1 |
| `kind-result-differs-from-summary` | `proxifyre`'s pass 1 `LAT` result says `kind: mix` while its header says `latency` | rc=0 | rc=1 |
| `kind-result-without-kind` | `proxifier`'s pass 1 `REL` result has no `kind` at all | rc=0 | rc=1 |
| `kind-no-result` | `proxifier`'s pass 1 `THRU` has no result record | rc=0 | rc=1 |
| `sum-divergence-memory` | `proxifier`'s private bytes × 1e14 in every pass | rc=0 | rc=1 |
| `sum-divergence-cpu` | `wf-aot-opt`'s pass 1 `LAT` identity deltas become `(1e16, 1, 1)` | rc=0 | rc=1 |
| `string-pid-versus-number-pid` | `wf-aot-opt`'s pass 1 `LAT` pid is a number then its string form | rc=0 | rc=1 |

Shape checks read the reference's own output, so the trees are known to move the section they claim:
`cpu-arm-samples-dropped` → §6 `47.28 [47.19–47.38] % (n=2)` with the generator cell still `(n=3)`;
`product-restart-mid-arm` → §6 `1/1, 2/2` identities and `restarts 1`; `persist-idle-window-unpublished`
→ §10 `idle s observed` `(n=2)`; `thru-frames-zero` → §6 `CPU ms / 1000 tx` `(n=2)` with the tx
denominator still named `frames`; `kind-result-differs-from-summary` → §6 `(n=2)`, one pass short,
because a `mix` kind finds none of the LAT arm's class counters; `kind-result-without-kind` → §6 `(n=2)`
with `connectAttempts` from passes 2–3 only — had the header been consulted, the result's own
`connectAttempts` would have kept all three; `kind-no-result` → §6 `(n=2)`, and see §10.3 for why that
tree cannot separate the fallback from `null`; `persist-null-members` → §4 and §10 empty cells with
`passes: 0` and `median: null` in `verdict.json`; `string-pid-versus-number-pid` → §6 `1/1, 2/2` (after
the fix).

Nine produced-output controls on the clean produced directory (base copy byte rc=0 / semantic rc=0):

| control | edit | byte | semantic |
|---|---|---|---|
| `headline-column-order-swapped` | §4's two `REL` columns exchanged, header and cells | 1 | 1 (`header`) |
| `headline-coverage-inconsistent` | §4's coverage cell `21 of 21` → `21 of 20` | 1 | 1 (`coverage`) |
| `headline-pass-count-changed` | one §4 pass-count cell `3` → `2` | 1 | 1 |
| `headline-marker-to-number` | `proxifier`'s `not carried (UDP bypassed)` → `0` | 1 | 1 (`text:` vs `number:`) |
| `cpu-reason-to-number` | §6's `n/a (no product process was sampled)` → `0` | 1 | 1 (`n/a (reason):` vs `number:`) |
| `headline-empty-cell-to-zero` | the empty cell of the `persist-null-members` tree → `0` | 1 | 1 (`empty:` vs `number: 0`) |
| `headline-null-suffix-dropped` | `(n=2 of 3; 1 null)` → `(n=2)` | 1 | 1 |
| `headline-persist-number` | a §10 median `100.0` → `50.0` | 1 | 1 (`value`) |
| `metric-member-removed` | `metrics/cpu.proxy.vcpuPct.threshold` deleted | 1 | **2** (missing member) |

The three empty-cell controls run against the `persist-null-members` mutant's own output, because the
frozen tree holds no empty cell in the compared surface.

Finally, §5's ten stored mutant pairs were re-compared with the frozen differ on their stored outputs:
**10/10 byte rc=0 and semantic rc=0 against their own mutant references, 10/10 rc=1 against the frozen
golden.**

### 10.3 What the check did not fix

- **`RunSamples.ProcessName` has the same kind-collapse shape as the identity encoding, and is left
  alone.** The reference computes `sample.get("process") or ""`, which maps only the *falsy* JSON values
  to `""` and keeps a truthy non-string (a numeric `process`, say) as its own name; the port maps every
  non-string to `""`. A tree carrying a numeric `process` would rank a different primary process — and
  where two candidate names tie on count the reference raises `TypeError` instead of writing anything,
  which D20.1 sends to a C#-side point assertion rather than to a two-sided comparison. The contract
  types the member as a string or omits it (the harness writes it from the matched process name, and
  `check-fixture-drift.py` compares paths, not value types), so no tree the campaign or `make_tree.py`
  writes reaches this. A faithful fix needs the raw JSON value threaded through
  `PrimaryProductProcess`/`PresentProductSamples`/`IsProcess` plus Python's truthiness rule — a change
  to the sample-selection seam, not to this pass. Registered here.
- **The arm-summary fallback in `ArmRecords.Kind` cannot be exercised.** `kind-no-result` deletes an
  arm's `result` and both sides still agree, but they agree for a structural reason: every arm metric
  is read out of the result record, so an arm without one has no denominator to print whatever its kind
  is. The *result-first* half of the rule **is** observable and is held by `kind-result-differs-from-
  summary` and `kind-result-without-kind`; the summary half is faithful by reading, not by mutant.
- **The compensation has no cheap regression anchor.** §7.1's correction shows that a plain
  `total += value` in `DescriptiveStats.Sum` passes *every* gate this batch has: the frozen tree cannot
  tell the two sums apart (byte-identical output, all criteria rc=0), and no unit test covers `Sum`.
  `D21.2 #2` registers the empty-cell and MIX-fallback unit tests for E4-c; this one is not in that list
  and belongs in it — a single fact (`DescriptiveStats.Sum([1e16, 1, -1e16])` is `1.0`) would pin it. It
  needs `Sum` reachable from the test assembly, which is the same visibility decision b1b made for
  `CpRandom`/`VerbatimNumber`/`VerbatimJson`, so it is E4-c's call and does not block this batch.
- **The batch adds no test and the suite stays at 1655.** Judgement: correct for what it ships — the
  criterion is a two-sided oracle over eleven slices, and the thirteen trees plus nine controls cover
  what the frozen tree cannot (empty cell, unknown kind, zero denominator, null members, restarts,
  compensated sums). The two bullets above are the exceptions, and they are registered rather than
  silently accepted.

### 10.4 The gates, re-run on the checked tree

| gate | command | result |
|---|---|---|
| build | `dotnet build WinForward.slnx -c Release` | **0 warnings, 0 errors** |
| tests | `dotnet test WinForward.slnx -c Release -m:1` | **1655 passed, 0 failed, 0 skipped**, 14 assemblies, rc=0 |
| format | `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` | rc=0, **0 bytes** (the check's first edit drew one MA0154 and two RCS1267, all three fixed rather than suppressed) |
| inspector | `jb inspectcode -f=Xml -e=HINT -o=… WinForward.slnx`, on a cleared `InspectCode`/`Transient`/`Daemon` cache and a cleared `/tmp/JB` | **`<Issue>` 0, `<IssueType>` 0**, `InspectionScope` = whole solution (638 files touched), no `CSharpErrors` (`inspectcode-final.xml`) |
| lines | `effective-lines.py` over the four paths | **0 bytes**, rc=0; 69 files / 8633 effective lines, largest `TableEnvironment.cs` at 321, largest of this batch `TableCpu.cs` at 221 (`RunSamples.cs` 192, `MetricCatalogue.cs` 195, `TableTcp.cs` 181, `TablePersist.cs` 158) |
| oracle | §10.1 | as tabulated |

Frozen artifacts were not touched: `git status --short benchmarks/WinForward.E2E.Analysis/verification`
and `git diff HEAD -- …/verification` are both empty (tarball sha256
`723b7378f2f29e9492b9e9a4eedae883516f39af3be44cdd94bc6b584c461902`, `py-tables.md`
`8011d05bdad5dd0b2232a1f9e0bd269a637ad7995299d351dae71a945104ebbc`, `py-verdict.json`
`2e0e64dada67524502cde8958dafdf6f90b1e54400015dacd89c921d8483c55d`), and `.editorconfig` is unchanged
(this batch adds no suppression of its own; the three `S1244` pragmas above are the only new ones and
each carries its reason). Every mutation ran on a fresh extraction of the tarball at `/tmp/wf-synth`;
after all thirteen the re-extracted `raw/` manifest is byte-identical to the pristine one
(`sha256 9e4334557f6e74001a5523edf15285ca5501937d00fe08ee019f74b944db3a30`, 391 files), and the
per-mutant changed-file hashes are recorded in `/tmp/e4b4-check/restore-hashes.py`'s output.
