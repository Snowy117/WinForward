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

// Regression comparison (repo: benchmarks/WinForward.E2E/scripts/)
//   compare-records.py <baseDir> <afterDir> --normalize research/record-normalize.json
//       [--band baseline/jitter-band.json | --write-band <file>]
//       [--rename-table research/contract-rename.json [--batch B2]] [--strict] [--explain-classes]
//   contract-inventory.py            # publishes research/contract-inventory.json + contract-rename.{json,md}
//   normalize-pattern-hits.py        # reports config patterns that match nothing
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

---

## 5. Good / Base / Bad Cases

- **Good**: add `ArrivedEarly` to `LossMetrics` as `required`, add `ArmKeys.Loss.ArrivedEarly`, write it in
  `WriteTo`; the explicit test factory fails to compile until the new member is set, and the shape test
  compares the emitted path set with the declared one in both directions.
- **Base**: an arm that never runs still publishes `metrics: {}` and `parameters: {}` via `EmptyMetrics`.
- **Bad**: `outcome.Metrics["sentOk"] = …` (string literal), a `Dictionary<string, object?>` carrier, or a
  field written as `null` because the value was unavailable — the first two are gated by
  `JsonKeyLiteralGateTests`, the third conflates "not measured" with "not published".

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
- **CLI snapshots** (`CliSnapshotTests` + `scripts/cli-snapshots.py`): 28 commands (both helps and every error
  path) recorded as exit code + stdout + stderr bytes; the replay test runs `Program.Main` in a
  non-parallel collection and restores `Console.Out`/`Error`/cwd; a reworded message must turn it red.
- **Effective lines** (`scripts/effective-lines.py`): the three harness projects must report nothing at a
  400-line limit; the counter strips blanks, `//` and `/* */` the way the compiler sees them (a `//` inside a
  string is not a comment, a multi-line raw string counts as code).
- **Regression comparison**: `run1 vs run1` and `run1 vs run2 --band` compare clean; a mutated contract
  counter fails while a mutated pid and a mutated latency reading do not.

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

> **Gotcha — the noise floor is not zero.** Two runs of the *same* binary differ: contract counters move on
> their own (target UDP echo ordering, boot clocks, host sampling). Before calling a batch's difference a
> regression, run the same binary twice and compare the two difference lists; also check whether the moving
> keys sit on a zero-width band, which means the band was never measured rather than that the value is stable.

> **Gotcha — stale audit premises.** Defect lists written against an older tree go stale. Before
> implementing an audit item, re-verify it against the current code (a grep or a failing test); record
> the verdict as `implemented | fixed | deferred` with a re-runnable command. In this repo the E2E audit's
> §2 list was ~10/13 already implemented when the refactor started.
