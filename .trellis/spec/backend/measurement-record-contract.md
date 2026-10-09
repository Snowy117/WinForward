# Measurement Record Contract

> How a published key is spelled, written, published and loaded: the `ArmKeys` shards, the write path,
> the `/`-joined path alphabet, the three states a value can be in, the sink policies, and fail-closed
> plan loading. Read it when you add, rename or remove a published key, touch a JSONL writer or a record
> type, or change what a plan may declare. Family hub: [measurement-harness.md](./measurement-harness.md).
> A run that fails mid-write is [measurement-run-lifecycle.md](./measurement-run-lifecycle.md)'s subject;
> the gates that hold these spellings are [measurement-tooling.md](./measurement-tooling.md)'s.

## One spelling per published key

Field names live in `benchmarks/WinForward.E2E.Contracts` (`ArmKeys.*.cs`), a public project both
binaries reference, so a rename is a compile error on both sides — the family invariant the
[hub](./measurement-harness.md) states.

- `ArmKeys` is `partial`, one file per record family, each shard inside the repository's 400-effective-line
  limit ([directory-structure.md](./directory-structure.md)). Nine files hold the arm metrics
  (`ArmKeys.{Control,Dns,Idle,Latency,Loss,Mix,Persistent,Reliability,Throughput}.cs`) beside
  `ArmKeys.Common.cs`, `ArmKeys.Run.cs` and `ArmKeys.Sample.cs`. The whole ledger — eight shards:
  `Envelope`, `TcpRecord`, `TcpSummary`, `UdpSummary`, `DnsSummary`, `TargetSummary`, `ErrorRecord`,
  `VerdictNames` — is one file, `ArmKeys.Ledger.cs`.
- One `const string` per **leaf per nesting level**: `Ledger.TcpSummary.Connections` and
  `Ledger.TargetSummary.TcpTotals.Connections` are two constants because they are two paths. The class
  chain spells the path (`ArmKeys.Common.Gates.ClientSendLoss` is `gates/clientSendLoss`), and a class
  that is never a JSON member of its own says so in its `<summary>`.
- A data-driven container's member names get a `*Names` shard next to the record rather than a constant
  per element: `ArmKeys.Ledger.VerdictNames`, `ArmKeys.Reliability.OutcomeNames`,
  `ArmKeys.Reliability.ModeNames`.
- Conditional keys are listed in the shard's `<remarks>` with the flag that omits them, so "absent" is
  never read as "unknown"; adding a writer means adding it to the literal gate's file list, because the
  gate checks only the files it is told about ([tooling](./measurement-tooling.md)).

## The write path

```csharp
public interface IJsonWritable { void WriteTo(Utf8JsonWriter writer); }  // one production path; the value owns its braces
public enum JsonlPolicy { Propagate, SwallowAndCount }                   // what a sink does when a record cannot be written
```

- Every record is an `IJsonWritable` with one writer, and its members are `required`: adding a key to a
  record cannot pass unnoticed, because the shape test's explicit factory stops compiling
  (`Contracts/Metrics/LossMetrics.cs`).
- `ArmParameters`' members are nullable, and `null` there means *this arm does not publish the
  quantity*: the writer omits the member rather than writing a JSON `null` (`Contracts/ArmParameters.cs`).
- Do not carry a record in a `Dictionary<string, object?>`: nothing then ties the spelling to `ArmKeys`,
  and no shape test can compare the emitted path set with the declared one.

## Path alphabet (the analyzer's addressing scheme)

- `canonical_path = join('/', member names)`, where a member name is the **literal** JSON member name: a
  dot inside a name stays whole. Examples: `metrics/tcp.sent`, `metrics/classes/udp/sent`,
  `metrics/latency/udp.sent`, `parameters/seconds`, `records/run.json`.
- An array contributes its own path exactly once; its elements flatten under that same path, and the
  element count is recorded as the array's arity (no `[i]` segments).
- `jsonl_paths.py` is the single flattener, shared by the inventory, `compare-records.py` and the rename
  table; two alphabets would make them disagree about what a key is.

## Three states, never conflated

| State | Meaning | Renderer |
|---|---|---|
| key absent | this arm does not publish the quantity (conditional block) | `n/a` |
| key present, value `null` | published, not measurable (zero denominator, no sample) | blank cell with a reason |
| key present, value `0` | measured zero | `0` |

Ratio and per-second helpers live in `Contracts` (`JsonRate.Rate` — six decimals;
`JsonPerSecond.PerSecond` — three) and return `double?` with `null` for a zero denominator or a zero
tick span. Do not write a bare `(double)a / b`.

## Sink policies

| Policy | Used by | I/O + body failures | close/dispose failures |
|---|---|---|---|
| `Propagate` | client arm files | thrown to the caller (counted first) | counted, never thrown |
| `SwallowAndCount` | target ledger, target summaries | counted, never thrown | counted, never thrown |

- The surface is `JsonlSink(string path, JsonlPolicy policy, Action<Utf8JsonWriter>? envelope)`,
  `WriteErrors` (read back to fail the arm), `WriteAsync(body, ct)`, `CompleteAsync()` — never throws, so
  call it inside the arm's failure boundary — and `DisposeAsync()`, the backstop only.
