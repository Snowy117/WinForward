# Measurement Harness Contract (benchmarks/WinForward.E2E)

> Scope: the end-to-end measurement harness and its contract with any analyzer that reads its
> records. Applies when you add, rename or remove a published field, change plan loading, touch
> the JSONL writers, or judge whether a harness change moved the numbers.

---

## 1. Scope / Trigger

Trigger this spec when a change:

- publishes, renames or removes a key in an arm record (`<arm>.jsonl`), `run.json` or the target ledger;
- changes what plan keys are accepted, or how a bad plan value is reported;
- touches `JsonlSink` / the writers / flush or dispose behavior;
- adds or edits a `catch (ObjectDisposedException)`, or changes what a teardown path publishes (§3.9);
- touches a `*Rate` key or the population behind it (§3.10);
- compares two harness runs (regression evidence) or claims a refactor is behavior-neutral.

The contract lives in `benchmarks/WinForward.E2E.Contracts` (public); the harness references it.
The analyzer (E4) will reference the same project, so a rename is a compile error on both sides,
not a silently empty table cell.

---

## 2. Signatures

```csharp
// Field names: one constant per published key, split per arm family (ArmKeys.*.cs, each ≤400 effective lines).
public static class ArmKeys
{
    public static class Common { public static class Record { public const string Metrics = "metrics"; /* … */ } }
    public static class Loss { public const string Sent = "sent"; /* … */ }
    public static class Latency { public const string TcpSent = "tcp.sent"; /* form A: the dot is part of the key */ }
    public static class Mix { public static class Classes { public static class Udp { public const string Sent = "sent"; } } }
}

// One production write path: a full JSON value (it owns its braces).
public interface IJsonWritable { void WriteTo(Utf8JsonWriter writer); }

public sealed record LossMetrics : IJsonWritable { public required long Sent { get; init; } /* … */ }
public sealed record ArmParameters : IJsonWritable { /* nullable members; null = this arm does not publish it */ }

public enum JsonlPolicy { Propagate, SwallowAndCount }

public sealed class JsonlSink : IAsyncDisposable
{
    public JsonlSink(string path, JsonlPolicy policy, Action<Utf8JsonWriter>? envelope);
    public long WriteErrors { get; }
    public ValueTask WriteAsync(Action<Utf8JsonWriter> body, CancellationToken cancellationToken);
    public ValueTask CompleteAsync();          // never throws; call it inside the arm's failure boundary
    public ValueTask DisposeAsync();           // backstop only
}

// Regression comparison and documentation gates (repo: benchmarks/WinForward.E2E/scripts/)
//   compare-records.py <baseDir> <afterDir> --normalize research/record-normalize.json
//       [--band baseline/jitter-band.json | --write-band <file>]
//       [--rename-table research/contract-rename.json [--batch B2]] [--strict] [--explain-classes]
//   contract-inventory.py            # publishes research/contract-inventory.json + contract-rename.{json,md}
//   jsonl_paths.py                   # the one flattening alphabet the inventory, the comparator and the table share
//   normalize-pattern-hits.py        # reports config patterns that match nothing
//   check-readme-contract.py         # README contract table -> ArmKeys constants -> write sites (§3.11)
//   effective-lines.py               # the 400-effective-line gate
//   cli-snapshots.py                 # records and replays the CLI surface (exit code + stdout + stderr)
//   oracle-diff.py                   # the frozen-oracle differ the analysis is judged with (semantic by default)
//   check-fairness.py                # asserts the fairness disclosures against the analysis's own tables.md
```

---

## 3. Contracts

### 3.1 Path alphabet (the analyzer's addressing scheme)

`canonical_path = join('/', member names)`, where a member name is the **literal** JSON member name:
a dot inside a name stays whole. Examples: `metrics/tcp.sent`, `metrics/classes/udp/sent`,
`metrics/latency/udp.sent`, `metrics/parameters/seconds`, `records/run.json`. `jsonl_paths.py` is the
single flattener used by the inventory, the comparator and the rename table.

### 3.2 Three states, never conflated

| State | Meaning | Renderer |
|---|---|---|
| key absent | this arm does not publish the quantity (conditional block) | `n/a` |
| key present, value `null` | published, not measurable (zero denominator, no sample) | blank cell with a reason |
| key present, value `0` | measured zero | `0` |

Ratio and per-second helpers live in `Contracts` (`JsonRate.Rate`, `JsonPerSecond.PerSecond`) and return
`double?` with `null` for a zero denominator / zero tick span. Do not write a bare `(double)a / b`.

### 3.3 Sink policies

| Policy | Used by | I/O + body failures | close/dispose failures |
|---|---|---|---|
| `Propagate` | client arm files | thrown to the caller (counted first) | counted, never thrown |
| `SwallowAndCount` | target ledger, target summaries | counted, never thrown | counted, never thrown |

