# Host/forwarded rule split (2026-10-04)

## Goal

Make the two policy domains WinForward already implements at runtime — host-originated flows, and
flows observed arriving on another NIC — explicit in the configuration file, instead of deriving
domain membership from whether a rule happens to carry an adapter selector.

## Background

The runtime already separates the two domains. `FlowDispatcher`
(`src/WinForward.Runtime/FlowDispatcher.cs:470`) and `FlowAttributionPipeline`
(`src/WinForward.Runtime/FlowAttributionPipeline.cs:226`) branch on `FlowOriginKind` and call
`PolicySnapshot.Evaluate` or `PolicySnapshot.EvaluateForwarded` respectively.

The configuration does not expose that split. There is one ordered `rules` array, and
`EvaluateForwarded` (`src/WinForward.Core/Policy.cs:52-61`) re-derives forwarded eligibility from
the rule's own fields: it admits a rule only when `RuleMatcher.IsAdapterQualified`
(`src/WinForward.Core/Policy.cs:12`, used at `:57`), then falls back to a hard-coded
`FlowAction.Pass` (`src/WinForward.Core/Policy.cs:60`).

That implicit model has three observable consequences:

- A forwarded rule cannot be written without an `adapterId`/`adapterName` selector. A rule with no
  adapter selector is silently dead for forwarded traffic, and the file says nothing about why.
- One adapter-qualified rule serves both domains, so rule *order* carries a cross-domain meaning the
  file does not show. The working gateway-LAN pattern in
  `.trellis/spec/backend/traffic-policy-lifecycle.md:28` depends on placing a combined
  `adapterId` + `remoteCidr` pass rule *before* the adapter proxy rule; that one rule is doing
  host-egress work and forwarded-LAN work at the same time.
- The top-level `fallbackAction` reads as a global default but only governs host traffic
  (`README.md:100-101`); forwarded traffic has no configurable default at all.

Capture scope is coupled to the same field. `CaptureAdapterScopeResolver.TryResolve` and
`ResolveForRefresh` (`src/WinForward.Runtime/Capture/CaptureAdapterScopeResolver.cs:27-51`, `:78-93`)
decide which adapters receive tunnel mode by scanning rule adapter selectors, and any
adapter-unconstrained rule widens capture to every MSTCP-bound adapter. A split that does not also
update this resolver produces the failure mode "rule configured, adapter never captured".

## Configuration Shape (decided)

Nested domains; the section names match `FlowOriginKind { Host, Forwarded }` one for one.

```json
{
  "socks5Servers": [ { "name": "main", "host": "proxy.example.com", "port": 1080 } ],
  "host": {
    "fallbackAction": "pass",
    "rules": [ { "process": ["browser.exe"], "action": "proxy", "proxyServer": "main" } ]
  },
  "forwarded": {
    "fallbackAction": "pass",
    "rules": [ { "adapterName": ["vEthernet (MyVM)"], "action": "proxy", "proxyServer": "main" } ]
  },
  "logLevel": "info"
}
```

Presence contract:

| Path | Presence | Value |
| --- | --- | --- |
| `host` | required | object |
| `host.fallbackAction` | required | `pass` \| `block` |
| `host.rules` | optional, default `[]` | array of rule objects |
| `forwarded` | optional | object |
| `forwarded.fallbackAction` | optional, default `pass` | `pass` \| `block` |
| `forwarded.rules` | optional, default `[]` | array of rule objects |

`host` is required because the host default action must be stated explicitly; silently defaulting a
leak-prevention knob is not acceptable. `forwarded` is optional because a host-only configuration
does not care about it. The rule object's field set is unchanged.

## Scope

- The configuration schema for the rule domains, and the validation that produces diagnostics for it.
- `PolicySnapshot`'s rule storage and evaluation contract for both domains.
- `CaptureAdapterScopeResolver`'s scope derivation over both rule lists.
- The domain-qualified surface of validation diagnostics and runtime trace/log fields.
- Migration of `examples/*.json`, `README.md`, and
  `.trellis/spec/backend/traffic-policy-lifecycle.md`.

## Out of scope

- `proxyUnavailableAction` / `processingFailureAction` runtime semantics. Both are currently
  validation-only stubs (`src/WinForward.Configuration/ConfigurationModels.cs:319-328`): a value
  other than `block` is rejected and the value is then discarded, and every runtime fail-closed site
  hard-codes block (`src/WinForward.Runtime/Capture/NdisPacketActionExecutor.cs:404`, `:438`, `:454`,
  `:487`). Giving them real meaning — including any `pass` downgrade, which for TCP requires
  rewriting a claimed flow's decision and reinjecting the retained SYN
  (`src/WinForward.Runtime/TcpRedirect/TcpProxyCoordinator.cs:165`) — is a separate task.
