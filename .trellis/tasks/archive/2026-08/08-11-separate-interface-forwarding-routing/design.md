# Separate Forwarded-Adapter and Host Policy Evaluation

## Problem Boundary

WinForward currently uses one ordered `PolicySnapshot` for both host-originated
and direction-derived forwarded traffic. An adapter-unqualified rule can widen
capture to all adapters and then match a new flow arriving on any of them. That
makes an ordinary catch-all proxy rule implicitly opt every forwarded adapter
into proxying.

The MVP keeps one configuration rule list but gives it two evaluation domains:

- `Host`: evaluate every rule in order, then use the configured
  `fallbackAction`.
- `Forwarded`: evaluate only rules that contain `adapterId` and/or
  `adapterName`, preserving their original order and all other match fields;
  pass when none match.

`Forwarded` retains its current definition: a new packet first observed as
NDIS `ON_RECEIVE`. This includes both traffic that Windows may route onward and
new inbound traffic addressed to a local service.

## Contracts

### Policy evaluation

Add an explicit forwarded evaluation operation to `PolicySnapshot` rather than
duplicating matcher logic in the runtime. It returns the first matching
adapter-qualified rule and otherwise `FlowDecision.Fallback(FlowAction.Pass)`.
The ordinary `Evaluate` operation and configured fallback remain unchanged for
host flows.

An adapter-qualified rule may also constrain protocol, address family, remote
CIDR, and remote port. A process constraint remains meaningful only when a
process identity exists; forwarded flows continue to have no process
attribution.

### Dispatcher ordering

For classifiable flows, preserve this order:

1. Pass registered WinForward self-traffic.
2. Handle active TCP reverse redirects.
3. Resolve an existing logical flow across direction and adapter boundaries and
   reuse its decision.
4. Attribute a process only for a genuinely new host flow.
5. Evaluate a genuinely new host flow with ordinary policy, or a genuinely new
   forwarded flow with forwarded policy.
6. Cache and execute the resulting decision.

Caching the implicit forwarded `pass` is required so later observations reuse
the same decision rather than entering the host policy as a new flow.

For non-flow packets, apply equivalent origin semantics without flow caching:
host packets keep the current meaningful-rule/fallback behavior; forwarded
packets consider only adapter-qualified rules that can meaningfully match a
non-flow frame, otherwise pass.

### Capture scope

Do not narrow capture scope in this task. Host rules can require observing
traffic on every adapter, and current tunnel mode enables both sent and receive
directions per adapter. The dispatcher/policy boundary is therefore the
authoritative eligibility gate.

Existing adapter selector resolution errors remain unchanged.

## Compatibility

- JSON configuration remains unchanged.
- Host rule order and `fallbackAction` behavior remain unchanged.
- Explicit adapter rules retain their existing matching behavior for forwarded
  traffic.
- Behavior intentionally changes for new forwarded traffic previously matched
  by adapter-unqualified rules or the host fallback: it now passes unless an
  adapter-qualified rule matches.
- A user who wants to block or proxy forwarded traffic must state that intent
  with an adapter-qualified rule.

## Risks

- `FlowOriginKind` is direction-derived, not an authoritative Windows routing
  decision. The accepted MVP applies the forwarded domain to unsolicited local
  inbound traffic too.
- The same routed tuple may be observed on more than one adapter. Existing flow
  resolution handles sequential cross-adapter observations, but concurrent
  first observations can race because flow identity is stored under the first
  key. Regression tests verify deterministic reuse in the supported sequential
  path; redesigning concurrent tuple claims is a separate task.
- Implicit pass must still consume flow-table capacity. Capacity exhaustion
  remains fail-closed, matching current dispatcher behavior.

## Rollback

The implementation is localized to policy evaluation, dispatcher selection,
tests, and documentation. Rollback restores ordinary evaluation for both
origins; no configuration or persisted-data migration is involved.