The body is serialized in memory first and the record is written in one step, so a throwing body leaves
**no half line**; cancellation is observed at the lock, never mid-record. Client sinks flush on a timer
(1 Hz) so a crash keeps the samples already written. Callers read `WriteErrors` back and mark the arm
failed, so a lost record still leaves `run.json.failed = true`.

### 3.4 Plan loading is fail-closed

Every arm kind has one descriptor (`Client/Arms/ArmKind.cs`: name, accepted keys, validator, runner).
Unknown keys, wrong JSON types, fractional values in integer keys, out-of-range values and colliding
output file names are **load errors that name the arm and the key** — never a silent fallback.
`0` stays a meaningful "not declared" for keys documented as such. Reported exit codes:
`0` all green, `1` arm-level failure with records, `2` usage/plan error.

### 3.5 Comparison classes (regression judgement)

| Class | Examples | Verdict |
|---|---|---|
| structural | key set, type, array length, string values, `arms[].file` | must match or be declared by the rename table |
| identity | `*/pid`, `sources/port`, version/hash, paths | presence + type only |
| contract | counters, `gates.*` (except overridden entries), **booleans** | within the per-key band; no `--band` means band 0 |
| reading | `latency/*`, `*Us`/`*Ms`/`*Bytes`, CPU/memory/throughput | informational; never fails, listed under `--strict` |

A batch that claims to be behavior-neutral must attach the `--strict` reading summary and explain every
out-of-band reading. A contract finding on a zero-width band must be confirmed by a second run with the
same binary before it is called a regression.

### 3.6 Lane seam: engine / policy / transport

An arm's send and receive loops live behind three collaborators (`Client/Lanes/`):

| Collaborator | Owns | Must not |
|---|---|---|
| `ILaneTransport` (adapter) | connect (`OpenAsync` returns a result, never throws), one send, one receive | judge wire validity, dispose the policy/engine |
| `ILanePolicy` | window admission + in-flight, frame building, reply classification, every receive-side counter | count on the receive thread, block, allocate in `BuildRequest` |
| `LaneEngine<TTransport>` | pacing, the offer loop, the bounded defer queue, the grace drain, the **send-side** counters (`LaneCounts`) | keep a second copy of the window or of any policy counter |

`LaneReceiveKind ∈ {Payload, EndOfStream, Malformed, IoError}` describes what arrived, not whether it is valid:
the TCP adapter maps `BadMagic`/`BadLength` to `IoError` (framing lost, counted then stop), `BadChecksum` to
`Malformed` (counted, keep reading), `EndOfStream` to `EndOfStream`; an oversized datagram becomes
`Malformed` with `FrameDecodeError.Truncated`, never a silent "corrupt".

Thread contract, in this order:

1. the **receive** thread decodes, classifies and enqueues a settlement carrying `ReceivedTicks` — no counters;
2. the **send** thread calls `Settle(now)` after `Pacer.WaitUntil` and before the next `BuildRequest`, and that is
   the only place pending removal, RTT (from `ReceivedTicks`, never from the settle time), histograms, `inFlight--`
   and receive-side counters change;
3. at arm end: stop offering → bounded grace (keep receiving and settling until the book is empty or the drain
   limit) → cancel and join the receive loop → a final `Settle` → compute gates and metrics.

Per-slot sequence is strictly serial (`BuildRequest → SendAsync → OnSent`, `OnSent` before the next
`BuildRequest`), and `BuildRequest` is idempotent: a deferred slot is retried with its original sequence and
intended instant, once per slot in `Supplied`. `WouldBlock` is sampled from `IsCompleted` **before** the await,
and a would-block that ends in an asynchronous failure increments both `SendWouldBlock` and `SendFailures`.
`DeferredPending` (queue occupancy at teardown) is what completes `outstandingAtTeardown`; never derive it by
subtracting the other counters (that only holds when a policy never returns `Skip`).

### 3.7 Naming a record key

One `const string` per **leaf per nesting level** — `Ledger.TcpSummary.Connections` and
`Ledger.TargetSummary.TcpTotals.Connections` are two constants because they are two paths. The envelope
(`utc`, `label`, `type`), the four arm families and the four ledger families each get their shard; conditional
keys are listed in the shard's `<remarks>`; a data-driven container's member names get a `*Names` array next to
the record. Adding a writer means adding it to the literal gate's file list — the gate only checks the files it
is told about, and a new file with literals stays green until it is listed.

### 3.8 Proving a refactor is behaviour-neutral

Layered evidence, cheapest first; a claim must name which layer it used:

1. **token multiset** over the moved files (comments stripped, string literals kept whole): differences must be
   limited to visibility widening, cross-file qualification and the new type declarations;
2. **ordered check**: every line of the new files maps monotonically onto the old file's line sequence
   (catches swaps and reordering the multiset cannot see);
