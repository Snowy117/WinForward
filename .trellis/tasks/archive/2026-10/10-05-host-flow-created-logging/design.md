# Design: host flow creation logging in the deferred attribution path

## Problem

Two code paths create a flow-table entry and only one of them reports it:

| Path | Entry created by | `flow.created` |
| --- | --- | --- |
| Inline (`FlowDispatcher.DispatchSlowAsync`) | `FlowTable.TryClaimResolved` at `src/WinForward.Runtime/FlowDispatcher.cs:257` | yes, `:267-274` |
| Deferred (`FlowAttributionPipeline` → `FlowAttributionPendingIndex.Claim`) | `FlowTable.TryClaimResolved` at `src/WinForward.Runtime/FlowAttributionPendingIndex.cs:464` | **no** |

Which path a flow takes is decided by `FlowDispatcher.TryDeferAttributionAsync`
(`src/WinForward.Runtime/FlowDispatcher.cs:308-313`): any new host flow admitted by `ShouldAttribute`
(`:367-371`) is deferred whenever the dispatcher was composed with an attribution pool, a setup
executor, and an attributor — the production composition
(`src/WinForward.Cli/DurableCaptureBundle.cs:294-301`). Because `RequiresProcessAttribution` is true
exactly when a host rule carries a process selector
(`src/WinForward.Core/Policy.cs:42`), the "I configured a process rule and now host flows vanished
from the log" experience is deterministic, not a race.

## Decision

Emit the existing `flow.created` event from the deferred path, at the moment the pipeline claims the
flow, through a new member on the pipeline's existing host interface.

### 1. New host hook

`IFlowAttributionHost` (`src/WinForward.Runtime/FlowAttributionPipeline.cs:11-32`) already exists so
the pipeline can call back into the dispatcher without touching its state. Add one member next to
`LogCapacityBlock`, which is the same kind of "one log line, dispatcher owns the payload" hook:

```csharp
/// <summary>The dispatcher's debug flow.created line for a flow the pipeline just created.</summary>
void LogFlowCreated(FlowContext context, long generation, FlowDecision decision);
```

`FlowDispatcher` implements it explicitly, mirroring the other members; the private method behind it
is the single emission point for the event (see §2 for the body).

The debug gate is the first statement of that method, so a disabled logger costs one interface call
plus a boolean — no array, no boxing, nothing on the pump's warm path.

### 2. One emission point

The inline path's payload construction (the former `FlowFields(CapturedFlowPacket, int)` used by
`DispatchSlowAsync`) is folded into the new method, so `flow.created` has exactly one body and one
payload:

```csharp
private void LogFlowCreated(FlowContext context, long generation, FlowDecision decision)
{
    if (!_logger.IsEnabled(RuntimeLogLevel.Debug)) return;
    var fields = new RuntimeLogField[10];
    fields[0] = new("flow", generation == 0 ? null : generation);
    fields[1] = new("protocol", context.Key.Protocol);
    fields[2] = new("origin", context.Key.Origin);
    fields[3] = new("source", context.Key.Local);
    fields[4] = new("destination", context.Key.Remote);
    fields[5] = new("process", context.ProcessName);
    fields[6] = new("processPath", _includeProcessPathInLogs ? context.ProcessPath : null);
    fields[7] = new("action", decision.Action);
    fields[8] = new("rule", decision.RuleIndex);
    fields[9] = new("proxy", decision.ProxyServerName);
    _logger.Event(RuntimeLogLevel.Debug, "flow.created", fields);
}
```

Both creation paths call it: the inline claim passes `packet.Context, packet.FlowGeneration`, the
deferred hook passes `entry.Context, generation`. Requirement R7 (no payload drift) is therefore
structural — there is no second body to drift from, and `FlowFields`/`FlowCreatedFields` disappear
rather than being renamed.

> Review pass (2026-10-05): the first implementation kept a shared seven-field builder and had both
> call sites fill three trailing slots (`fields[^3..^1]`) — single-sourced at the front, duplicated
> at the back. The reviewer collapsed the two into the method above after the implementer's gates
> were green, which is why the tree was re-validated on the frozen shape.

`processPath` keeps its `_includeProcessPathInLogs` gate (R5), which is derived from the rule set
(`src/WinForward.Configuration/ConfigurationModels.cs:175`), so a deployment with only filename
selectors still never logs a path.