- The body is serialized in memory first and the record is written in one step, so a throwing body
  leaves **no half line**; cancellation is observed at the lock, never mid-record.
- Client sinks flush on a fixed 1 Hz timer (`Contracts/Json/JsonlSink.cs:18`), so a crash keeps the
  samples already written.
- A lost record still fails the arm (`run.json.failed = true`), which is
  [run lifecycle](./measurement-run-lifecycle.md)'s half of the rule.

## Plan loading is fail-closed

- Every arm kind has **one** descriptor carrying its name, accepted keys, validator and runner
  (`Client/Arms/ArmKind.cs`); the same table is the loader's whitelist and the dispatcher's choice of
  arm, so a kind cannot exist in one and be missing from the other.
- Unknown keys, wrong JSON types, fractional values in integer keys, out-of-range values and colliding
  output file names are **load errors that name the arm and the key** — never a silent fallback. `0`
  stays a meaningful "not declared" for keys documented as such.
- Exit codes: `0` all green, `1` arm-level failure with records, `2` usage/plan error
  (`Cli/ExitCodes.cs`).

| Condition | Result |
|---|---|
| plan key unknown to the arm kind | exit 2, `arm '<name>' (kind '<kind>'): unknown key '<key>' (expected one of: <accepted keys>)` (`Client/PlanFile.cs:312`) |
| text key present with the wrong JSON type | exit 2, `'<key>' is <value>, which is not a string` (`:387`) |
| integer key not an integer | exit 2, `'<key>' is <value>, which is not an integer` (`:418`) |
| numeric key not a positive number | exit 2, `'seconds' is <value>, which is not a positive number` (`:327`) |
| integer outside the key's domain | exit 2, `'<key>' is <value>, which is outside <min>..<max>` (`:426`) |
| arm name longer than the file-name limit | exit 2, `arm '<name>' maps to a <n>-character file name '<file>', above the <n>-character limit` (`:158`) |
| two arm names mapping to one file | exit 2, `arm names '<a>' and '<b>' both map to the output file '<file>.jsonl'` (`:164`) |
| schedule past the tracker's sequence space | exit 2, `the schedule offers sequences up to <n>, past the tracker's MaxSequence of <n>` (`Client/Arms/ArmKind.cs:123`) |

## Good / Base / Bad

- **Good** — a key is added the way `Late` and `LateRate` are declared: `required` on the record, a
  constant in the arm's shard, written from `WriteTo` — the shape test's explicit factory then fails to
  compile until the new member is set, and the two-way path-set comparison covers it.
- **Base** — an arm that never runs still publishes `metrics: {}` and `parameters: {}` through
  `EmptyMetrics` (`Client/EmptyMetrics.cs:11`, `Client/ArmRecordWriter.cs:21`).
- **Bad** — a key-position literal written by hand, e.g. `writer.WriteString("sent", …)` where
  `ArmKeys.Loss.Sent` declares `sent`: `JsonKeyLiteralGateTests` fails on it. The gate matches only
  literals that are **currently declared** spellings, so the retired `sentOk`
  (`contract-inventory.py:53`, `DOTTED_LEAF_RENAMES`) passes it — it catches a duplicated spelling, not a
  stale one. Writing `null` because the value was unavailable is the third wrong shape: it conflates
  "not measured" with "not published", and a validator that falls back to a default on an unknown key
  does the same for a plan nobody wrote.

#### Wrong
```csharp
outcome.Metrics["sent"] = sent;                              // a literal duplicating ArmKeys.Loss.Sent: a rename never reaches it
public Dictionary<string, object?> Metrics { get; init; }    // no compile-time member coverage
```

#### Correct
```csharp
var metrics = new LossMetrics { Sent = sent, /* every required member */ };
public required IJsonWritable Metrics { get; init; }         // one production path
```

## Tests Required

- **Shape, per kind** (`tests/WinForward.E2E.Tests/Shapes/*Shape.cs`, `ContractShapeTests`): an explicit
  factory over the typed record; flatten the **production** writer output and compare path sets with the
  declared keys in both directions; nulls write the key; conditional blocks are omitted whole and only by
  their flag; array arity equals the plan value (`desktops`, `lanes`); data-driven containers keep their
  declared members.
- **Sink matrix** (`JsonlSinkTests`): `Propagate` throws on body/write but not on close;
  `SwallowAndCount` counts all four failure kinds; a cancelled write never cuts a record in half; the
  periodic flush surfaces data.
- **Plan loading** (`PlanFileValidationTests` / `PlanFileTests`): all shipped plans load, the built-in
  plan loads, and each rejection fixture yields exit 2 with the arm and the key named.
- **Ledger shape** (`LedgerShapeTests`): drive the production writers (a real connection, a real
  datagram) and compare the flattened ledger paths with the declared keys in both directions, per record
  family and in declared order; the three states asserted separately; the `sources` array written even
  when empty; the conditional `dnsAlt` block omitted whole.
