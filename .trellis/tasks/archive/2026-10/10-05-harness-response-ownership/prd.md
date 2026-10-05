# Harness: per-flow response ownership check and a trustworthy sharing baseline

Parent: `10-05-udp-association-sharing-correctness`.

## Goal

Give the stability harness the one measurement it cannot currently make — **whether each relay reply
reached the flow that asked for it** — and re-run the UDP association-reuse comparison on top of it.

Three scenarios conclude per-flow success from a first-response timestamp chosen by the payload's own
flow id, without ever checking which flow the reply arrived on: `ChurnCountingSink`
(`UdpChurnScenario.cs:392-405`, behind the `udpChurn` sharing comparison), `BurstCountingSink`
(`UdpBurstInstrumentation.cs:205-235`, behind `udpBurst`) and `SessionBudgetSink`
(`UdpSessionBudgetInstrumentation.cs:211-225`). The loopback server in front of them reproduces the real
server-side write path exactly — one peer address per association, every reply written to the last sender
(`LoopbackSocks5UdpServer.cs:306-342`) — so a reply the server delivered to the wrong flow is recorded as
that flow's own first response.

A control experiment against sing-box 1.14.1 measures what that hides: with two live flows on one
association only 45–50 % of replies reach the flow that asked, falling to 9 % at eight flows, with zero
packet loss throughout. Under sharing every flow is therefore recorded as answered — a flow that received
nothing is indistinguishable from one that received a sibling's reply — and the latency samples collapse
onto the arrival time of a single batch.

Until this is fixed, the per-flow success columns that made sharing look free cannot be read as evidence
that sharing preserves per-flow semantics.

## Confirmed facts

- Affected sinks, all three per-flow and all three used by scenarios that share
  (`UdpChurnScenario.cs:58`, `UdpBurstScenario.cs:59`, `UdpSessionBudgetScenario.cs:95`):
  `ChurnCountingSink` (`UdpChurnScenario.cs:364-405`), `BurstCountingSink`
  (`UdpBurstInstrumentation.cs:180-235`), `SessionBudgetSink`
  (`UdpSessionBudgetInstrumentation.cs:198-226`).
- Sinks that are **not** per-flow and must not be rewritten as if they were:
  `UdpLossScenario`'s loss rate comes from the echo receiver's own count against datagrams sent
  (`UdpLossScenario.cs:61`) — the forward direction — so misdelivered replies never entered it; its
  `responsesInjected` field (`UdpLossScenario.cs:73`) is a total that does include them.
  `GcSoakScenario.CountingUdpResponseSink` (`GcSoakScenario.cs:419`) counts totals only.
  `UdpSessionBenchmarks.ResponseCountingSink` (`UdpSessionBenchmarks.cs:179`) runs under `Off`
  (`UdpSessionBenchmarks.cs:83`).
- Therefore the published "25 kpps lossless data path" is a forward-direction measurement and is **not**
  invalidated by this finding. What is invalidated is the per-flow success claim of the sharing columns:
  `firstResponses` / the `firstResponseMs` distribution in `udpChurn` and `udpBurst`, and the first-response
  ticks in `udpSessionBudget`.
- The latency figures themselves are not fabricated — a reply did arrive and its timestamp is real. What
  is lost is *whose* reply it was, so "no flow went unanswered" is unsupported and the distribution is
  flattened onto one arrival batch.
- Resource columns are unaffected: churn bytes/session, associations per session, descriptors, and the
  session-rate figures carry no ownership assumption.
- Reuse mode per scenario, as built today: `Auto` in `UdpChurnScenario.cs:58`, `UdpBurstScenario.cs:59`,
  `UdpSessionBudgetScenario.cs:95`, `UdpLossScenario.cs:173`, `GcSoakScenario.cs:108`; `Off` in
  `FrameworkSetupBenchmarks.cs:61` and `UdpSessionBenchmarks.cs:83`. Producing an `off` column therefore
  needs a code edit, which is why every published comparison was assembled from differently-built binaries.
- Both sides of the ownership comparison already reach `InjectAsync`: the arriving flow as its
  `originalFlow` argument, and the sender's identity in the payload as `flowId` (`DatagramHeader`).
  `flowKeys` is built one-to-one with those indices (`UdpChurnScenario.cs:78-80`), so a sink only needs the
  array handed to it.
