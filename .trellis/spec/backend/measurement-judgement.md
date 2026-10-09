# Judging A Measurement Run

> How two harness runs are compared and a refactor is proven behaviour-neutral, and what each rate name
> means: the four comparison classes and their band, the five evidence layers, and the `achievedRate`
> caliber with its rename-table registration. Read it when a cell looks wrong, before calling a
> difference a regression, before claiming a refactor is behaviour-neutral, or when you add a rate.
> Family hub: [measurement-harness.md](./measurement-harness.md). The scripts that run the comparison
> are [measurement-tooling.md](./measurement-tooling.md)'s.

## Comparison classes

| Class | Examples | Verdict |
|---|---|---|
| structural | key set, type, array length, string values, `arms[].file` | must match or be declared by the rename table |
| identity | `*/pid`, `sources/port`, version/hash, paths | presence + type only |
| contract | counters, `gates.*` (except overridden entries), **booleans** | within the per-key band; no `--band` means band 0 |
| reading | `latency/*`, `*Us`/`*Ms`/`*Bytes`, CPU/memory/throughput | informational; never fails, listed under `--strict` |

A boolean has no band to fall into: a gate or a flag is equal or it is a contract change. The classes
are declared as data in `record-normalize.json` (`identityPathPatterns`, `contractPathPatterns`,
`readingPathPatterns`, `classOverrides`), and `compare-records.py` refuses a path that matches both a
contract and a reading pattern rather than tossing a coin.

A batch that claims to be behaviour-neutral must attach the `--strict` reading summary and explain
every out-of-band reading. **A contract finding on a zero-width band must be confirmed by a second run
with the same binary before it is called a regression** — the noise floor is not zero: two runs of the
same binary differ on their own (target UDP echo ordering, boot clocks, host sampling), so compare the
two difference lists and check whether the moving keys sit on a zero-width band, which means the band
was never measured rather than that the value is stable.

## Proving a refactor is behaviour-neutral

Layered evidence, cheapest first; a claim must name which layer it used:

1. **token multiset** over the moved files (comments stripped, string literals kept whole): differences
   must be limited to visibility widening, cross-file qualification and the new type declarations;
2. **ordered check**: every line of the new files maps monotonically onto the old file's line sequence
   (catches swaps and reordering the multiset cannot see);
3. **per-method body equality**: extract each method and compare bodies after normalising comments,
   visibility and type qualification — this is what catches two statements swapped inside one method;
4. **registered difference classes**: anything left (a constant changing owner, a nested type being
   promoted, members reordered) is written down with its reason and its behavioural argument, not
   silently absorbed;
5. **runtime equivalence**: `compare-records.py` on a frozen baseline with the same-binary pair
   difference as the noise floor, plus the zero-width published keys compared value by value.

A refactor that needs a real behaviour change (a failure path that used to kill the arm, a missing
sent-set check) registers it as an intentional change with the affected keys named.

## One rate name, one caliber: `achievedRate`

`metrics/achievedRate` means one thing in every arm: **requests successfully sent per elapsed second** —
the count of requests the socket accepted over the arm's elapsed span (`JsonPerSecond.PerSecond`,
`null` when no time passed). One numerator per arm, and no arm may keep the old "completed responses"
caliber under this name:

| Arm | Numerator | Site |
|---|---|---|
| LAT / LATLOAD | `tcp.SentOk` / `udp.SentOk` | `Client/Arms/LatencyMetricsWriter.cs:139,170` |
| LOSS | `UdpReliabilityTracker.SentOk` | `Client/Arms/LossArm.cs:120` |
| DNS / DNSALT | `sent` (queries written to the socket) | `Client/Arms/DnsArm.cs:110` |
| REL | attempts whose request send completed (`TransferMeasured`) | `Client/Arms/ReliabilityMetricsWriter.cs:76` |
| PERSIST | `_sentRequests` (rounds whose frame send completed) | `Client/Arms/PersistentArm.cs:119` |

The *completion* caliber is a different **name**, never the same one: PERSIST publishes
`metrics/completionRate` (responses per elapsed second, `Client/Arms/PersistentArm.cs:120`), the
population `achievedRate` carried before the unification. A run with `requests > responses` therefore has
`achievedRate > completionRate`, and the rename is value-preserving by construction: the `ADDITIONS` row
records the two populations as one under a new name, and the old expression survives word for word
(`JsonPerSecond.PerSecond(state._responses, …)`).

A new published key is declared in `ArmKeys` (here `metrics/completionRate`) and asserted natively by
`ContractShapeTests` + `DeclaredKeys` (`tests/WinForward.E2E.Tests/ContractShapeTests.cs:14-19`,
`DeclaredKeys.cs:6-9`), which compare each kind's declared paths against the bytes a run publishes in
both directions — the same two-way diff the deleted migration tool `contract-inventory.py` used to
compute offline. `compare-records.py --rename-table … --batch B2` (D19.3 A) reads the frozen
`contract-rename.json` from `benchmarks/WinForward.E2E.Analysis/verification/`: a path the table does
not declare reads as a structural difference and the batch's own gate goes red.

#### Bad — a rate under the wrong population
```csharp
AchievedRate = JsonPerSecond.PerSecond(state._responses, …)   // PERSIST: the completion population under the send name
AchievedRate = JsonPerSecond.PerSecond(attempts.Length, …)    // REL: counts attempts that never sent a request
```

## Tests Required

- **Rate calibers** (`PersistentRateCaliberTests`, `Shapes/PersistentShape`): the shape factory carries
  `completionRate` as a nullable reading and the declared-key comparison covers it; a real PERSIST run
  with every request answered publishes `completionRate == achievedRate > 0`, and a run whose peer never
  answers publishes `completionRate == 0 < achievedRate`; `ContractShapeTests` must show the PERSIST
  arm declaring and publishing `completionRate`, and `compare-records.py --rename-table … --batch B2`
  must stay at `contract=0`.
- **Regression comparison — a manual gate, not a test.** No test and no workflow invokes
  `compare-records.py`; run it by hand over two artifact directories (each holding `out/`,
  `ledger.jsonl` and `target.out`):

  ```
  python3 benchmarks/WinForward.E2E/scripts/compare-records.py RUN1 RUN2 --normalize <record-normalize.json>
      [--band jitter-band.json | --write-band jitter-band.json] [--rename-table <contract-rename.json> [--batch B2]]
      [--strict] [--explain-classes]
  ```

  The normalization fixture still sits in the archive,
  `.trellis/tasks/archive/2026-10/10-07-e2e-harness-refactor/research/record-normalize.json`; the
  rename and inventory tables it composes with moved out of the archive to
  `benchmarks/WinForward.E2E.Analysis/verification/` (`contract-rename.json`, `contract-inventory.json`),
  beside the row profiles and the golden. Neither table has a generator left: `contract-inventory.py`
  was the spent migration tool that produced the pair and is deleted, so they are ground truth rather
  than regenerable output. `normalize-pattern-hits.py` (D16.2's dead-pattern report) is deleted with it;
  the normalization config stays a frozen fixture, and the declared-vs-published comparison it informed
  is now `ContractShapeTests`' job. `run1 vs run1` and
  `run1 vs run2 --band` must compare clean; a mutated contract counter fails while a mutated pid and a
  mutated latency reading do not. The script exits 1 on any structural, identity, contract or
  rename-table finding, and 0 when the comparison is clean.