3. **per-method body equality**: extract each method and compare bodies after normalising comments, visibility
   and type qualification — this is what catches two statements swapped inside one method;
4. **registered difference classes**: anything left (a constant changing owner, a nested type being promoted,
   members reordered) is written down with its reason and its behavioural argument, not silently absorbed;
5. **runtime equivalence**: `compare-records.py` on a frozen baseline with the same-binary pair difference as
   the noise floor, plus the zero-width published keys compared value by value.

A refactor that needs a real behaviour change (a failure path that used to kill the arm, a missing sent-set
check) registers it as an intentional change with the affected keys named.

### 3.9 Teardown books no data point (the `ObjectDisposedException` vocabulary)

Trigger: any `catch (ObjectDisposedException)` over a socket, and any decision about what a teardown
(a closed arm, a stopped target, a disposed socket) may publish. The rule is uniform: **teardown is not
a measurement**, so such a catch books no counter, no verdict and no observation. The five shapes below
are the whole allowed vocabulary, and `ObjectDisposedCatchGateTests` holds the registry (31 catches in
19 files under `benchmarks/WinForward.E2E`; the three in `Contracts/Json/JsonlSink.cs` are the sink's own
swallow-and-count policy, D14.7, and stay out of it):

| Shape | Body | Published effect |
|---|---|---|
| ignored | a comment only | none — a loop/worker whose socket the teardown closed simply ends |
| ends | `return;` / `return false;` / `break;` / `return "unknown";` | none |
| connect failure | `return new LaneOpenResult(Ok: false, …)` | the caller's own result: this *arm* did fail to connect |
| no verdict | `return CommandOutcome.TornDown;` | `TcpTargetServer` writes no `tcp` record and bumps no verdict bucket (`command.Outcome` is `null`) |
| no observation | `result._status = ExchangeStatus.Cancelled;` | the REL attempt is published as `cancelled`, never as an `otherError` the peer was seen to produce |

Two measured properties bound how much of this is behavioural, and both are load-bearing for anyone
writing the next teardown path:

1. a socket disposed **before** the next socket call throws `ObjectDisposedException` (so these arms are
   reachable exactly when a socket object is reused after disposal);
2. a socket disposed **under a pending receive** ends that receive as
   `SocketException(OperationAborted)` ("Operation canceled"), which the socket sites book in a
   socket-error arm — their own, or the transport's one level down (`LaneEngine`; the five E3-e moved
   sites all have their own). A real teardown race therefore never reaches the teardown arm; the arms
   are defensive, and the source gate (not a driven test) is what keeps them honest. Two of the 31 are
   not sockets at all (`TargetLog`, `ResourceSampleWriter` wrap stderr) and have no socket-error arm:
   there the catch is only a closed-pipe guard, and its shape is the gate's business too.
   `TcpConnectionProtocol` is the exception worth driving: it takes its reader as an argument, so a
   socket disposed before the first read reaches its arm (`AConnectionTornDownUnderTheProtocolPublishesNoVerdict`).

REL keeps the teardown attempt inside `outcomes` (its `Observed` stays the arm's default) because
`sum(outcomes) == connectAttempts == scheduledAttempts` is a live analyzer identity
(`WinForward.E2E.Analysis/Checks/IdentityChecks.cs`, `ReliabilityInvariantsOf`); what a teardown changes
there is the published `status` (`cancelled`, not `exchanged`) and the `otherError` flag. A site whose
counter is a *census* rather than a measurement keeps counting: `TcpTargetServer` still increments
`connections` at accept, so a torn-down connection can make `connections` exceed the number of `tcp`
records.

### 3.10 One rate name, one caliber: `achievedRate`

`metrics/achievedRate` means one thing in every arm: **requests successfully sent per elapsed second** —
the count of requests the socket accepted over the arm's elapsed span (`JsonPerSecond.PerSecond`,
`null` when no time passed). One numerator per arm, and no arm is allowed to keep the old
"completed responses" caliber under this name:

| Arm | Numerator | Site |
|---|---|---|
| LAT / LATLOAD | `tcp.SentOk` / `udp.SentOk` | `LatencyMetricsWriter` |
| LOSS | `UdpReliabilityTracker.SentOk` | `LossArm` |
| DNS / DNSALT | `sent` (queries written to the socket) | `DnsArm` |
| REL | attempts whose request send completed (`TransferMeasured`) | `ReliabilityMetricsWriter` |
| PERSIST | `_sentRequests` (rounds whose frame send completed) | `PersistentArm` |

The *completion* caliber is a different **name**, never the same one: PERSIST publishes
`metrics/completionRate` (responses per elapsed second), which is the population `achievedRate` carried
before the unification. So a run with `requests > responses` has `achievedRate > completionRate`, and the
rename is value-preserving by construction: `completionRate == JsonPerSecond.PerSecond(responses, ticks)`,
the old expression word for word.

