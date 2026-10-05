# Host flow creation logging in the deferred attribution path (2026-10-05)

## Goal

Make host-originated flows visible again in the debug log. When `host.rules` carries any process
selector, every host flow's flow-table entry is created by the deferred attribution pipeline
(`FlowAttributionPipeline`), which never emits the `flow.created` event the inline dispatcher path
emits. The result is that host flows produce no line naming their process, the rule that decided
them, or even that they were created — only the proxy-layer legs (`tcp.redirect.*`,
`udp.session.*`) survive, and only for proxied flows.

## Background

`FlowDispatcher.DispatchAsync` routes every eligible new host flow into the deferred pipeline
(`src/WinForward.Runtime/FlowDispatcher.cs:252`), because `ShouldAttribute` admits exactly the
host-origin flows that carry no process yet (`:367-371`). The pipeline evaluates policy on a setup
worker and claims the flow at delivery time through
`FlowAttributionPendingIndex.Claim` → `FlowTable.TryClaimResolved`
(`src/WinForward.Runtime/FlowAttributionPendingIndex.cs:449-474`).

The `flow.created` event has exactly one emission site in the repository — the inline path at
`src/WinForward.Runtime/FlowDispatcher.cs:267-274` — and its field builder
(`FlowFields`, `:534-545`) is the only producer of the `process` and `processPath` fields for that
event. `NdisPacketActionExecutor`'s trace events
(`src/WinForward.Runtime/Capture/NdisPacketActionExecutor.cs:467-477`) carry no process field
either, and the pipeline bypasses `LogPacketStage` entirely. So with a process selector configured,
**no log line at any level names the process of a host flow**.

### Field evidence (987-line debug capture, 2026-10-05 08:37:27–08:44:44)

| Observation | Value |
| --- | --- |
| `flow.created` lines | 34 |
| `flow.created` with `origin=forwarded` | 34 |
| `flow.created` with `origin=host` | 0 |
| Highest flow generation seen | 665 |
| Lines containing `process=` or `processPath=` | 0 |
| Host-origin TCP proxy legs (`tcp.redirect.created`) | 146 |
| Host-origin UDP sessions (`udp.session.created`) | 78 |
| Lines mentioning `1.1.1.1:443` | 6 (all proxy legs: `tcp.redirect.created`, `tcp.relay.started`, `tcp.relay.ended`, `tcp.redirect.closed`) |

Host flows that matched a `pass` rule, and host flows that were blocked, produced **zero** lines.

## Requirements

- R1: When the deferred pipeline creates a host flow's flow-table entry, it emits one
  `flow.created` debug event carrying the same field set the inline path emits: `flow`,
  `protocol`, `origin`, `source`, `destination`, `process`, `processPath`, `action`, `rule`,
  `proxy`.
- R2: Exactly one such event per claimed flow — not one per retained packet, and not one per
  delivery drain.
- R3: The event is emitted only for a flow the pipeline actually created. Capacity-blocked,
  flow-full, pending-rejected, and failed attributions keep their current (silent or warn)
  behavior.
- R4: Forwarded flows and every non-deferred path keep today's behavior byte for byte.
- R5: `processPath` stays gated by `IncludeProcessPathInLogs`
  (`src/WinForward.Configuration/ConfigurationModels.cs:175`).
- R6: The debug gate must be evaluated before any field array is allocated, and no work must be
  added to the capture pump's hot path.
- R7: The field builder stays single-sourced, so the inline and deferred `flow.created` payloads
  cannot drift.
- R8: The event's identity and level are unchanged, so existing tooling and README text stay true.

## Non-goals

- Adding packet-level (`packet.*`) events to the deferred path. The claim-time `flow.created` line
  answers "who owns this flow and which rule decided it"; per-packet tracing for deferred flows is
  a separate, heavier decision.
- A new `domain=` field: `origin=host|forwarded` already names the policy domain on the same line.
- Changing when or whether attribution runs, or the throttled `flow.attribution-miss` warn.

## Acceptance criteria

- [ ] A host flow created through the pipeline logs exactly one `flow.created` line containing
      `origin=host`, `process=<name>`, `rule=<index>`, `action=`, `proxy=`, and (when path logging
      is on) `processPath=`.
- [ ] Dispatching several packets on the same host flow still logs exactly one `flow.created`.
- [ ] A forwarded flow's `flow.created` payload is unchanged (regression test on the existing
      behavior).
- [ ] With the logger threshold above debug, the pipeline pays no allocation for the new event.
- [ ] Repository gates pass: `dotnet format WinForward.slnx --severity info --verify-no-changes
      --no-restore`, `jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-inspectcode.xml WinForward.slnx`
      with zero `<Issue>` entries, `dotnet build WinForward.slnx -c Release` zero-warning, and
      `dotnet test WinForward.slnx -c Release` green.
- [ ] Documentation: `README.md`'s logging paragraph notes that a host flow's `flow.created` line
      is emitted when the deferred attribution claim completes, so it can appear after the proxy
      legs that flow produced.
