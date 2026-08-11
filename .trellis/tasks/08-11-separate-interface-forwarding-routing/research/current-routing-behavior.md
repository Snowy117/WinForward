# Current Routing Behavior

## Confirmed Scenario

An explicit catch-all proxy rule has no adapter constraint. Its presence causes
`CaptureAdapterScopeResolver` to include every MSTCP-bound adapter. A new flow
observed `ON_RECEIVE` on virtual adapter B is classified as `Forwarded`, but
`RuleMatcher` does not inspect that origin. The catch-all rule therefore matches
and proxies B even when only adapter A was intended for forwarding proxy use.

`fallbackAction` cannot be `proxy`; proxy fallback is represented by an
explicit empty-match proxy rule. A `pass` or `block` fallback currently also
applies without an origin distinction.

## Evidence

- `src/WinForward.Runtime/CaptureAdapterScopeResolver.cs:25-49`: any
  adapter-unconstrained rule widens capture scope to all adapters.
- `src/WinForward.Runtime/NdisAdapterModeController.cs:28-48`: each scoped
  adapter receives both sent and receive tunnel modes.
- `src/WinForward.Runtime/PacketFlowClassifier.cs:15-40`: `ON_SEND` becomes
  `Host`; `ON_RECEIVE` becomes `Forwarded`, retaining adapter identity.
- `src/WinForward.Core/Policy.cs:12-23`: matcher fields omit origin.
- `src/WinForward.Core/Policy.cs:39-47`: ordinary fallback is shared.
- `src/WinForward.Configuration/ConfigurationModels.cs:120`: fallback proxy is
  invalid in JSON.
- `src/WinForward.Runtime/FlowDispatcher.cs:79-138`: existing flow resolution
  precedes new-flow attribution and policy evaluation.
- `src/WinForward.Core/Domain.cs:139-159`: a decision is reused across direction
  and adapter observations for the same logical tuple.
- `README.md:141-143`: documented intent says host traffic is selected by
  process and forwarded traffic by originating adapter.

## Architectural Constraints

- Capture exclusion is not an authoritative eligibility control. Host policy
  may need all adapters captured, and routed traffic can later appear on a
  different captured adapter.
- New inbound traffic addressed to a local service and traffic Windows may
  forward are both initially `ON_RECEIVE`; the current runtime cannot
  distinguish them authoritatively.
- The safe evaluator boundary is after self-traffic/reverse/existing-flow
  handling and before a genuinely new flow is claimed and evaluated.
- Non-flow packets have no cached logical flow and require equivalent
  origin-specific evaluation directly in the dispatcher or policy layer.

## Selected Product Decisions

- Use current `FlowOriginKind.Forwarded` as the MVP boundary.
- Only adapter-qualified rules may select new forwarded traffic.
- If no adapter-qualified rule matches, pass the forwarded traffic unchanged.
- Keep host policy and configured fallback behavior unchanged.
- Do not alter the JSON schema or manage Windows routing/NAT in this task.