```csharp
public static partial class ArmKeys
{
    public static class Persistent
    {
        public const string AchievedRate = "achievedRate";     // requests whose send completed per second
        public const string CompletionRate = "completionRate"; // responses per second (new key, E3-e)
    }
}

public sealed record PersistentMetrics : IJsonWritable
{
    public required double? AchievedRate { get; init; }
    public required double? CompletionRate { get; init; }
}
```

A new published key is registered in `scripts/contract-inventory.py`'s `ADDITIONS` (here
`metrics/completionRate`) **before** `compare-records.py --rename-table … --batch B2` runs (D19.3 A):
without the row the one-sided path reads as a structural difference and the batch's own gate goes red.

### 3.11 The README's contract table is checked, not trusted

`benchmarks/WinForward.E2E/README.md`'s "Which keys are contract" section is what a reader consults
when a cell looks wrong, and it is the one place a rename can go stale without anything failing: the
analysis resolves most paths by arm kind, so a misspelt key renders an empty cell and the report still
builds. The section is therefore a **gate**, not prose:

```console
$ python3 benchmarks/WinForward.E2E/scripts/check-readme-contract.py
111 key(s) checked against 401 declared constant path(s): ok        # exit 0
```

The checker reads the section (tables *and* prose), and for every backticked key path it names — and
for the five root names the table spells bare — it:

1. resolves it to a `const string` in `ArmKeys.*.cs` — the class chain spells the path
   (`ArmKeys.Common.Gates.ClientSendLoss` is `gates/clientSendLoss`), a `<name>` placeholder resolves
   to its container, and a `metrics/latency/…`/`metrics/loss/…`/`parameters/latency/…`/
   `parameters/loss/…` token resolves as the `BASE` phase key one level down;
2. requires that constant to be **referenced by a write site** — `Client/`, `Target/`, `Wire/`,
   `Cli/`, `Program.cs` or the `Contracts` records' own `WriteTo` methods; a test is not a write site,
   because a key only a test writes is a key no run publishes, and a line-commented call is not one
   either, because the scan drops line comments before it looks for the reference;
3. fails on any token that is an `old_path` of `research/contract-rename.json` whose `new_path`
   differs, so a rename cannot leave the table describing a record that no longer exists.

Three token classes are declared rather than inferred, so that dropping them is an edit and not a
silent pass: `metrics/clientSendLoss` (the analysis's legacy gate fallback, published by no current
latency arm), and the two spellings the section documents as carrying nothing — `parameters/window`
and `parameters/loss.lossWindowMs`, which are plan-key spellings that the analysis reads at neither
path (it reads `parameters/inFlightWindow` and `parameters/loss/lossWindowMs` instead). A key that is
contract but that no table reads (the two out-of-range counters and `completionRate`) belongs in the
section's prose paragraph, where the same check covers it.

The same section names its authority for the record's *shape*: every key it lists must be spelled the
way `ArmKeys` spells it, and the write site is what proves the record carries it.

### 3.12 The UDP source census is a per-interval table (T2, 2026-10-08)

`udpSummary.sources[]` is what the *interval* saw and `sourceOverflow` is what the interval could not
place; both readings are unchanged. What T2 changed is the capacity behind them, and the choice is
recorded here rather than left in the code:

- One fixed table of 64 slots per receive loop. A datagram takes the slot of its `(address, port)` when
  its endpoint already holds one, and otherwise the lowest-numbered slot **no datagram of the current
  interval has touched yet** — the idleness clock restarts at every `Harvest`, so every slot is claimable
  at the moment an interval opens. A claim that changes hands starts its count over, so the delta
  `Harvest` publishes is still the interval's own.
- `Harvest` ends the interval. The previous behaviour — "an endpoint keeps its slot for the life of the
  server" — made the table's capacity the process's lifetime distinct endpoints, so a campaign that
  outlived 64 endpoints went blind **permanently**: E5-b's ledger held exactly 64 source endpoints and
  then published `sources: []` with `received == sourceOverflow` for 42 minutes. Capacity is now the
  interval's active endpoints per loop, which is what the produced delta was always about.
- **The trade-off, stated.** A slot the opening interval has not touched yet may be handed to another
  endpoint, so an endpoint that appears once and never again is no longer listed in later intervals (it
  never had a non-zero delta there anyway) — and an endpoint that keeps arriving can lose its slot to a
  fresh endpoint that arrives before its own first datagram of the interval. That costs it its running
  count, not its record: its next datagram claims a fresh slot, and that claim's whole count is published
  as this interval's datagrams under the endpoint's own key, because `sources[]` is keyed by endpoint and
  not by slot. `sourceOverflow` therefore describes an interval that touched more than 64 endpoints on
  one loop — the slots go to the **first** 64 endpoints of the interval, not to the busiest — rather than
  a census that is dead for the rest of the run. A larger table was rejected: it only postpones the same
  blindness and multiplies the per-datagram scan.