- Wave shape fires every flow's first datagram back-to-back (`UdpChurnScenario.cs:260-286`) — the shape that
  maximises last-sender misdelivery.
- The harness relay that answers every echo reply to `_lastClient` (`LoopbackSocks5UdpServer.cs:249-342`) is
  the stand-in for the server under test and must keep behaving this way; the ownership check goes in the
  observer, never in the relay.
- The latency win on record is real and comes from amortizing the control connect (2.46 ms) and ASSOCIATE
  (4.49 ms total) against a 13.1 µs relay socket
  (`BenchmarkDotNet.Artifacts/results/WinForward.Benchmarks.Perf.FrameworkSetupBenchmarks-report-github.md`).

## Requirements

- R1 — Ownership validation in the three per-flow sinks: a reply may only be recorded as a flow's first
  response when the arriving `originalFlow` matches the payload's flow id. A mismatched reply must not
  advance any timestamp.
- R2 — Misdelivery accounting in those three scenarios: the count of replies that arrived on a flow other
  than their sender is a first-class output field, separate from loss. So that a reader can check it, each
  affected row must make `own + noResponse == flows` visible under `off`, and `own + misdelivered + noResponse`
  accounting complete under sharing.
- R3 — Reuse-mode selection per run: `off`, `always` and `auto` selectable without editing source, so all
  columns come from one binary and one recorded command line.
- R4 — Baseline re-run and write-up: re-run the sharing comparison with the corrected sinks and publish the
  result set under `benchmarks/results/`, reporting the latency and resource columns — which should survive —
  next to the per-flow success columns, which should not.
- R5 — Say what is now known: the two counting sinks and the `Off` perf benchmark get a written note of why
  their figures are unaffected rather than a silent pass, so the next reader does not re-derive it.

## Constraints

- Managed-only: the harness never touches WinpkFilter/NDISAPI and must keep running on Linux.
- No change to the production packet path, the coordinator, or the association pool. This task changes
  measurement, not behaviour; the R3 plumbing may touch harness construction only.
- The loopback server keeps its single-peer-address delivery. Fixing delivery inside the harness would make it
  report healthy behaviour for a server model that does not have it.
- Forward-direction measurements stay as they are; do not widen the ownership check into scenarios whose
  conclusion never depended on it.
- Existing canonical command lines stay reproducible, and every previously published number the new semantics
  supersede is marked as superseded rather than silently replaced.
- Never record the user's real configuration (`__RUNTIME_*__` placeholder secrets, personal domains, LAN
  addresses) in a result set or commit.

## Acceptance criteria

- [ ] With `always` and `auto`, `--scenario udpChurn` reports a non-zero misdelivery count, and that count
      agrees with replies independently observed arriving on the wrong flow.
- [ ] With `off`, the same scenario reports zero misdelivery in every wave, and `own + noResponse == flows`.
- [ ] Under sharing, `own + misdelivered + noResponse` accounts for every flow and is visible in the emitted row.
- [ ] A reply recorded as a flow's first response is always one that arrived on that flow, in `udpChurn`,
      `udpBurst` and `udpSessionBudget`.
- [ ] The reuse mode of a run appears in that run's recorded command line and in its output rows.
- [ ] `UdpLossScenario`'s loss rate is unchanged by this work, and its row or README states that it is a
      forward-direction figure; `GcSoakScenario` and the `Off` perf benchmark carry a written note of why they
      are unaffected.
- [ ] Result set published under `benchmarks/results/<date>-udp-reuse-ownership/` and referenced from
      `benchmarks/README.md`, with the superseded `2026-09-28-udp-reuse` per-flow columns marked as such and
      its resource and forward-direction columns explicitly left standing.
- [ ] `dotnet build WinForward.slnx -c Release` is zero-warning and `dotnet test WinForward.slnx -c Release`
      stays green.

## Out of scope

- Implementing an exclusive-lease sharing mode in the product (`UdpAssociationPool`,
  `udpAssociationReuse`). It is a behaviour change and is priced as its own child task once this baseline
  exists.
- Multi-destination wave generation. The destination-keyed policy is what needs it, so it belongs with that
  policy's task.
- Any change to `UdpProxySession`'s production reply handling.