### 3. Generation plumbing

`FlowAttributionPendingIndex.Claim` discards the created state
(`_flows.TryClaimResolved(entry.Key, decision, out _)`,
`src/WinForward.Runtime/FlowAttributionPendingIndex.cs:464`). Change the signature to hand the
generation back:

```csharp
public AttributionClaim Claim(PendingFlowAttribution entry, out long generation)
```

- `Claimed`: `generation` is the created `FlowState.Generation` (`src/WinForward.Core/FlowTable.cs:366`).
- `CapacityBlocked` / `AlreadyAttributed`: `generation` is 0 and nothing is logged.

`FlowAttributionPipeline.Deliver` captures the verdict once and switches on the captured value:

```csharp
var claim = _index.Claim(entry, out var generation);
switch (claim)
{
    case AttributionClaim.Claimed:
        if (entry.Decision is { } decision) _host.LogFlowCreated(entry.Context, generation, decision);
        break;
    case AttributionClaim.AlreadyAttributed:
        break;
    case AttributionClaim.CapacityBlocked:
        RuntimeCounters.Shared.Increment(RuntimeCounters.FlowCapacityBlock);
        _host.LogCapacityBlock(entry.Context);
        break;
    default:
        throw new InvalidOperationException($"Unhandled attribution claim '{claim}'.");
}
```

Capturing the verdict also removes the second `_index.Claim(entry)` call the current
`default:` arm makes inside its interpolated string
(`src/WinForward.Runtime/FlowAttributionPipeline.cs:278`) — that arm re-entered a state-mutating
method, which no `out` parameter can express anyway.

The emission point is claim time rather than decision time on purpose. `Deliver` may decide an entry
whose claim is then refused at capacity, and the failure arms never claim at all; logging at
`Evaluate` would announce flows that do not exist in the table. Claim time is the only point where
"this flow now exists, and this is the decision attached to it" is true.

### 4. Ordering (accepted, documented)

A host flow is claimed *after* its retained batch has been executed
(`FlowAttributionPipeline.Deliver`, `:237-267`), because the claim is deliberately the last step of
delivery. The new line therefore appears a few milliseconds after the proxy legs the flow produced,
not before them:

```
08:44:03.778 [debug] tcp.redirect.created tcpAssociation=138 source=192.168.31.110:54893 destination=1.1.1.1:443 ...
08:44:03.781 [debug] flow.created flow=651 protocol=tcp origin=host source=192.168.31.110:54893 destination=1.1.1.1:443 process=chrome.exe action=proxy rule=5 proxy=main
```

Reordering delivery to log first would mean claiming before executing, which is exactly the
invariant the current design protects ("an entry is admitted, delivered and drained only on its own
adapter's pump ... and claims the flow last",
`src/WinForward.Runtime/FlowAttributionPipeline.cs:33-36`). README's logging paragraph gets a
sentence so the ordering is not mistaken for an anomaly.

## Compatibility and rollback

- Forwarded flows never enter the pipeline (`ShouldAttribute` requires `FlowOriginKind.Host`), and
  every other caller of `Claim` is an assertion in `FlowAttributionPendingIndexTests`; the `out`
  parameter touches four call sites there.
- Event name, level, and field names are unchanged, so existing greps and README statements stay
  valid; only the previously-missing lines appear.
- Rollback is a single commit revert: the hook, the `out` parameter, and the builder rename are one
  cohesive change with no data or configuration surface.

## Risks

- **Duplicate line for one flow.** If `TryClaimResolved` resolved an existing entry instead of
  creating one, `Claimed` would be reported for a flow another path created. `Claim` already probes
  `_flows.TryResolve` under its own gate immediately before (`:456`), and every other `Claim` caller
  holds the same gate, so the only way to hit it is an inline claim of the same logical flow between
  the two statements — a combination the deferral admission excludes for host flows. Accepted, not
  defensively coded around: a duplicate debug line is the worst case, and the alternative (a second
  table-wide lookup or a create-only `FlowTable` overload) costs more than it buys.
- **Log volume.** One extra line per host flow at debug level. At the capture's observed rate
  (~660 flows in 7 minutes) this is negligible next to the per-packet trace level.
- **None for attribution correctness.** The hook is called after the claim and touches no decision,
  counter, or lease.
