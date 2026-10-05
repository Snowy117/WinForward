# Implement — per-flow response ownership in the stability harness

Order matters: the sink corrections (steps 2–3) are what the acceptance run (step 7) measures. Do not run
the baseline before the sinks are corrected, or the new numbers inherit the old blind spot.

## 1. Reuse-mode selection (`--reuse`)

`benchmarks/WinForward.Benchmarks/Stability/SoakOptions.cs`:

- add `public UdpAssociationReuseMode ReuseMode { get; private init; } = UdpAssociationReuseMode.Auto;`
  beside `TcpRelayMode` (`:123`)
- add `case "--reuse": return options with { ReuseMode = ParseReuseMode(Value(args, ref index)) };` to
  `ApplyArgument` (`:236-281`)
- add `ParseReuseMode` beside `ParseTcpRelayMode` (`:366-369`): `auto` / `always` / `off`, case-insensitive;
  anything else throws `ArgumentException` naming the argument and the accepted values, so a typo cannot
  silently measure the default.

Substitute `options.ReuseMode` at the five `Auto` constructors: `UdpChurnScenario.cs:58`,
`UdpBurstScenario.cs:59`, `UdpSessionBudgetScenario.cs:95`, `UdpLossScenario.cs:173`,
`GcSoakScenario.cs:108`. Leave `FrameworkSetupBenchmarks.cs:61` and `UdpSessionBenchmarks.cs:83` on `Off`.

Document `--reuse` in the option list at `benchmarks/README.md:84-106`.

## 2. Ownership check — the three per-flow sinks

Each sink takes the `FlowKey[]` its scenario already builds and rejects a reply whose payload `flowId` does
not index back to the arriving `originalFlow`, before any timestamp is written.

- `ChurnCountingSink` (`UdpChurnScenario.cs:364-405`): constructor `(int flows)` becomes
  `(int flows, FlowKey[] flowKeys)`; call site `UdpChurnScenario.cs:49`.
- `BurstCountingSink` (`UdpBurstInstrumentation.cs:180-235`): the flow-id space is `[0, backgroundFlows)`
  warmup and `[backgroundFlows, backgroundFlows + burstFlows)` burst. Keep the existing branch structure, put
  the ownership test in front of the burst branch, and decide explicitly what a mismatched *warmup* reply
  does — it must not reach the in-flight attribution tracker (`tracker.TryTake`).
- `SessionBudgetSink` (`UdpSessionBudgetInstrumentation.cs:198-226`): constructor gains the keys; call site
  `UdpSessionBudgetScenario.cs:85`.

A mismatch increments that sink's `Misdelivered` counter and returns.

## 3. Row shape

- `WaveSample` (`UdpChurnScenario.cs:325-356`) gains `Own`, `Misdelivered`, `NoResponse`, emitted from
  `BuildMetrics`.
- the parameter object of `context.WriteResult` (`UdpChurnScenario.cs:115-120`) gains `reuse = options.ReuseMode`.
- the same two additions for the burst and session-budget rows.
- keep `own + noResponse == flows` checkable from the emitted row under `off`.

## 4. Notes for the unaffected scenarios (R5)

- `UdpLossScenario.cs:61` derives its loss rate from `receiver.Received / stats.SentDatagrams` — the forward
  direction. Record that where the row is documented; do not add an ownership filter to the loss arithmetic.
- `GcSoakScenario.cs:419` and `UdpSessionBenchmarks.cs:179` count totals only; state why that suffices for
  what they measure.

## 5. Regression protection

The affected sinks are `private`/`internal` to the benchmark assembly and `tests/` has no benchmark test
project. The durable check is therefore the row identity from step 3 plus this task's acceptance run. If
exposing the comparison is cheap — an `internal static` matcher plus `InternalsVisibleTo` — prefer a direct
unit test; otherwise record the reasoning in the task so the next reader knows why the invariant is asserted
through output rather than through a test. Choose one and say which in the completion report.

## 6. Build and test

```bash
dotnet build WinForward.slnx -c Release
dotnet test WinForward.slnx -c Release
```

## 7. Acceptance run

One column per invocation, from the same binary, with the command line recorded verbatim:

```bash
dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- \
  --stability --scenario udpChurn --burst-flows 48 --churn-waves 3 --dial-delay-ms 50 \
  --reuse off --output benchmarks/results/<date>-udp-reuse-ownership/churn-off.jsonl
```

Repeat for `--reuse auto` and `--reuse always`, and for `--scenario udpBurst` and
`--scenario udpSessionBudget`. Under `off`, assert zero misdelivery; under `always` / `auto`, report the rate
and check the accounting identity holds.

## 8. Publish

`benchmarks/results/<date>-udp-reuse-ownership/README.md` following the shape of
`benchmarks/results/2026-09-28-udp-reuse/README.md`: verbatim commands, environment, before/after columns, and
an explicit statement of which older numbers are superseded and which still stand. Add the directory to
`benchmarks/README.md`.

## Validation commands

| Gate | Command |
| --- | --- |
| build | `dotnet build WinForward.slnx -c Release` (zero warnings) |
| test | `dotnet test WinForward.slnx -c Release` (green) |
| format | `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` (empty output) |
| inspector | `jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-inspectcode.xml WinForward.slnx` (zero `<Issue>`) |

The format and inspector gates are slow; run them before reporting, not after every edit.

## Risky points

- `BurstCountingSink`'s two flow-id ranges. An ownership test written against the wrong offset reports
  misdelivery for every warmup reply; verify with a run that exercises warmup only.
- Changing what counts as an answer changes every count derived from it. For the three per-flow sinks that is
  the point, but the result README must say so or the corrected number reads as a regression.
- `--reuse always` disables capability detection by design — do not "fix" that.
- Keep the harness Linux-runnable: no Windows-only APIs, no dependency on WinpkFilter/NDISAPI.

## Rollback points

- After step 1: revert the CLI change alone; no measurement semantics have moved.
- After steps 2–3: the sinks are the semantic change; reverting them restores the old, blind numbers.
