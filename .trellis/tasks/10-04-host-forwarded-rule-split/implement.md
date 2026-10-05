# Implementation plan — Host/forwarded rule split

> Execution order for `10-04-host-forwarded-rule-split`. Requirements: [prd.md](./prd.md).
> Technical design: [design.md](./design.md).

## Before the first edit

```bash
# 1. Record the test baseline (quality-guidelines.md:26 — additive tests raise it by exactly their count).
dotnet test WinForward.slnx -c Release

# 2. Re-measure the file-size risk (limit: 400 effective lines, directory-structure.md:81).
rg -v '^\s*$' src/WinForward.Configuration/ConfigurationModels.cs | rg -v '^\s*//' | wc -l   # 375 at planning time
```

The two numbers that must be written into the task record before implementation starts: the test
total, and `ConfigurationModels.cs` effective lines.

## Batch 1 — Core: two-domain `PolicySnapshot`

Files: `src/WinForward.Core/Policy.cs`, `src/WinForward.Runtime/FlowDispatcher.cs`,
`src/WinForward.Runtime/FlowAttributionPipeline.cs`.

- [x] `PolicySnapshot`: keep the two-argument constructor, add `ForwardedRules` and
      `ForwardedFallbackAction` as `init` properties, rename `Rules` → `HostRules`, rename
      `Evaluate` → `EvaluateHost`.
- [x] `EvaluateForwarded`: delete the `rule.Matcher.IsAdapterQualified` condition; walk
      `ForwardedRules` in order; return `FlowDecision.Fallback(ForwardedFallbackAction)`.
- [x] `RequiresProcessAttribution`: scan `HostRules` only, with the reason in a comment (a forwarded
      flow has no host process owner, and validation rejects `process` in the forwarded list).
- [x] Update the two production call sites (`FlowDispatcher.cs:470`,
      `FlowAttributionPipeline.cs:226`) for the rename.
- [x] Migrate `tests/WinForward.Core.Tests/EndpointAndPolicyTests.cs` (13 `PolicySnapshot` sites,
      15 evaluation calls): rename call sites, and move forwarded-domain rules into
      `ForwardedRules`.
- [x] **Delete and replace** `ForwardedPolicySkipsUnqualifiedRulesAndConfiguredFallback`
      (`EndpointAndPolicyTests.cs:59`) — it asserts both halves of the contract this task reverses.
      Its replacement asserts: an unqualified forwarded rule matches, and the configured forwarded
      fallback is used when nothing matches.
- [x] Update `tests/WinForward.Runtime.Flow.Tests/FlowDispatcherExecutorTests.cs`: move the
      adapter-qualified rules of `DispatcherSeparatesForwardedAdaptersFromHostCatchAllPolicy` and
      `DispatcherReusesForwardedDecisionAcrossAdapterObservations` into `ForwardedRules`; rename
      `DispatcherCachesForwardedImplicitPassAcrossOriginsAndFailsClosedAtCapacity` (the implicit
      pass is now a configured default).

New regressions in this batch:

- [x] A forwarded flow matches a `ForwardedRules` entry that carries no adapter selector.
- [x] Identical non-adapter matchers in both lists: the host rule does not match the forwarded flow,
      and the forwarded rule does not match the host flow.
- [x] `ForwardedFallbackAction = Block` blocks an unmatched forwarded flow; the default is `Pass`.

Gate: `dotnet build WinForward.slnx -c Release` zero-warning; `WinForward.Core.Tests` and
`WinForward.Runtime.Flow.Tests` green.

## Batch 2 — Configuration: domain DTO and validation

Files: `src/WinForward.Configuration/ConfigurationModels.cs`,
`src/WinForward.Configuration/ConfigurationRules.cs` *(new)*.

- [x] Extract `ParseRule`, `ValidateNonEmpty`, `NormalizeSet`, `ParseNetworks`, `ParseSet<T>`,
      `ParsePorts`, `ParseProtocol`, `ParseFamily` and `ParseAction` into `ConfigurationRules`
      (internal static, the `ConfigurationLimits` pattern).
- [x] Add `RuleDomainDto` and the `host` / `forwarded` properties to `WinForwardConfigDto` per
      design §3.1. The old top-level `rules` key gets no mapped property: it stays an unknown key
      and is rejected by `UnmappedMemberHandling.Disallow`.
- [x] Rewrite `TryValidate`: required `host`, required `host.fallbackAction`, optional
      `forwarded` (default: no rules, fallback `pass`), and both lists parsed with paths
      `host.rules[i]` / `forwarded.rules[i]`.
- [x] Reject `process` inside `forwarded.rules[i]` at path `forwarded.rules[i].process`.
- [x] Confirm `ConfigurationModels.cs` is back under 400 effective lines; split further along the
      same seam if not.
- [x] Migrate the fixtures in `tests/WinForward.Configuration.Tests/ConfigurationValidationTests.cs`
      (27 occurrences) and `ConfigurationLimitsTests.cs` (9), and the two inline configurations in
      `tests/WinForward.Runtime.UdpProxy.Tests/UdpAssociationHeadTests.cs`.

New regressions in this batch:

- [x] Missing `host`, and missing `host.fallbackAction`, each fail at their own path.
- [x] Omitting `forwarded` validates, and an unmatched forwarded flow then passes.
- [x] `forwarded.fallbackAction` defaults to `pass` and accepts `block`; a bad value fails at
      `forwarded.fallbackAction`.