- **The bounded boundary effect.** A re-claim overwrites the previous claim's count, and a datagram that
  lands in the summariser's read window of the closing interval can be lost with it. Measured at campaign
  shape (8 loops, 60 continuous + 400 historical + 100 probe endpoints, 32,040 datagrams): one datagram,
  i.e. `received = censused + sourceOverflow + 1`. It is inside the analyzer's datagram band (1 %, or one
  second of the arm's own rate) and is the price of a bounded table; the alternative is the permanent
  blindness above. The window scales with how often `Harvest` runs against how long an endpoint lives: at
  the shipped 1 Hz a churn run of 8 loops and 439,445 datagrams lost nothing, while a summariser looping
  every few microseconds discards the tail of nearly every short-lived endpoint — the one-second summary
  interval is part of this contract, not an implementation detail to shorten casually.
- **The handshake.** One receive loop writes a table and the summariser is the only reader. A claim
  publishes `_claim` last with a release `Volatile.Write`, so a reader of that value sees the identity
  and count written before it; a re-claim writes `_claim = 0`, crosses `Interlocked.MemoryBarrier()`, then
  moves the identity and publishes the new claim. `Harvest` reads `_claim`, the identity and the count,
  then `_claim` again, and leaves a slot claimed under the read to the next interval, so a torn claim can
  never be published as one endpoint's address beside another's count. No allocation, delegate or LINQ is
  on the datagram path; the only added work is one volatile read of the interval's epoch per datagram.

---

## 4. Validation & Error Matrix

| Condition | Result |
|---|---|
| plan key unknown to the arm kind | exit 2, `arm '<name>' (kind '<kind>'): unknown key '<key>'` |
| key present with the wrong JSON type | exit 2, `'<key>' is <value>, which is not a string/number` |
| fractional value in an integer key | exit 2, `'<key>' is <value>, which is not an integer` |
| integer outside the key's domain | exit 2, `'<key>' is <value>, which is outside <min>..<max>` |
| two arm names mapping to one file | exit 2, both original names + the mapped file |
| scheduled sequence count above the tracker's space | exit 2, arm + highest sequence + `MaxSequence` |
| arm throws / is interrupted | exit 1; an `error` record (`type,arm,kind,label,error,message,detail,startedTicks,endedTicks`) + `run.json.failed` |
| sink write fails | the record is lost but counted; the arm is failed; `run.json` still written |
| ledger / summary write fails | counted in `ledgerWriteErrors`; the target keeps serving |
| a teardown catch books a counter, verdict or observation | `ObjectDisposedCatchGateTests` fails, naming `file:line` and the shape it read |
| a published key is missing from the rename table's `ADDITIONS` | `contract-inventory.py rename` exits 1: "the fresh run publishes paths the table does not declare" |
| a README contract key has no `ArmKeys` constant, no write site, or is a renamed-away spelling | `check-readme-contract.py` exits 1, one `FAIL: <token>: <reason>` line per key; exit 2 when the README or the rename table cannot be read, when the `ArmKeys` shards declare nothing, or when the section names no key at all |
| a harness `.cs` file passes the 400-effective-line limit | `effective-lines.py <paths>` exits 1, naming the file and its count |

---

## 5. Good / Base / Bad Cases

- **Good**: add `ArrivedEarly` to `LossMetrics` as `required`, add `ArmKeys.Loss.ArrivedEarly`, write it in
  `WriteTo`; the explicit test factory fails to compile until the new member is set, and the shape test
  compares the emitted path set with the declared one in both directions.
- **Base**: an arm that never runs still publishes `metrics: {}` and `parameters: {}` via `EmptyMetrics`.
- **Bad**: `outcome.Metrics["sentOk"] = …` (string literal), a `Dictionary<string, object?>` carrier, or a
  field written as `null` because the value was unavailable — the first two are gated by
  `JsonKeyLiteralGateTests`, the third conflates "not measured" with "not published".
- **Good (teardown)**: the empty catch (`catch (ObjectDisposedException) { /* the arm's own teardown closed it */ }`)
  and the two explicit answers (`return CommandOutcome.TornDown;`,
  `result._status = ExchangeStatus.Cancelled;`) — the shape is in the registry, so nothing is fabricated and
  nothing is silently absorbed either.
- **Base (teardown)**: a clean run's teardown counters stay where they were (`classes/page/errors`,
  `classes/bulk/errors`, `dnsSummary/tcpAborted`, `tcpSummary/verdicts/error` and `outcomes/otherError` all
  keep their values), because no harness path can dispose a socket under a call that owns it (§3.9).
- **Bad (teardown)**: `catch (ObjectDisposedException) { Interlocked.Increment(ref counters._bulkErrors); }`
  — a fabricated bulk error; and `catch (ObjectDisposedException) { attempt.OtherError = true; }` — an
  `otherError` the peer was never seen to produce.
