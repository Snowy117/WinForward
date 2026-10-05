# Design — per-flow response ownership in the stability harness

## Boundary

Everything in this task lives under `benchmarks/`. The production packet path, `UdpProxyCoordinator`,
`UdpAssociationPool` and `UdpProxySession` stay untouched: the task measures the shipped behaviour, so
anything that changes what is measured defeats its purpose.

The line that must not move is the stand-in server. `LoopbackSocks5UdpServer` keeps one peer address per
association and answers every echo reply to `_lastClient` (`LoopbackSocks5UdpServer.cs:306-342`); that is
a faithful stand-in for a connection-oriented server and it is the thing under test. The ownership check
therefore belongs in the **observer** — the response sink — and never in the server stand-in or in the
relay loop. Fixing delivery inside the harness would make the harness report healthy behaviour for a
server model that does not have it.

## Data flow

```
client wave ──▶ coordinator ──▶ Socks5UdpTransport (per-flow relay socket)
                                        │
                                        ▼
                        LoopbackSocks5UdpServer.RelayConnection   (one per association)
                          ForwardAsync: _lastClient = sender
                          SendReplyAsync: write to _lastClient     ← the modelled defect
                                        │
                                        ▼
                        UdpProxySession.ReceiveLoopAsync
                          TryGetReceiveSource ──▶ InjectResponseAsync(Flow, source, payload…)
                                        │
                                        ▼
                        <scenario>CountingSink.InjectAsync(originalFlow, …)
                          payload embeds (sequence, flowId)  ── compare ──▶ originalFlow
```

Both sides of the comparison already reach `InjectAsync`: the arriving flow as `originalFlow`, and the
sender's identity in the payload as `flowId` (`DatagramHeader`). The sink additionally needs the
`FlowKey[]` the scenario built, because `flowId` is an index into it, and a `FlowKey` cannot be
reconstructed from the flow id alone.

## Contracts

**Sink classification.** Each affected sink resolves three mutually exclusive outcomes per wave:

| Outcome | Condition | Effect |
| --- | --- | --- |
| `own` | `flowKeys[flowId] == originalFlow` | records the first-response timestamp for `flowId`, exactly as today |
| `misdelivered` | same wave, different flow | increments a counter; **never** touches a timestamp |
| `noResponse` | no reply observed for a flow | unchanged from today's absent-sample handling |

The identity `own + noResponse == flows` must hold for a run with zero misdelivery, and
`own + misdeliveredReplies + noResponse == flows` must hold in general (a flow can receive several
foreign replies and still be counted once for `noResponse`). The emitted row must carry both counters
rather than leaving the reader to infer them from a latency list length.

**Reuse-mode selection.** Follow the existing enum-argument precedent exactly: a `ReuseMode` property on
`SoakOptions` defaulting to `Auto`, parsed by `--reuse <off|always|auto>` through a small
`ParseReuseMode` mirroring `ParseTcpRelayMode` (`SoakOptions.cs:252-253`, `:366-369`), and rejected with
`ArgumentException` on an unknown value so a typo cannot silently measure the default. Each of the five
stability scenarios that currently pins `UdpAssociationReuseMode.Auto` in source passes
`options.ReuseMode` instead; the two perf benchmarks keep `Off`, which is already the correct baseline
for what they measure.

**Row shape.** `context.WriteResult("udp.churn", <scenario parameters>, <metrics>)` already separates
parameters from measurements (`UdpChurnScenario.cs:115-120`). The reuse mode is a parameter and goes in
the first object; the ownership counters are measurements and go in the second. That keeps a row
self-describing when jsonl files from different columns are read side by side.

## Trade-offs

- **Count at the sink, not at the relay.** Counting inside `RelayConnection` would be cheaper and
  simpler, and wrong: it would observe the harness's own forwarding choice instead of what the product
  received.
- **Do not change `DatagramHeader`.** The payload already carries `(sequence, flowId)`; widening the
  header would change every scenario's payload size and invalidate the recorded `payloadBytes`.
- **`noResponse` stays derived, not stored.** Storing "no reply" would need a second pass over the wave;
  deriving it from `flows - own - distinctMisdeliveredTargets` is what the acceptance identity checks.
- **Accepting a partial re-run.** `off` and `always`/`auto` need separate runs because the mode is
  process-scoped (a pool is constructed per run, `UdpChurnScenario.cs:58`). Producing all columns from
  one binary is the improvement; producing them from one process is not attempted.

## Compatibility

- Existing jsonl files keep parsing: every new field is additive in the metrics object.
- Previously published numbers are not rewritten. The new result set is published alongside
  `benchmarks/results/2026-09-28-udp-reuse/`, and that README gains a pointer marking its loss columns as
  superseded by the ownership-aware measurement.
- Recorded command lines change only by the added `--reuse` argument; the canonical shape in
  `benchmarks/README.md:84-106` gains the new option in its list.

## Rollback

Benchmark-only change with no product surface: reverting the commit restores the previous harness
behaviour, and the result set stays as a record of what was measured. No configuration, migration or
runtime state is involved.