- [x] `process` in `forwarded.rules` fails at the domain-qualified path; the same selector in
      `host.rules` validates.
- [x] A rule-level error inside each domain reports its domain-qualified path.

Gate: zero-warning build; `WinForward.Configuration.Tests` green.

## Batch 3 — Runtime: capture scope over both lists

Files: `src/WinForward.Runtime/Capture/CaptureAdapterScopeResolver.cs`.

- [x] Walk `HostRules` then `ForwardedRules`; union both contributions; `anyUnconstrainedRule` is
      set by either list.
- [x] Replace the `int ruleIndex` diagnostic parameter with a precomputed rule path string in
      `ResolveRuleScope` and `ResolveSelectorSet`; diagnostics read `host.rules[0]` /
      `forwarded.rules[0]`.
- [x] Update `tests/WinForward.TestSupport/CaptureRunnerFakes.cs` only if the fixtures need a domain;
      the scope fixtures are host-scope by nature and should keep their current meaning.

New regressions in this batch:

- [x] An adapter-constrained rule in `forwarded.rules` selects exactly that adapter.
- [x] An unconstrained rule in `forwarded.rules` widens scope to every MSTCP-bound adapter.
- [x] A forwarded selector that matches no current adapter fails startup with a domain-qualified
      diagnostic; in refresh mode it narrows with a warning instead of stopping the run.

Gate: zero-warning build; `WinForward.Runtime.Capture.Tests` green.

## Batch 4 — Remaining test migration

- [x] Fix every remaining compile error from the rename and the rule-list move. Known sites:
      `FlowDispatcherTests`, `FlowContextMetadataTests`, `FlowAttributionPipelineTests`,
      `RuntimeLoggingTests`, `RuntimeDiagnosticLoggingTests`, `IdleExpirySweeper*`,
      `TcpCoordinatorFakes`, `TcpReversePrefilterTests`, `TcpFragmentHandlingTests`,
      `NdisCaptureResilienceTests`, `MultiAdapterCaptureLoopArrivalSignalTests`,
      `PacketPathWalkCountTests`, `DurableCaptureBundleTests`, `UdpProxyCompositionTests`,
      `SelfTrafficWarmPathGateTests`, `HotPathAllocationGateTests`.
- [x] Do not adjust assertions to make a test compile: a test that fails because its rule moved
      domains is reporting a real semantic change and must be re-expressed.

Gate: full `dotnet test WinForward.slnx -c Release` green; total equals the recorded baseline plus
exactly the number of added regressions.

## Batch 5 — Documentation and examples

- [x] `README.md`: the configuration block (`:54-78`), the rule bullets (`:84-99`), the
      `fallbackAction` bullet (`:100-103`), the Notes section (`:249-252`), and the Examples list
      (`:232-243`).
- [x] State in the README that a trace `rule` index is relative to its domain, and that the event's
      `origin` field names the domain.
- [x] Call out the `forwarded.fallbackAction: block` consequence: an unmatched forwarded flow is
      dropped, which for a VM means loss of connectivity.
- [x] `examples/*.json` (all six): `process-proxy`, `hyperv-adapter-proxy`, `dns-policy`,
      `dns-proxy`, `pass-fallback`, `block-fallback`. `hyperv-adapter-proxy` is the only one whose
      rule is genuinely forwarded; the rest are host-domain examples.
- [x] `.trellis/spec/backend/traffic-policy-lifecycle.md`: rewrite the "Host vs forwarded policy
      domains" bullets (`:9-16`, especially `:11`) and the gateway-LAN authoring pattern (`:28`).

Gate: `WinForward.Configuration.Tests` green against the migrated examples.

## Batch 6 — Quality gates

```bash
dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore   # exit 0, empty output
dotnet build WinForward.slnx -c Release                                          # zero warnings
dotnet test WinForward.slnx -c Release                                           # green
jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-inspectcode.xml WinForward.slnx         # zero <Issue> entries
```

`jb inspectcode` exits 0 even with findings — parse the XML. Budget 10–20 minutes for it.

## Risky files and rollback points

| Risk | File | Why it matters | Check |
| --- | --- | --- | --- |
| File-size breach | `src/WinForward.Configuration/ConfigurationModels.cs` | 375 effective lines against a 400 limit; the split adds shape and diagnostics | Re-measure after Batch 2 |
| Silent scope narrowing | `src/WinForward.Runtime/Capture/CaptureAdapterScopeResolver.cs` | A missed rule list makes a configured adapter never captured, which reads as a policy bug | Batch 3 regressions cover both lists and both directions |
| Deleted contract | `tests/WinForward.Core.Tests/EndpointAndPolicyTests.cs:59` | Its assertions are the old contract; adjusting it instead of replacing it would hide the change | The replacement asserts the new contract in both halves |
| Public surface move | `src/WinForward.Core/Policy.cs` | `Rules` / `Evaluate` rename touches every consumer | The compiler enumerates them; no site may be silenced with a suppression |

Rollback is a plain revert of the task's commits: no persisted state, no migration, and no on-disk
format written by the tool.

## Follow-up checks before `task.py start`

- [x] `prd.md` has no blocking open question (Q1 resolved: nested domains, both domains carry a
      configurable fallback).
- [x] `design.md` and `implement.md` are reviewed against the final planning summary.
- [x] The test baseline and the `ConfigurationModels.cs` effective-line count are recorded.