- **Good (rates)**: PERSIST publishes `completionRate = responses/elapsed` and
  `achievedRate = sentRequests/elapsed`, and a run with unanswered requests has
  `achievedRate > completionRate` while an answered run has them equal — one numerator per name, and the
  old value is still readable under the new completion name.
- **Bad (rates)**: `AchievedRate = JsonPerSecond.PerSecond(state._responses, …)` in PERSIST (the completion
  population under the send name) or `PerSecond(attempts.Length, …)` in REL (counts attempts that never
  sent a request).
- **Good (README keys)**: the contract table names `gates/clientSendLoss`, `metrics/tcp.sent` and
  `metrics/classes/udp/sentOutOfRangeSequences`; each resolves to a constant that a write site references,
  so `check-readme-contract.py` prints `ok` and exits 0.
- **Base (README keys)**: a plan-key spelling the section names as carrying nothing
  (`parameters/window`) is carried in `DOCUMENTED_NON_KEYS` instead of failing — the section states
  why no record has it and which path does, so the checker reads it as a statement rather than a hole.
- **Bad (README keys)**: the table still names `metrics/tcp.sentOk` (renamed to `metrics/tcp.sent`), or a
  key that exists only in a test factory, or a key whose only writer was commented out, or
  `metrics/classes/udp/sentOutOfRangeSequences` while the MIX writer does not reference
  `ArmKeys.Mix.UdpClass.SentOutOfRangeSequences` — the checker exits 1, naming the token and the reason.
- **Bad (phase keys)**: spelling BASE's nested keys as `metrics/latency` + a fresh constant instead of the
  latency arm's own (`ArmKeys.Latency.UdpSent`) — the checker's phase rule would still resolve it, but the
  writer would not, so the second check (write site) is what catches a copy that drifted.

---

## 6. Tests Required (assertion points)

- **Shape, per kind** (`tests/WinForward.E2E.Tests/Shapes/*Shape.cs`): explicit factory over the typed record;
  flatten the **production** writer output and compare path sets with the declared keys in both directions;
  null cases write the key with `null`; conditional blocks are omitted whole (and only by their flag);
  array arity equals the plan value (`desktops`, `lanes`); data-driven containers keep their declared members.
- **Literal gate** (`JsonKeyLiteralGateTests`): scans the whole file (not line by line) for key-position
  literals in `Client/Arms/**` and `ClientRunner.cs`, and fails when the scan root or the known-key set is empty.
- **Plan loading** (`PlanFileValidationTests` / `PlanFileTests`): all shipped plans load, the built-in plan
  loads, and each rejection fixture yields exit 2 with the arm and key named.
- **Sink matrix** (`JsonlSinkTests`): Propagate throws on body/write but not on close; SwallowAndCount counts
  all four failure kinds; a cancelled write never cuts a record in half; the periodic flush surfaces data.
- **Lane seam** (`tests/WinForward.E2E.Tests/Lanes/*`): with a fake transport — send outcomes, would-block
  sampled before the await (two facts, one per mutation), defer FIFO with a retry that does not re-count
  `Supplied`, the three grace exits, settlement carrying the receive time, a concurrency identity
  (`delivered == sent == settled`, each sequence exactly once) and a zero-allocation gate whose counter-proof
  fails when `BuildRequest` allocates. With a real transport: the same gate on the sending path, and the
  send path must keep using the `ReadOnlyMemory` overload (`byte[]`/`ArraySegment` binds an overload that
  allocates per call — the fake gate cannot see that).
- **Ledger shape** (`LedgerShapeTests`): drive the production writers (real connection, real datagram) and
  compare the flattened ledger paths with the declared keys in both directions, per record family and in
  declared order; the three states; the `sources` array written even when empty; the conditional `dnsAlt`
  block omitted whole.
- **Source census** (`SourceCensusTests`, §3.12): a continuing endpoint is published as the interval's own
  delta across intervals; a wave no datagram touched for an interval is reclaimed whole by the next wave
  (the fact fails against a census that never reclaims); a full table reports the interval's unplaced
  datagrams as `sourceOverflow`; and a summariser running on another thread beside the receive loop
  publishes every recorded datagram exactly once, under its own port, with nothing unplaced.
- **CLI snapshots** (`CliSnapshotTests` + `scripts/cli-snapshots.py`): 28 commands (both helps and every error
  path) recorded as exit code + stdout + stderr bytes; the replay test runs `Program.Main` in a
  non-parallel collection and restores `Console.Out`/`Error`/cwd; a reworded message must turn it red.
- **Effective lines** (`scripts/effective-lines.py`): the three harness projects must report nothing at a
  400-line limit; the counter strips blanks, `//` and `/* */` the way the compiler sees them (a `//` inside a
  string is not a comment, a multi-line raw string counts as code).
