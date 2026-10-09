# Measurement Run Lifecycle

> What a run may publish when it fails or is torn down: the failure records, the five-shape
> `ObjectDisposedException` vocabulary, the two censuses that keep counting through a teardown, and the
> tests that hold it. Read it before adding a `catch (ObjectDisposedException)`, changing an arm's
> failure path, or deciding what a teardown may publish. Family hub:
> [measurement-harness.md](./measurement-harness.md). The sink policies a write failure travels through
> are [measurement-record-contract.md](./measurement-record-contract.md)'s.

## What a failed run publishes

| Condition | Result |
|---|---|
| arm throws / is interrupted | exit 1; an `error` record (`type,arm,kind,label,error,message,detail,startedTicks,endedTicks`) + `run.json.failed` |
| sink write fails | the record is lost but counted; the arm is failed; `run.json` still written |
| ledger / summary write fails | counted in `ledgerWriteErrors`; the target keeps serving |

## Teardown books no data point

Trigger: any `catch (ObjectDisposedException)` over a socket, and any decision about what a teardown (a
closed arm, a stopped target, a disposed socket) may publish. The rule is uniform: **teardown is not a
measurement**, so such a catch books no counter, no verdict and no observation. The five shapes below
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

## Why the arms are defensive

- A socket disposed **before** the next socket call throws `ObjectDisposedException`, so these arms are
  reachable exactly when a socket object is reused after disposal.
- A socket disposed **under a pending receive** ends that receive as
  `SocketException(OperationAborted)` ("Operation canceled"), which the socket sites book in a
  socket-error arm — their own, or the transport's one level down (`LaneEngine`). A real teardown race
  therefore never reaches the teardown arm; the source gate, not a driven test, is what keeps the arms
  honest.
- `TcpConnectionProtocol` is the exception worth driving: it takes its reader as an argument, so a
  socket disposed before the first read reaches its arm
  (`AConnectionTornDownUnderTheProtocolPublishesNoVerdict`). Two of the 31 are not sockets at all
  (`TargetLog`, `ResourceSampleWriter` wrap stderr), so there the catch is only a closed-pipe guard —
  the gate's business too.

## Two censuses that keep counting

- REL keeps the teardown attempt inside `outcomes` (its `Observed` stays the arm's default) because
  `sum(outcomes) == connectAttempts == scheduledAttempts` is a live analyzer identity
  (`WinForward.E2E.Analysis/Checks/IdentityChecks.cs`, `ReliabilityInvariantsOf`). What a teardown
  changes there is the attempt's published `status` (`cancelled`, not `exchanged`) and its `otherError`
  flag, nothing else.
- A site whose counter is a *census* rather than a measurement keeps counting: `TcpTargetServer` still
  increments `connections` at accept, so a torn-down connection can make `connections` exceed the number
  of `tcp` records.

## Good / Base / Bad

- **Good** — the empty catch (`catch (ObjectDisposedException) { /* the arm's own teardown closed it */ }`)
  and the two explicit answers (`return CommandOutcome.TornDown;`,
  `result._status = ExchangeStatus.Cancelled;`): the shape is in the registry, so nothing is fabricated
  and nothing is silently absorbed.
- **Base** — a clean run's teardown counters stay where they were (`classes/page/errors`,
  `classes/bulk/errors`, `dnsSummary/tcpAborted`, `tcpSummary/verdicts/error` and `outcomes/otherError`
  all keep their values), because no harness path can dispose a socket under a call that owns it.
- **Bad** — `catch (ObjectDisposedException) { Interlocked.Increment(ref counters._bulkErrors); }`
  (a fabricated bulk error) and `catch (ObjectDisposedException) { attempt.OtherError = true; }`
  (an `otherError` the peer was never seen to produce). `ObjectDisposedCatchGateTests` fails on either,
  naming `file:line` and the shape it read.

#### Wrong — teardown booked as a measurement
```csharp
catch (ObjectDisposedException)
{
    Interlocked.Increment(ref counters._pageErrors);   // the harness's own teardown, published as a page error
}
```

#### Correct — teardown books nothing, the failure arms keep counting
```csharp
catch (ObjectDisposedException)
{
    /* teardown closed the socket first: a teardown is not a page error and books nothing (D19.2 ⑨) */
}

catch (SocketException)
{
    Interlocked.Increment(ref counters._pageErrors);   // a socket error the harness did see is still a page error
}
```

## Tests Required

- **`ObjectDisposedCatchGateTests`** — the registry holds every `catch (ObjectDisposedException)` site
  with its shape in source order, asserts the baseline counts (31 sites / 19 files), and refuses any body
  that books a counter; re-adding an increment, or reverting `TornDown`/`Cancelled`, turns it red
  (mutation-checked).
- **`ObjectDisposedTeardownTests`** — the behavioural half: the protocol's torn-down arm (`Outcome` is
  `null`), the arm-cancelled arm (still `TcpVerdict.Error`), the two MIX connect-failure arms
  (`_bulkErrors == 1`, `_pageErrors == MixPageLoop.PageConnections`, 13) and the DNS aborted-read arm
  (`tcpAborted == 1`).