- The JSON Schema file, its publication path, and its sync test. Follow-up task. Adding a tolerated
  `$schema` key later is purely additive, so deferring it does not create a second breaking
  configuration change.
- Non-flow frame handling. Spec-locked at "always pass on both origins"
  (`.trellis/spec/backend/traffic-policy-lifecycle.md:20-22`); unchanged.
- Configuration hot reload. Not supported and not being added.
- Any change to how `FlowOriginKind` is derived from NDIS direction.

## Requirements

- **R1 — Two explicit domains.** The configuration exposes two independent, ordered rule lists: one
  evaluated for host-originated flows, one for flows observed on another NIC. Domain membership is
  positional (which list a rule is in) and is never inferred from the rule's matcher fields.
- **R2 — Host semantics preserved.** The host list keeps today's behavior exactly: every rule is
  eligible, the first match wins, and no match selects `host.fallbackAction`.
- **R3 — Forwarded eligibility is positional.** Every rule in the forwarded list is eligible
  regardless of whether it carries `adapterId`/`adapterName`. When present, those selectors narrow
  the rule to the adapter the packet was observed on, exactly like every other matcher field.
- **R4 — No rule serves both domains.** A host rule never matches a forwarded flow and a forwarded
  rule never matches a host flow. A rule intended for both must be written in both lists.
- **R5 — Each domain owns its default action.** `fallbackAction` moves under the domain it governs,
  per the presence contract above. Forwarded traffic stops using a hard-coded default.
- **R6 — Process selectors are rejected in the forwarded domain.** A forwarded flow has no host
  process owner, so a `process` selector in `forwarded.rules` can never match. It is a validation
  error naming the rule, not a silently dead rule. Consequently process attribution is required only
  by host rules.
- **R7 — Capture scope unions both lists.** Adapter-constrained rules in either list contribute
  their resolved adapters to the capture scope; an unconstrained rule in either list widens scope to
  every MSTCP-bound adapter. Startup and refresh diagnostics stay domain-qualified.
- **R8 — Domain-qualified operator surface.** Validation diagnostics name the domain in the path
  (`host.rules[i]` / `forwarded.rules[i]`), so host index 2 and forwarded index 2 are
  distinguishable. The runtime trace `rule` field stays a domain-relative index, and the domain is
  the `origin` field the same event already carries — no new field and no log-format change.
- **R9 — Documentation and examples migrate.** `README.md`, the six `examples/*.json` files, and
  `.trellis/spec/backend/traffic-policy-lifecycle.md` describe the new shape; no example or doc keeps
  the old one.

## Acceptance Criteria

- [x] A forwarded flow matches a rule in `forwarded.rules` that carries no adapter selector.
- [x] With identical non-adapter matchers in both lists, a rule in `host.rules` does not match a
      forwarded flow and a rule in `forwarded.rules` does not match a host flow.
- [x] An unmatched host flow selects `host.fallbackAction`; an unmatched forwarded flow selects
      `forwarded.fallbackAction`; a configured `block` forwarded default blocks an unmatched
      forwarded flow.
- [x] A `process` selector inside `forwarded.rules` fails validation with a domain-qualified
      diagnostic, and the same selector inside `host.rules` validates.
- [x] An adapter-constrained rule in `forwarded.rules` selects that adapter for capture; an
      unconstrained rule in `forwarded.rules` widens capture scope to every MSTCP-bound adapter; an
      adapter selector that resolves to no current adapter still fails startup with a
      domain-qualified diagnostic.
- [x] A configuration omitting `host`, or omitting `host.fallbackAction`, fails validation; one
      omitting `forwarded` validates and behaves as "no forwarded rules, pass".
- [x] A matched forwarded rule reports its index relative to the forwarded list, and the same trace
      event carries `origin=Forwarded`, so the domain is unambiguous without a format change.
- [x] All six `examples/*.json` files validate against the new shape, and the README documents the
      new shape as the only supported one.
- [x] `dotnet build WinForward.slnx -c Release` is zero-warning, `dotnet test WinForward.slnx -c Release`
      is green, and both committed quality gates pass.

## Notes

- The old top-level `rules` key is **not** special-cased. The project has not shipped a release, so
  it gets the same `UnmappedMemberHandling.Disallow` rejection every other unknown key gets, and no
  migration path is offered.
- The test `ForwardedPolicySkipsUnqualifiedRulesAndConfiguredFallback`
  (`tests/WinForward.Core.Tests/EndpointAndPolicyTests.cs:59`) asserts the exact contract this task
  reverses in both halves: that an unqualified rule is skipped, and that the configured fallback is
  not used. It is replaced, not adjusted.
- The trace `rule` field stays a domain-relative index; the domain is the `origin` field the same
  event already carries (`src/WinForward.Runtime/FlowDispatcher.cs:526`, `:539`). No log-format
  change and no new field on `FlowDecision`.