- **README contract keys** (`scripts/check-readme-contract.py`): the harness README's contract section must
  report `ok` and exit 0 against `ArmKeys.*.cs` and `research/contract-rename.json`; the three assertion
  points are that the parsed constant set is non-empty, that every token resolves to a constant a write site
  references, and that no token is a renamed-away spelling. A reader that skipped the `ArmKeys` shards, or a
  rename that only touched the record, must turn it red. The two emptiness guards are themselves checks:
  a shard set that declares nothing, or a section that names no key at all, is an exit 2 rather than a
  vacuous `ok`.
- **Regression comparison**: `run1 vs run1` and `run1 vs run2 --band` compare clean; a mutated contract
  counter fails while a mutated pid and a mutated latency reading do not.
- **Teardown vocabulary** (`ObjectDisposedCatchGateTests` + `ObjectDisposedTeardownTests`): the registry
  holds every `catch (ObjectDisposedException)` site with its shape in source order, asserts the baseline
  counts (31 sites / 19 files), and refuses any body that books a counter — re-adding an increment, or
  reverting `TornDown`/`Cancelled`, turns it red (mutation-checked). The behavioural half drives the
  protocol's torn-down arm (`Outcome is null`), the arm-cancelled arm (still `TcpVerdict.Error`), the two
  MIX connect-failure arms (`_bulkErrors == 1`, `_pageErrors == 13`) and the DNS aborted-read arm
  (`tcpAborted == 1`).
- **Rate calibers** (`PersistentRateCaliberTests`, `Shapes/PersistentShape`): the shape factory carries
  `completionRate` as a nullable reading and the declared-key comparison covers it; a real PERSIST run with
  every request answered publishes `completionRate == achievedRate > 0`, and a run whose peer never answers
  publishes `completionRate == 0 < achievedRate`; `contract-inventory.py rename` must report the key as
  `added` and `compare-records.py --rename-table … --batch B2` must stay at `contract=0`.

---

## 7. Wrong vs Correct

#### Wrong
```csharp
outcome.Metrics["udp.sentOk"] = sentOk;          // literal key, duplicated spelling, unchecked rename
public Dictionary<string, object?> Metrics { get; init; }   // no compile-time member coverage
sink.WriteAsync(w => { w.WriteString("type", "result"); }, ct);  // token may cancel mid-record
```

#### Correct
```csharp
var metrics = new LatencyMetrics { Tcp = tcpBlock, Udp = udpBlock, /* required members */ };
public required IJsonWritable Metrics { get; init; }         // one production path
await sink.WriteAsync(w => metrics.WriteTo(w), CancellationToken.None);  // cancellation at the lock only
```

#### Wrong — the seam copies the bookkeeping
```csharp
// engine increments a window counter AND the policy keeps its own; nothing fails until the numbers drift
if (++_inFlight > window) { _deferred.Enqueue(frame); }
policy.OnReply(payload, Clock.Now);   // receive thread books RTT and counters
policy.Settle(Clock.Now);             // and the send thread books them again
```

#### Correct — one writer per quantity
```csharp
var decision = policy.BuildRequest(sequence, intended, buffer, out var length);  // policy owns the window
if (decision == LaneSlotDecision.Send) { var send = transport.SendAsync(...); var block = !send.IsCompleted; ... }
policy.OnReceive(result, payload, receivedTicks);   // decode + classify + enqueue, no counters
policy.Settle(Clock.Now);                           // the only settlement point, on the send thread
```

#### Wrong — teardown booked as a measurement
```csharp
catch (ObjectDisposedException)
{
    Interlocked.Increment(ref counters._pageErrors);   // the harness's own teardown, published as a page error
}

catch (ObjectDisposedException)
{
    return new CommandOutcome(…, new TcpModeOutcome(TcpVerdict.Error, 0, 0, truncated: false));   // a verdict for a connection nothing measured
}
```

#### Correct — teardown books nothing, the failure arms keep counting
```csharp
catch (ObjectDisposedException)
{
    /* teardown closed the socket first: a teardown is not a page error and books nothing (D19.2 ⑨) */
}

catch (ObjectDisposedException)
{
    // A socket disposed under the connection measured nothing, so no verdict is published (D19.2 ⑨).
    return CommandOutcome.TornDown;
}

catch (SocketException)
{
    Interlocked.Increment(ref counters._pageErrors);    // a socket error the harness did see is still a page error
}
```

#### Wrong — the README keeps a spelling the record no longer has
```markdown
| `latency` (LAT, LATLOAD) | `metrics/tcp.sentOk`, `metrics/udp.sentOk`, `gates/clientSendLoss` |
```
The rename landed in `ArmKeys.Latency.TcpSent`/`UdpSent` and in every writer, and nothing fails: the
analysis resolves the path by arm kind, finds nothing, and renders the cell empty. The report still
builds, and the document now describes a record nobody publishes.

