# Plan: measurement-harness.md

> Target: `.trellis/spec/backend/measurement-harness.md` (607 lines, 8 H2 sections, 12 H3
> subsections, 480 effective lines by the PRD's own measure). Verified against the tree at the
> current HEAD. Every `file:line` below was read, not inferred. Section 3 proposes a 9-child family
> plus a hub; the hub keeps the filename and carries a **Where things moved** table so the numbered
> citations in `backend/index.md` (§3.9, §3.10) and `guides/index.md` (§3.8) stay resolvable.
>
> The four seeded "file not found" hits are **refuted** — `tables.md`, `verdict.json` and
> `<out>/plots/SKIPPED.md` are the analyzer's live outputs, and §8 correctly describes `analyze.py`
> as retired. The real staleness is elsewhere: the README contract gate the spec leans on cannot
> run at all, and its research-table path (like `check-fixture-drift.py`'s) points into a task
> directory that was archived by `aee1955`.

## 1. Stale claims (verified against code)

| # | Section (line) | Claim as written | What the code says now | Evidence (file:line) | Action |
|---|---|---|---|---|---|
| 1 | §3.11 (269) and its transcript (272-274), repeated in §6 (442-448) and §7 (542-545) | `check-readme-contract.py` "is therefore a **gate**, not prose" and prints `111 key(s) checked against 401 declared constant path(s): ok # exit 0` | The script exits **2** and prints nothing but an error: `cannot read …/.trellis/tasks/10-07-e2e-harness-refactor/research/contract-rename.json: [Errno 2] No such file or directory`. The rename table it hard-codes was moved to the archive by `aee1955 chore(task): archive 10-07-e2e-harness-refactor`. Run against the archived copy the output is byte-for-byte what the spec quotes (`111 key(s) checked against 401 declared constant path(s): ok`, exit 0), so the numbers are right and only the path is dead. | `benchmarks/WinForward.E2E/scripts/check-readme-contract.py:47` (the stale path); `.trellis/tasks/archive/2026-10/10-07-e2e-harness-refactor/research/contract-rename.json` (where it lives); `tests/WinForward.E2E.Tests/RepoPaths.cs:49-71` (the archive-aware lookup the .NET side already uses) | Fix the script's path first (or move the table), then keep the claim. Until then §3.11's central assertion is false and §4's row 361 contradicts it. |
| 2 | §3.11 (288), §2 (56-59), §6 (443), §3.12 (324) — and §8's table | The gates read `research/contract-rename.json`, `research/record-normalize.json`, `research/contract-inventory.json` | Those files are at `.trellis/tasks/archive/2026-10/10-07-e2e-harness-refactor/research/`; no live `research/` directory exists under the repo root or under `benchmarks/WinForward.E2E/`. A second script hard-codes the same dead path. | `.trellis/tasks/archive/2026-10/10-07-e2e-harness-refactor/research/{contract-rename.json,record-normalize.json,contract-inventory.json}`; `benchmarks/WinForward.E2E.Analysis/verification/check-fixture-drift.py:40` (same stale path) | Name the archived path explicitly, or make the scripts locate the table the way `RepoPaths.cs` does. |
| 3 | §8, artifact table (592) | "`scripts/oracle-diff.py` — the differ" listed among the analyzer's `verification/` artifacts | `benchmarks/WinForward.E2E.Analysis/scripts/` holds only `analyze.sh`. The differ is `benchmarks/WinForward.E2E/scripts/oracle-diff.py` (as §2 line 65 says). Read in place, the row names a file that does not exist. | `ls benchmarks/WinForward.E2E.Analysis/scripts/` → `analyze.sh` only; `benchmarks/WinForward.E2E/scripts/oracle-diff.py:1` | Rewrite the row's path, or add a "(the harness's `scripts/`, not this project's)" note. |
| 4 | §8, artifact table (593) | "`verification/row-profiles.json` + `scripts/check-fairness.py`" | `row-profiles.json` is in `verification/` as written, but `check-fairness.py` is in `benchmarks/WinForward.E2E/scripts/` — the pairing implies both are in the analyzer project. | `benchmarks/WinForward.E2E.Analysis/verification/row-profiles.json`; `benchmarks/WinForward.E2E/scripts/check-fairness.py:1` | Split the row's two paths. |
| 5 | §6 (436) | CLI snapshots record "**28 commands** (both helps and every error path)" | `CASES` in the collector holds **31** entries. The 28 is the frozen `before/` tree (the pre-E2-d binary); `after/index.json` holds 31, and `CliSnapshotTests` replays 28 from `before/` plus the 3 E3-d additions from `after/`. | `benchmarks/WinForward.E2E/scripts/cli-snapshots.py:42-76` (31 entries); `.trellis/tasks/archive/2026-10/10-07-e2e-harness-refactor/research/cli-snapshots/before/index.json` (28) and `after/index.json` (31); `tests/WinForward.E2E.Tests/CliSnapshotTests.cs:49-63`, `:76-79` | Say "31 commands (28 replayed against the `before` tree plus the 3 the E3-d receive-loop option added)". |
| 6 | §6 (439-441) | "the **three harness projects** must report nothing at a 400-line limit" | Four projects carry harness code: `WinForward.E2E`, `WinForward.E2E.Contracts`, `WinForward.E2E.Analysis`, `WinForward.E2E.Tests`. The analyzer exists precisely because it is a fourth. | `WinForward.slnx:13,33,34,35`; `benchmarks/WinForward.E2E/scripts/effective-lines.py:20-22` (usage) | "the four harness projects". |
| 7 | §6 (414-415) | Literal gate "scans the whole file … for key-position literals in `Client/Arms/**` and `ClientRunner.cs`" | It scans every file under `Client/Arms/` plus **nine named writer files** across two binaries: `Client/ClientRunner.cs`, `Client/ArmRecordWriter.cs`, `Client/RunFileWriter.cs`, `Client/ResourceSampler.cs`, `Client/ResourceSampleWriter.cs`, `Target/TcpTargetServer.cs`, `Target/UdpEchoServer.cs`, `Target/DnsServer.cs`, `Target/TargetRunner.cs`. The `Target/` writers — the ledger, half the record surface — are missing from the spec's description. | `tests/WinForward.E2E.Tests/JsonKeyLiteralGateTests.cs:42-52`, `:172-192` | List the scanned roots as `Client/Arms/**` + the two writers' groups, or say "the nine writer files named in the test". |
| 8 | §5 (368) | "**Good**: add `ArrivedEarly` to `LossMetrics` as `required`, add `ArmKeys.Loss.ArrivedEarly`" | No `ArrivedEarly` exists anywhere in the tree. `LossMetrics` has 29 `required` members, and the late-arrival quantity is published as `Late` / `LateRate` (`ArmKeys.Loss.Late`). | `benchmarks/WinForward.E2E.Contracts/Metrics/LossMetrics.cs:18-100`; `benchmarks/WinForward.E2E.Contracts/ArmKeys.Loss.cs:33-34`; `rg -n ArrivedEarly .` → the only hit in the whole repo is this spec line (and this plan) | Replace with a real member (`Late`) or mark the sentence explicitly hypothetical. |
| 9 | §5 (373-374) and §7 (470) | "`outcome.Metrics["sentOk"] = …` (string literal) … the first two are gated by `JsonKeyLiteralGateTests`", and the Wrong block's `Metrics["udp.sentOk"]` | The gate flags only literals that match a **currently declared** `ArmKeys` spelling. `sentOk` is not one: the convergence renamed it to `sent` (`DOTTED_LEAF_RENAMES = {"sentOk": "sent"}`), and no `"sentOk"`/`"udp.sentOk"` literal exists in any `.cs` file. So the spec's own Bad example is exactly the spelling a rename retired, and the gate passes it. The `Dictionary<string, object?>` half is not a key-position literal either and is not what that test checks. | `benchmarks/WinForward.E2E/scripts/contract-inventory.py:53`; `benchmarks/WinForward.E2E.Contracts/ArmKeys.Loss.cs:25` (`Sent = "sent"`); `tests/WinForward.E2E.Tests/JsonKeyLiteralGateTests.cs:126-132` (the filter is `s_knownKeys.Contains(literal.Key)`) | Rewrite the example to a spelling that is still declared (e.g. `Metrics["sent"]`), or drop the "gated by" claim for this pair. |
| 10 | §1 (21) | "The analyzer (E4) **will** reference the same project, so a rename is a compile error on both sides" | The analyzer does reference it today — the future tense is a plan sentence left in a contract. | `benchmarks/WinForward.E2E.Analysis/WinForward.E2E.Analysis.csproj:10`; `benchmarks/WinForward.E2E/WinForward.E2E.csproj:11` | Past tense; keep the "compile error on both sides" rule. |
| 11 | §3.1 (77) | Path alphabet examples include "`metrics/parameters/seconds`" | The path is `parameters/seconds`. `metrics` and `parameters` are sibling objects of an arm record; nothing publishes `metrics/parameters/…` anywhere in the tree. | `benchmarks/WinForward.E2E.Contracts/ArmKeys.Common.cs:117-119` (`Parameters.Seconds = "seconds"`); `rg -n "metrics/parameters" .` → hits only in this spec and in the archived design note the example was copied from (`.trellis/tasks/archive/2026-10/10-07-e2e-harness-refactor/design-decisions.md:219`, still spelling `metrics/tcp.sentOk` — the pre-E1-B1 alphabet) | Change the example to `parameters/seconds`. |
| 12 | §2 (59-60) | "`contract-inventory.py` — publishes research/contract-inventory.json + contract-rename.{json,md}" | The script requires a subcommand and takes every output path from the caller: `inventory --run DIR --out FILE`, `rename --baseline DIR --run DIR --out-json FILE --out-md FILE`. Bare invocation is an argparse usage error. | `benchmarks/WinForward.E2E/scripts/contract-inventory.py:299-317` | Show the two real invocations (or at least the subcommand); the fixed `research/` destinations are the caller's choice, not the script's. |
| 13 | §4 (350) | Unknown plan key → `arm '<name>' (kind '<kind>'): unknown key '<key>'` | The message continues: `… unknown key '<key>' (expected one of: <every accepted key>)`. | `benchmarks/WinForward.E2E/Client/PlanFile.cs:312` | Append the suffix, or mark the quote as a prefix. |
| 14 | §4 (351) | Wrong JSON type → `'<key>' is <value>, which is not a string/number` | There is no "not a number" branch. Text keys fail as `which is not a string`; numeric keys go through `TryReadInt`/`TryReadNumber`, whose failures are `which is not an integer` and `which is not a positive number`. | `benchmarks/WinForward.E2E/Client/PlanFile.cs:387` (`not a string`), `:418` (`not an integer`), `:327` (`not a positive number`) | Split the row into the three real messages. |
| 15 | §3.7 (159-160) | "the **four** arm families and the **four** ledger families each get their shard" | Nine arm-metric shard files exist (`Control`, `Dns`, `Idle`, `Latency`, `Loss`, `Mix`, `Persistent`, `Reliability`, `Throughput`) plus `Common`, `Run`, `Sample`; the ledger is **one** file, `ArmKeys.Ledger.cs`, holding **eight** shards (`Envelope`, `TcpRecord`, `TcpSummary`, `UdpSummary`, `DnsSummary`, `TargetSummary`, `ErrorRecord`, `VerdictNames`). "Four and four" is not the tree's shape. | `benchmarks/WinForward.E2E.Contracts/ArmKeys.{Control,Dns,Idle,Latency,Loss,Mix,Persistent,Reliability,Throughput}.cs`; `benchmarks/WinForward.E2E.Contracts/ArmKeys.Ledger.cs:64,67,80,112,139,183,243,395,417` | Restate with the real counts, or say "one shard per record family, in a file per arm family". Author's call on what "family" means here — see §5. |
| 16 | §6 (449-450) and §6 (439-441) | "**Regression comparison**: `run1 vs run1` … compare clean" and "**Effective lines** (`scripts/effective-lines.py`): the three harness projects must report nothing" are listed under "Tests Required (assertion points)" | No test in the repository invokes either script: `rg -n "compare-records\|effective-lines" tests/` returns no hits, and neither `.github/workflows/analyzer-gate.yml` nor `release-build.yml` runs them. They are manual gates documented in `benchmarks/WinForward.E2E/README.md:694-696`. | `tests/` (no hits); `.github/workflows/analyzer-gate.yml:1-21`; `benchmarks/WinForward.E2E/README.md:694-696` | Either mark them "manual gate, not a test" or add the wiring; the heading currently claims coverage the tree does not have. |
| 17 | §3.11 (290-291) | "**Three token classes** are declared rather than inferred" | There are **two** declared sets holding three tokens: `LEGACY_TOKENS = {"metrics/clientSendLoss"}` and `DOCUMENTED_NON_KEYS = {"parameters/window", "parameters/loss.lossWindowMs"}`. The behaviour the sentence describes is right; the count is of tokens, not classes. | `benchmarks/WinForward.E2E/scripts/check-readme-contract.py:123-131` | "Three tokens, in two declared sets". |

**Refuted seeds (do not change).** The four files the automated existence check could not find are
not stale:

- `analyze.py` — §8 (569-571) already says it "is no longer in the tree" and survives only as frozen
  output. Correct. The file was deleted in `b3b4aa4`; the retirement recipe is recorded at
  `benchmarks/WinForward.E2E.Analysis/verification/FROZEN.md:8,99-108`, and `rg analyze.py` finds
  only historical prose in `FROZEN.md`, `make_cp_vectors.py:17`, `make_tree.py:48` and
  `check-boundary-trees.py:25`.
- `tables.md` — the analyzer's live primary output, written at
  `benchmarks/WinForward.E2E.Analysis/Cli/AnalysisRunner.cs:38-41`, and §8's "sixteen `## N.`
  sections" is right (`Tables/TablesWriter.cs:52-69` holds exactly 16 headings).
- `verdict.json` — also live (`AnalysisRunner.cs:43-46`); §8's "fourteen top-level keys" is right
  (`Verdict/VerdictWriter.cs:19-34` holds exactly 14).
- `plots/SKIPPED.md` — written unconditionally at `AnalysisRunner.cs:48` via
  `Cli/PlotsNotice.cs:41`. The frozen, byte-identical copy is
  `verification/plots-SKIPPED.md`; naming both roles is a clarity fix, not a correction.

Two further claims I checked and could **not** fault: §4's `check-readme-contract.py` exit-code
description (`check-readme-contract.py:265-300`) is accurate — including "exit 2 when … the rename
table cannot be read", which is exactly today's behaviour and is what makes row 1 above an internal
contradiction rather than a typo; and §8's "`--resamples 10000` / `--seed 20261006` /
`--warmup-seconds 5.0` / `DefaultMinPasses = 3`" matches `Cli/AnalysisOptions.cs:17-28`.

## 2. Verbosity cuts

| # | Lines | What | Why it can go |
|---|---|---|---|
| 1 | 9-18 | `## 1. Scope / Trigger` heading plus eight trigger bullets | The preamble (3-5) already states the same trigger set in one sentence; the two lists differ only in wording. Fold the two triggers that are not in the preamble (§3.9, §3.10) into it and delete the section — the template's "scope/trigger" duty is already discharged twice. |
| 2 | 20-22 | "The contract lives in … so a rename is a compile error on both sides, not a silently empty table cell" | Restates the preamble and §3.11's 267-269 a third time. Keep one sentence in the hub. |
| 3 | 32-42 | The `ArmKeys` / `LossMetrics` / `ArmParameters` C# sketches | `/* … */` pseudo-signatures that carry no rule, duplicate `ArmKeys.Loss.cs` and `Metrics/LossMetrics.cs`, and and drift from the record the moment a member is added. Keep `IJsonWritable`, `JsonlPolicy` and the `JsonlSink` member list; point at the sources for the rest. |
| 4 | 55-66 | The twelve-line script inventory with one-line descriptions | Duplicated almost verbatim in `benchmarks/WinForward.E2E/README.md:63-72` and again in §8's table. Keep the three scripts a reader needs to find the contract (`jsonl_paths.py`, `compare-records.py`, `check-readme-contract.py`) and link the README table. |
| 5 | 202-212 | The two "measured properties" that bound the teardown vocabulary | Eleven lines for one rule and one exception. The E3-e parenthetical ("the five E3-e moved sites all have their own"), the `TargetLog`/`ResourceSampleWriter` aside and the `TcpConnectionProtocol` paragraph restate the `<remarks>` of the gate at greater length. Keep: a disposed socket re-used throws; a socket disposed under a pending receive ends as `OperationAborted`; therefore the source gate, not a driven test, is the check. |
| 6 | 214-220 | The REL census paragraph | The rule (REL keeps teardown inside `outcomes`; a census keeps counting) is load-bearing, but "what a teardown changes there is the published `status` (`cancelled`, not `exchanged`) and the `otherError` flag" repeats the §3.9 table row at 197. Compress to the invariant plus its anchor. |
| 7 | 237-242 | "the rename is value-preserving by construction: `completionRate == JsonPerSecond.PerSecond(responses, ticks)`, the old expression word for word" | Re-derives what the table directly above (229-236) already states and what `PersistentArm.cs:119-120` shows. Keep the one-sentence rule (`achievedRate > completionRate` when requests exceed responses). |
| 8 | 260-262 | The `ADDITIONS` registration rule | Repeated verbatim as §4's row at 360. Keep it in the rate child and let §4 point at it. |
| 9 | 271-274 **and** 542-545 | The same `check-readme-contract.py` console transcript, twice | Identical output block, identical point. Keep one copy (in the gate's own document). |
| 10 | 290-296 | The declared token classes, explained twice | 290-291 says they are declared rather than inferred; 294-296 says the same thing again for `completionRate`. The README carries the same paragraph at `README.md:419-431`. Keep the three tokens and the one-line reason. |
| 11 | 298-299 | "The same section names its authority for the record's *shape* … the write site is what proves the record carries it" | Restates step 2 at 283-285. |
| 12 | 307-316 | §3.12's first bullet: the slot-claim walk-through | The mechanism is stated at greater length in `Target/SourceCensus.cs:10-30`. Keep the contract sentence (capacity is the interval's active endpoints per loop, deltas are the interval's own) and drop the walk-through. |
| 13 | 317-326 | "The previous behaviour … E5-b's ledger held exactly 64 source endpoints and then published `sources: []` with `received == sourceOverflow` for **42 minutes**" | A 42-minute incident retold in full for a rule that fits in one line: capacity is now per interval, not per process lifetime. Keep the date tag and one clause of the symptom. |
| 14 | 327-335 | "The bounded boundary effect", with two campaign-scale measurements (8 loops, 560 endpoints, 32,040 datagrams → one datagram lost; a 439,445-datagram churn run → none lost) | Two measurements for one rule. Keep the rule ("a re-claim can drop at most one datagram at the interval boundary; the 1 Hz summary interval is part of the contract") and the single headline number `received = censused + sourceOverflow + 1`. |
| 15 | 336-342 | The claim handshake, in seven lines | `Target/SourceCensus.cs:24-30` states the same handshake in the same words. Keep the observable invariant ("a torn claim is never published as one endpoint's address beside another's count") and the anchor; delete the re-derivation of the two memory barriers. |
| 16 | 375-404 | §5's teardown, rate and README bullets (three of the four blocks) | Every one restates a rule already given in §3.9, §3.10 or §3.11 — and the teardown and README *Bad* examples reappear in §7 at 498-528 and 530-547. Keep only the bullets that add a case the rule does not already imply. |
| 17 | 420-426, 427-430, 451-457, 458-462 | §6's lane, ledger, teardown and rate bullets | Each re-enumerates the assertions the test file itself owns and the contract section above already states (§3.6, §3.2, §3.9, §3.10). §6's job is the class-name → rule mapping; the assertion inventory is the test's job. |
| 18 | 469-480 | §7's first Wrong/Correct pair | The `Wrong` block's comment ("token may cancel mid-record") and the `Correct` block's ("cancellation at the lock only") restate §3.3 lines 98-100; the literal-key half restates §3.7. |
| 19 | 482-496 | §7's seam pair | Line-by-line restatement of §3.6: `policy.OnReceive(…) // decode + classify + enqueue, no counters` is §3.6's thread-contract item 1, and `policy.Settle(Clock.Now); // the only settlement point` is item 2. |
| 20 | 498-528 | §7's teardown pair | Restates §3.9's shape table; the tag `D19.2 ⑨` appears twice in the same block, and §5 at 382-384 already carries the same two mutations. |
| 21 | 530-547 | §7's README pair | The whole of §3.11 again — same claim, same transcript, same `111 key(s)` line. |
| 22 | 549-553 | "Gotcha — a published key with no reader is still contract" | Restates §3.11's 294-296 and the README's own paragraph at `README.md:419-431`. |
| 23 | 555-558 | "Gotcha — the noise floor is not zero" | Restates §3.5's closing paragraph (120-122) and §3.8's layer 5 (176-177). |
| 24 | 560-563 | "Gotcha — stale audit premises" | Near-verbatim duplicate of `.trellis/spec/guides/index.md:76-82` ("When Implementing From An Audit Or A Defect List"), and the "E2E audit's §2 list was ~10/13 already implemented" half is changelog with no rule. |

Raw-line arithmetic: the cuts above remove roughly 200 raw / 150 effective lines. That lands the
document near 400 raw / 330 effective — still over the cap once any of the retained material grows,
so the split in §3 is not optional.

## 3. Split proposal

Family total ≈ 620 raw lines (≈ 330 effective, down from 480), spread over one hub and nine
children; the largest child is ≈ 92 raw. The family total is *higher* than 607 because ten files
carry ten sets of headings, blank lines and one-line scopes (~90 heading lines and ~165 blank lines
that the single file did not pay for). No file approaches 400 by either measure.

### measurement-harness.md (hub, ≈ 70 lines)

The hub keeps its filename and becomes a real document, not a table of contents:

1. **Scope** — four sentences: what the harness is, that its contract is public, and that a change
   to a published key, plan loading, a writer, a teardown path or a comparison rule lands here.
2. **The one definition point** — field names live in `benchmarks/WinForward.E2E.Contracts`; both
   binaries reference it (`WinForward.E2E.csproj:11`, `WinForward.E2E.Analysis.csproj:10`), so a
   rename is a compile error on both sides rather than an empty table cell. This is the hub's one
   load-bearing invariant and the reason the family exists.
3. **The four rules worth knowing before you read anything else** — one definition point; teardown
   books no data point; `achievedRate` is requests successfully sent per elapsed second; a key the
   README documents is gated, not trusted. One line each, each linking its child.
4. **Topic map** — the nine children, each with the question it answers.
5. **Where things moved** — a `§3.x → child` table so `backend/index.md:22` (§3.9, §3.10) and
   `guides/index.md:93` (§3.8) stay resolvable by number for a human reader, and so anyone holding
   an old copy of the 607-line file can find the rule.

### record-key-contract.md (≈ 72 lines)

- Purpose: how a published key is spelled and written — one `ArmKeys` shard per family, one
  `IJsonWritable` write path, the `/`-join path alphabet, and the three states a value can be in.
- Sections moved: §2 lines 38-44 (`IJsonWritable`, the records); §3.1 (73-79); §3.2 (80-90); §3.7
  (155-163); §5 lines 368-374; §6 lines 414-415. The `§4` rows that police a *plan value* (350-353)
  go to `plan-loading-contract.md` instead.
- Notes: fix the `metrics/parameters/seconds` example (stale row 11); restate the shard counts
  honestly (stale row 15). Anchors: `ArmKeys.Common.cs`, `Metrics/*.cs`, `Json/PerSecond.cs:18`,
  `Json/Rate.cs:18`, `JsonKeyLiteralGateTests.cs:42-52`.

### plan-loading-contract.md (≈ 40 lines)

- Purpose: fail-closed plan loading — one descriptor per arm kind, the accepted-key whitelist, the
  numeric domains, the exit codes, and the exact refusal text a user sees.
- Sections moved: §3.4 (103-110); §4 rows 350-355; §6 lines 416-417.
- Notes: quote the real messages (stale rows 13, 14). Anchor: `Client/Arms/ArmKind.cs:11-95` (name,
  keys, validator, runner in one table), `Client/PlanFile.cs:140-175,290-330,375-440`,
  `Cli/ExitCodes.cs:5-9`.

### run-failure-and-teardown.md (≈ 92 lines)

- Purpose: every way a run may report a failure, and the teardown vocabulary that must report
  nothing — the two sink policies, `WriteErrors`, and the five `ObjectDisposedException` shapes.
- Sections moved: §3.3 (91-102); §3.9 (182-221); §4 rows 356-359; §5 lines 375-384; §6 lines
  418-419 and 451-457; §7 lines 498-528.
- Notes: this is the biggest child and the one most cited from elsewhere; keep the `D19.2 ⑨` tag
  once. Anchor: `Json/JsonlPolicy.cs:6-19`, `Json/JsonlSink.cs:35-129`,
  `ObjectDisposedCatchGateTests.cs:39-75`, `ObjectDisposedTeardownTests.cs:38-141`.

### lane-seam-contract.md (≈ 55 lines)

- Purpose: the engine/policy/transport seam — who owns each counter, the thread contract, the four
  receive kinds, deferral, and where would-block is sampled.
- Sections moved: §3.6 (124-154); §6 lines 420-426; §7 lines 482-496.
- Notes: verified correct as written; the cut is the duplicated §6/§7 blocks. Anchors:
  `Client/Lanes/LaneTransportContracts.cs:12-40,77-106`, `ILanePolicy.cs:33-82`,
  `LaneEngine.cs:99-151,281-305`, `LaneCounts.cs:24-41`.

### regression-comparison.md (≈ 58 lines)

- Purpose: comparing two harness runs and proving a refactor behaviour-neutral — the four classes,
  the band and the noise floor, the five evidence layers, and the tool chain that runs them.
- Sections moved: §3.5 (111-123); §3.8 (164-180); §2 lines 56-61 (`compare-records.py`,
  `jsonl_paths.py`, `normalize-pattern-hits.py`); §6 lines 449-450; §7 lines 555-558.
- Notes: the `--normalize research/record-normalize.json` invocation must name the archived path
  (stale row 2). Anchors: `compare-records.py:99-101,228-260,830-840`,
  `jsonl_paths.py:4-17,65-67`, `record-normalize.json`.

### rate-calibers.md (≈ 62 lines)

- Purpose: one rate name, one caliber — `achievedRate` means requests successfully sent per elapsed
  second in every arm, and the completion caliber lives under its own name.
- Sections moved: §3.10 (222-263); §4 row 360; §5 lines 385-391; §6 lines 458-462.
- Notes: the one §3.x subsection whose every claim verified clean; the cut is the re-derivation
  (verbosity row 7) and the doubled `ADDITIONS` rule (row 8). Anchors:
  `LatencyMetricsWriter.cs:139,170`, `LossArm.cs:120`, `DnsArm.cs:110`,
  `ReliabilityMetricsWriter.cs:76`, `PersistentArm.cs:119-120`,
  `contract-inventory.py:65-94`.

### udp-source-census.md (≈ 52 lines)

- Purpose: the per-interval 64-slot source census — capacity, reclaim, the bounded boundary effect
  and the claim handshake.
- Sections moved: §3.12 (301-345); §6 lines 431-435.
- Notes: keep "(T2, 2026-10-08)" — the date justifies the reclaim rule. Keep the disarmingly honest
  "a larger table was rejected" trade-off sentence; it is the rule's *why*. Anchors:
  `Target/SourceCensus.cs:12-30,64,83-90,93-131`, `Target/UdpEchoServer.cs:14,90-133`,
  `SourceCensusTests.cs`.

### analyzer-contract.md (≈ 55 lines)

- Purpose: the .NET analyzer that replaced the Python reference, and its frozen oracle — options and
  defaults, the three artifacts, the differ contract, the boundary trees.
- Sections moved: §8 (567-607).
- Notes: fix the two artifact-table paths (stale rows 3, 4); add
  `verification/check-fixture-drift.py` (present in the tree, absent from the doc, and broken by the
  same archived path as row 2); say once that `verification/plots-SKIPPED.md` is the frozen copy of
  the `<out>/plots/SKIPPED.md` the run writes. Anchors: `Cli/AnalysisOptions.cs:15-27`,
  `Cli/AnalysisRunner.cs:25-61`, `Tables/TablesWriter.cs:52-69`,
  `Verdict/VerdictWriter.cs:19-34`,
  `benchmarks/WinForward.E2E/scripts/oracle-diff.py:129-190,200-260,1167-1270`,
  `verification/FROZEN.md`.

### harness-gates.md (≈ 72 lines)

- Purpose: the harness's own gates and recorded surfaces — the README contract table, the
  effective-line limit, and the recorded CLI text.
- Sections moved: §2 lines 62-64 (the three script rows); §3.11 (264-300); §4 rows 361-362; §5
  lines 392-404; §6 lines 436-448; §7 lines 530-547.
- Notes: **this document cannot be written truthfully until `check-readme-contract.py:47` is
  fixed** — while the path is dead the gate exits 2 and §3.11's "exit 0" claim is false. Keep the
  `111 / 401` numbers: they are what the checker prints once it can read the table. Anchors:
  `check-readme-contract.py:44-70,123-131,228-300`, `effective-lines.py:15-30`,
  `cli-snapshots.py:42-76`, `CliSnapshotTests.cs:49-79`.

## 4. Cross-references that break

| Where | What it says now | What breaks | Fix |
|---|---|---|---|
| `.trellis/spec/backend/index.md:22` | index row citing "the teardown vocabulary (§3.9) and the `achievedRate` caliber (§3.10)" | Both numbers die when §3.9 and §3.10 move. The row's description also becomes a hub description rather than a document description. | Rewrite the row for the hub and add nine rows, one per child, each with a one-line scope. |
| `.trellis/spec/guides/index.md:93` | `[Measurement Harness §3.8](../backend/measurement-harness.md)` under "When Claiming A Refactor Is Behaviour-Neutral" | `§3.8` moves to `regression-comparison.md`; the numbered anchor dies. The link itself survives because the hub keeps the filename. | Retarget to `[Regression Comparison](../backend/regression-comparison.md)`. |
| `.trellis/spec/guides/index.md:83` | `[Measurement Harness](../backend/measurement-harness.md) (contract + comparison classes)` | Link survives; the parenthetical promises "comparison classes", which move out of the hub. | Reword to "(the contract family's topic map)", or retarget the parenthetical at the comparison child. |
| `.trellis/spec/backend/directory-structure.md:49` | `├── WinForward.E2E/            # end-to-end harness (contract in measurement-harness.md)` | Nothing breaks — it names no section. | Optional: point at the hub's topic map. |
| Internal self-references at lines 16, 17, 62, 381, 431, 580 | `(§3.9)`, `(§3.10)`, `(§3.11)`, `(§3.9)`, `(§3.12)`, `(§3.11)` | Each becomes a dangling number once its target moves. | Rewrite as relative links to the owning child. |
| `.trellis/spec/backend/index.md` "History" note | The 2026-08-29 split precedent | Should gain a one-line entry for this split, matching the existing convention. | Append to the history blockquote. |
| Frozen evidence in `benchmarks/results/**` | — | **Nothing breaks.** `rg -n "measurement-harness" benchmarks/results/` returns no hits, so no dated record cites this file or any of its sections. | None. |
| Live code comments in `src/`, `tests/`, `benchmarks/` | — | **Nothing breaks.** No `.cs` comment cites `measurement-harness.md`; the spec citations from code are all to `windows-ndisapi.md`, `hot-path.md`, `traffic-policy-lifecycle.md` and `directory-structure.md`. AC5 has no work here. | None. |

## 5. Open questions / judgement calls

1. **The gate cannot pass until a script is fixed.** §3.11 is the one section whose central claim is
   a *gate that works*; it does not, because `check-readme-contract.py:47` reads an archived path.
   The spec revision can either (a) describe the gate as "currently red: the rename table must be
   found again" — accurate but useless — or (b) fix the script (or move
   `contract-rename.json`/`record-normalize.json` back to a live path) in the same change. I
   recommend (b) and modelled the fix on `tests/WinForward.E2E.Tests/RepoPaths.cs:49-71`, which
   already documents the archive-aware lookup for exactly this reason. The lead should decide
   whether that one-line script edit is inside this task's "spec-only" boundary.
2. **`check-fixture-drift.py` is invisible to the spec and broken the same way.** It is a real check
   (`verification/check-fixture-drift.py`), it fails today for the same archived-path reason, and
   §8's artifact table never mentions it. Add it to `analyzer-contract.md`, or say why it is out of
   scope.
3. **Which line measure does AC2 use?** The PRD tabulates "raw / effective (non-blank, non-heading)"
   and reports this file as 607 / 480. I sized the children by raw lines (largest ≈ 92, family
   ≈ 620) so they pass either way, and estimated effective to show the R2 drop (480 → ≈ 330).
   Confirm that "no doc exceeds 400 lines" means raw.
4. **§3.7's "four arm families and the four ledger families" (line 159).** The tree has nine arm
   shards and one ledger file holding eight shards. I could not determine what the original "four"
   counted — possibly the four *convergence* families in `contract-inventory.py:44-56`, which would
   make the sentence refer to the migration rather than to the code. The rewrite needs the author's
   intent; I have marked it stale and left the real counts as the evidence.
5. **§5's `ArrivedEarly` (line 368) — hypothetical or abandoned?** There is no `ArrivedEarly` in the
   tree, no rename-table row for it, and no `ADDITIONS` entry. It reads as a *worked example* of
   "add a published key" (the sentence says "add … as `required`"), so it may be deliberately
   future-tense; but the spec's other Good/Base/Bad examples all cite real members. I cannot verify
   it either way and have flagged the identifier, not the intent. Decide whether to mark it
   explicitly hypothetical or rewrite it on `Late`.
6. **§6's two non-tests.** "Regression comparison" and "Effective lines" are listed under "Tests
   Required (assertion points)" but no test and no workflow runs them. Either the heading means
   "checks a change must satisfy" (in which case say so) or those bullets are promising coverage
   that does not exist. Related: §6 says "three harness projects" while `effective-lines.py` is
   documented as being run over four paths.
7. **The `plots/` naming.** The spec says the analyzer outputs `plots/SKIPPED.md`; the frozen
   byte-identical reference copy is `verification/plots-SKIPPED.md`. Confirm that the doc should
   name both roles in one sentence, since an existence check over `plots/SKIPPED.md` will keep
   reporting a miss (it is an output directory, never checked in).
8. **Is nine children too many?** The topic seams are genuine, but `rate-calibers.md` (≈ 62) and
   `udp-source-census.md` (≈ 52) are both "the exact meaning of one published number" and could
   merge into a single ≈ 110-line child if the index table should stay short. I kept them apart
   because the census is a data-structure contract and the caliber is a naming contract, and because
   `index.md` already cites §3.10 by number — but this is a taste call, and the lead may prefer an
   eight-child family.
