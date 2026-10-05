# Design — Host/forwarded rule split

> Technical design for `10-04-host-forwarded-rule-split`. Requirements are in [prd.md](./prd.md);
> this file records the architecture, contracts, and trade-offs behind them.

## 1. Change boundary

The smallest behavior gap: today the forwarded domain is *derived* from a rule's matcher fields
(`RuleMatcher.IsAdapterQualified` at `src/WinForward.Core/Policy.cs:12`, gating `:57`), and the
forwarded fallback is a constant (`:60`). After the change the domain is *stated* by which list a
rule lives in, and both domains own a configured fallback.

Where the behavior actually lives, in dependency order:

| Layer | File | Why it is necessary |
| --- | --- | --- |
| Configuration | `src/WinForward.Configuration/ConfigurationModels.cs` | Owns the JSON object graph and the diagnostics; the file shape is defined here |
| Configuration | `src/WinForward.Configuration/ConfigurationRules.cs` *(new)* | Owns rule-domain parsing and validation; required by the 400-effective-line limit (§2) |
| Core | `src/WinForward.Core/Policy.cs` | Owns `PolicySnapshot`: the two lists, the two fallbacks, and the evaluation contract |
| Runtime | `src/WinForward.Runtime/Capture/CaptureAdapterScopeResolver.cs` | Derives capture scope by walking rules; must walk both lists or scope silently narrows |
| Runtime | `src/WinForward.Runtime/FlowDispatcher.cs` | Call site of the renamed host evaluation |
| Docs | `README.md`, `examples/*.json`, `.trellis/spec/backend/traffic-policy-lifecycle.md` | The documented contract is the spec of record |

Explicitly **not** done here: `proxyUnavailableAction` / `processingFailureAction` runtime
semantics, the JSON Schema file, non-flow frame handling, hot reload, and any change to how
`FlowOriginKind` is derived.

## 2. Constraint: the configuration file must be split

`ConfigurationModels.cs` is at **375 effective lines** against the ≤ 400 limit
(`.trellis/spec/backend/directory-structure.md:81`), and the note at `:84` records it as
deliberately kept cohesive because it was already compliant. The domain split adds the new DTO
shape and the per-domain diagnostics, which would push it over.

The extraction follows the existing `ConfigurationLimits` precedent rather than inventing a new
seam: `ConfigurationLoader` keeps the public defaults and the object graph, while an internal
static collaborator owns the checks that normalize each value. So:

- `ConfigurationModels.cs` keeps `WinForwardConfigDto`, `RuleDomainDto`, `RuleDto`,
  `Socks5ServerDto`, `ConfigDiagnostic`, `ValidatedConfiguration`, `TryParse`, `TryValidate`
  orchestration, server validation, log-level parsing, and the failure-action stubs.
- `ConfigurationRules.cs` *(new, internal static `ConfigurationRules`)* takes `ParseRule`,
  `ValidateNonEmpty`, `NormalizeSet`, `ParseNetworks`, `ParseSet<T>`, `ParsePorts`,
  `ParseProtocol`, `ParseFamily`, and `ParseAction`.

Estimated result: `ConfigurationModels.cs` ≈ 230 effective lines, `ConfigurationRules.cs` ≈ 180.
Both well inside the limit. `ConfigurationRules.cs` shares the `ConfigurationLoader` partial
namespace but is its own top-level type, matching `ConfigurationLimits`.

## 3. Configuration contract

### 3.1 Object graph

```csharp
public sealed partial class WinForwardConfigDto
{
    [JsonPropertyName("logLevel")]          public JsonElement LogLevel { get; init; }
    [JsonPropertyName("socks5Servers")]     public IReadOnlyList<Socks5ServerDto?>? Socks5Servers { get; init; }
    [JsonPropertyName("host")]              public RuleDomainDto? Host { get; init; }
    [JsonPropertyName("forwarded")]         public RuleDomainDto? Forwarded { get; init; }
    // … the numeric limits and udp* keys are unchanged
}

public sealed class RuleDomainDto
{
    [JsonPropertyName("fallbackAction")] public string? FallbackAction { get; init; }
    [JsonPropertyName("rules")]          public IReadOnlyList<RuleDto?>? Rules { get; init; }
}
```

`RuleDto` is unchanged: the split moves rules between lists, it does not alter a rule.

### 3.2 No migration path from the old shape

The project has not shipped a release, so the old top-level `rules` key is not special-cased: no
mapped legacy property, no targeted diagnostic. `ConfigurationJsonContext`'s
`UnmappedMemberHandling.Disallow` already rejects it in `TryParse` with the fixed template
`Invalid JSON configuration` at path `rules` — the same handling every other unknown key gets.

No translation from the old shape is attempted anywhere. A translation would have to decide whether
each adapter-qualified rule belongs to the host list, the forwarded list, or both; today's semantics
say "both", which is exactly the coupling being removed, and a wrong guess on a leak-prevention
configuration is a silent traffic leak.

### 3.3 Validation matrix

| Condition | Diagnostic path | Result |
| --- | --- | --- |
| `host` absent | `host` | error |
| `host.fallbackAction` absent or not `pass`/`block` | `host.fallbackAction` | error |
| `host.rules` absent | — | accepted, treated as `[]` |
| `forwarded` absent | — | accepted, no forwarded rules, forwarded default `pass` |
| `forwarded.fallbackAction` absent | — | accepted, `pass` |
| `forwarded.fallbackAction` not `pass`/`block` | `forwarded.fallbackAction` | error |
| `process` present in `forwarded.rules[i]` | `forwarded.rules[i].process` | error |
| any rule field invalid | `<domain>.rules[i].<field>` | error, as today |