#### Correct — the table names the constant, and a gate holds it there
```markdown
| `latency` (LAT, LATLOAD) | `metrics/tcp.sent`, `metrics/udp.sent`, `gates/clientSendLoss` … |
```
```console
$ python3 benchmarks/WinForward.E2E/scripts/check-readme-contract.py
111 key(s) checked against 401 declared constant path(s): ok
```
Both halves of the check matter: the constant must exist **and** a write site must reference it, so a
key that survives only in a shape-test factory is still a failure.

> **Gotcha — a published key with no reader is still contract.** `sentOutOfRangeSequences`,
> `outOfRangeSequences` and `completionRate` are read by no table and gate, so a rename of any of them
> leaves every artifact byte-identical while the record's meaning changes under it. That is why
> `check-readme-contract.py` reads the contract section's prose too: the paragraph that documents such a
> key is where its spelling has to be pinned.

> **Gotcha — the noise floor is not zero.** Two runs of the *same* binary differ: contract counters move on
> their own (target UDP echo ordering, boot clocks, host sampling). Before calling a batch's difference a
> regression, run the same binary twice and compare the two difference lists; also check whether the moving
> keys sit on a zero-width band, which means the band was never measured rather than that the value is stable.

> **Gotcha — stale audit premises.** Defect lists written against an older tree go stale. Before
> implementing an audit item, re-verify it against the current code (a grep or a failing test); record
> the verdict as `implemented | fixed | deferred` with a re-runnable command. In this repo the E2E audit's
> §2 list was ~10/13 already implemented when the refactor started.

---

## 8. The analyzer

The analyzer is `benchmarks/WinForward.E2E.Analysis` (C#, shares `Contracts`). It replaced the Python
reference (`analyze.py`), which is no longer in the tree; the reference survives only as the frozen
`verification/golden/` output the differ is run against. Entry point:
`benchmarks/WinForward.E2E.Analysis/scripts/analyze.sh` — it builds once, then execs the binary with the
caller's working directory and arguments untouched (`--raw --out --ledger --flat --warmup-seconds
--resamples --seed`; defaults `../raw`, `..`, `5.0`, `10000`, `20261006`, and `--ledger` may be repeated.
`AnalysisOptions.DefaultMinPasses = 3` is a constant rather than a flag: fewer than three passes is
reported as `inconclusive`).
It reads JSONL with `JsonDocument`: the envelope-level names come from `ArmKeys` (a compile error on a
rename), while the paths inside `metrics`/`parameters` are literals addressed positionally on `/`, because
only the arm's kind knows which map a path belongs to — so a metric rename is a two-sided edit and
`scripts/check-readme-contract.py` gates the documented half of it (§3.11). No reflection, no source
generation, no `InternalsVisibleTo`. Outputs `tables.md` (sixteen `## N.` sections), `verdict.json`
(fourteen top-level keys) and `plots/SKIPPED.md` (written unconditionally).

What constrains the analyzer now:

| Artifact | Purpose |
|---|---|
| `verification/synthetic-tree.tar.gz` | the campaign every regression runs on (deterministic tar: sorted entries, fixed mtime/owner) |
| `verification/golden/{py-tables.md,py-verdict.json}` | the reference output for that tree |
| `verification/golden/{cp-random-vectors,py-number-vectors,py-json-vectors}.json` | the primitives' vectors, not compared by the differ |
| `verification/synthetic/make_tree.py` + `FROZEN.md` | how the tree is built, and how to refreeze (any change means refreezing and re-diffing from batch 1) |
| `scripts/oracle-diff.py` | the differ: `--mode semantic` (default) or `--mode byte`, `--batch N`, `--tolerance` |
| `verification/row-profiles.json` + `scripts/check-fairness.py` | the fairness rules as data, asserted against the C# output |
| `verification/check-boundary-trees.py` | the knob trees (window overflow, undecodable, truncated, zero denominator) |

Differ contract: exit `0` equal, `1` different, `2` **something that should exist does not** — a missing
slice or an unreadable artifact is never a pass. Semantic mode keeps headings, column names, row identity,
cell counts and cell kinds exact, compares numbers within one unit of the reference's printed precision,
decodes strings before comparing, and ignores object key order and row order. Byte mode is a structure-surface
regression, not a batch criterion. Every section and every verdict key belongs to exactly one batch in
`BATCH_SECTIONS`; a new section must be added there, and a key without a batch is a usage error.

A boundary state the reference cannot render (a zero denominator makes it raise) is asserted from the C#
side alone: build the knob tree, assert the C# output, and add a negative control that must turn red.
Records whose numbers are summed for the report follow CPython's compensated summation when the reference
does; that rule has its own anchor test rather than relying on a comparison.