`fallbackAction` accepts only `pass` and `block`, matching today's `allowProxy: false`.

## 4. Core contract

```csharp
public sealed class PolicySnapshot
{
    public PolicySnapshot(IReadOnlyList<PolicyRule> hostRules, FlowAction hostFallbackAction);

    public IReadOnlyList<PolicyRule> HostRules { get; }
    public IReadOnlyList<PolicyRule> ForwardedRules { get; init; } = [];
    public FlowAction ForwardedFallbackAction { get; init; } = FlowAction.Pass;

    public bool RequiresProcessAttribution { get; }        // host rules only
    public FlowDecision EvaluateHost(FlowContext context);
    public FlowDecision EvaluateForwarded(FlowContext context);
}
```

Design decisions and their reasons:

- **Two-argument constructor plus `init` properties.** `new PolicySnapshot(rules, fallback)` occurs
  at 62 sites in the tree (21 of them with an empty rule list). Keeping the constructor shape means
  those sites keep compiling, and the 21 `new PolicySnapshot([], X)` sites keep their *exact*
  meaning: no host rules, no forwarded rules, host fallback `X`, forwarded fallback default
  `pass` — which is precisely today's behavior (host uses `X`, forwarded hard-codes pass). The
  `init` property form matches how `ValidatedConfiguration` carries its optional members.
- **`Evaluate` → `EvaluateHost`.** The call site is
  `context.Key.Origin == FlowOriginKind.Forwarded ? _policy.EvaluateForwarded(context) : _policy.Evaluate(context)`
  (`FlowDispatcher.cs:470`, mirrored at `FlowAttributionPipeline.cs:226`). Naming both sides for
  their domain makes the branch say what it does; the rename is mechanical.
- **`EvaluateForwarded` loses the `IsAdapterQualified` gate.** Eligibility becomes positional, and
  `adapterId`/`adapterName` become ordinary narrowing fields, which is requirement R3.
- **`RequiresProcessAttribution` scans host rules only.** A forwarded flow has no host process
  owner, so a process selector in the forwarded list could never match, and configuration
  validation rejects it (§3.3). Scanning both lists would only open the attribution path for rules
  that cannot match.
- **No `FlowDecision` change, and no log-format change.** `FlowDecision.RuleIndex` stays a
  domain-relative index. R8's log half is already satisfied: both rule-index sites
  (`FlowDispatcher.cs:271` in the `flow.created` event, `:474` in `packet.action`) are built with a
  field set that already carries `origin` (`:526`, `:539`), so `rule=0 origin=Forwarded`
  unambiguously means `forwarded.rules[0]`. Adding a redundant domain field to a struct that is
  copied per flow would be duplication, not clarity. The requirement becomes a documentation and
  test obligation instead: the README states the index is domain-relative, and a test asserts the
  origin field accompanies it.

## 5. Capture-scope contract

`CaptureAdapterScopeResolver.TryResolve` / `ResolveForRefresh` currently walk `policy.Rules` once
(`src/WinForward.Runtime/Capture/CaptureAdapterScopeResolver.cs:27-51`, `:78-93`) and report
`rules[{ruleIndex}]`. The change:

- Walk `policy.HostRules` then `policy.ForwardedRules`; the two contributions are unioned into the
  same scope set, and `anyUnconstrainedRule` is set if *either* list has an unconstrained rule.
- Replace the `int ruleIndex` diagnostic parameter with a precomputed rule path string
  (`host.rules[0]` / `forwarded.rules[2]`), so `ResolveRuleScope` and `ResolveSelectorSet` stop
  formatting `rules[{ruleIndex}]` themselves.

This is the highest-consequence part of the change: a missed list silently produces "rule
configured, adapter never captured", which looks like a policy bug rather than a scope bug. The
acceptance criteria pin both directions explicitly (adapter-constrained forwarded rule selects its
adapter; unconstrained forwarded rule widens to every MSTCP-bound adapter).

## 6. Trade-offs

- **A rule intended for both domains is written twice.** Today one adapter-qualified rule serves
  host egress and forwarded traffic at once, which is the coupling being removed. The duplication is
  the cost; the benefit is that no rule's domain is implicit. The gateway-LAN pattern documented at
  `traffic-policy-lifecycle.md:28` becomes two entries and stops depending on rule order relative to
  an unrelated rule.
- **`forwarded.fallbackAction: block` is a loaded gun.** An unmatched forwarded flow is dropped
  silently (a `packet.dropped reason=policy` trace). It requires an explicit value and the default
  preserves today's behavior, but the README must call the consequence out for a VM-facing gateway.
- **The public `PolicySnapshot` surface changes** (`Rules` → `HostRules` + `ForwardedRules`,
  `Evaluate` → `EvaluateHost`). It is an internal-shaped type consumed only inside the solution, and
  the compiler enumerates every consumer.
- **`ConfigurationRules.cs` is a new file, not a new concept.** It is the `ConfigurationLimits`
  pattern applied to the rule domain, forced by the file-size limit rather than by a desire for
  decomposition.

## 7. Operational notes

- The configuration is validated fully before interception starts and is immutable for the run;
  nothing here interacts with hot reload (which does not exist).
- No packet-path shape changes: `RuleMatcher.IsMatch` still runs once per new-flow claim, and the
  warm path is untouched.
- Rollback is a plain revert of the task's commits. There is no persisted state, no migration, and
  no on-disk format written by the tool — the configuration file is operator-owned input.
