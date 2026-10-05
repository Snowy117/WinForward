# Implementation plan — Remove UDP association sharing

Companion to `prd.md` and `design.md`. Ordered so each phase is independently reviewable; the whole
change is a revertable change set, and no phase needs a data migration.

## Phase A — configuration surface removal (small, mechanical, first thing a deployment notices)

- [ ] A1. Delete the three keys from `WinForwardConfigDto` (`ConfigurationModels.Udp.cs`) and the
      `UdpAssociationReuseMode` enum (`src/WinForward.Configuration/UdpAssociationReuseMode.cs`).
      Nothing replaces them: the loader's unknown-property rule fails an old configuration closed and
      names the offending key's JSON path (design §3).
- [ ] A2. Remove R3's `proxyServer` legacy machinery on the same reasoning: `RuleDto.LegacyProxyServer`
      and its diagnostic in `ConfigurationRules.ParseRule`, the `ConfigurationRejectsTheRenamedProxyServerKey`
      theory, and the README clause that documents the rejection. The rename stays complete; the old
      spelling becomes an unknown property.
- [ ] A3. Remove `UdpAssociationReuse` / `UdpAssociationMaxPerServer` /
      `UdpAssociationFlowsPerAssociation` from `ValidatedConfiguration`, the parsing helpers in
      `ConfigurationModels.Udp.cs`, and the association-head warning in `ConfigurationLimits.cs`.
- [ ] A5. Staging, so each phase is green on its own: while the machinery still exists (Phase B
      deletes it), the composition root constructs the pool with `UdpAssociationReuseMode.Off`, so the
      moment the keys leave the schema the per-flow posture is the only reachable one. Phase B then
      removes the pool and that argument together.
- [ ] A6. Tests in `tests/WinForward.Configuration.Tests`: a configuration carrying any removed key
      fails at parse with its JSON path named; a configuration without them validates as before; the
      association-head warning test and the `proxyServer` legacy theory go.

## Phase B — ownership move and machinery deletion (the substance)

- [ ] B1. Introduce the per-flow association (proposal: `Socks5UdpAssociation`) owning the control
      connection, the ASSOCIATE result, the relay-endpoint publication, and the control-stream
      watchdog; it raises `UdpAssociationLostException` when the stream ends. Take the minimal
      behaviour from `UdpControlAssociation` and leave the pool, lease, refcount, evidence, and
      in-place re-association behind.
- [ ] B2. `Socks5UdpTransportFactory.CreateAsync` dials/ASSOCIATEs/binds per flow and hands the
      association to `Socks5UdpTransport`, which disposes both with the flow. `Socks5UdpTransport`'s
      lease coupling (`IUdpExchangeCounters` reads, relay rebinding) is replaced by the association's
      direct state; the counters it exposes keep their meaning.
- [ ] B3. Delete `UdpAssociationPool.cs`, `UdpControlAssociation.cs`, `UdpAssociationLease.cs`,
      `UdpAssociationEvidence` (inside the capability file), `UdpAssociationCapability.cs`, and the
      per-server set/capability bookkeeping. Keep `UdpAssociationTable`/`UdpAssociation` as the alias
      registry (design §2).
- [ ] B4. Composition: `UdpProxyComposer.CreateAssociationPool` goes, `DurableCaptureBundle` stops
      constructing/disposing the pool, and the coordinator keeps only its transport factory.
- [ ] B5. Observability per design §4: keep `udpAssociationLost` / `udp.association.lost` and the
      `udpAssociation=<generation>` field (the flow's own association id), delete
      `udpAssociationRecovered`, `udpAssociationFallbacks`, and the `udp.association.recovered` /
      `udp.association.fallback` events. Update the README event table in Phase E.
- [ ] B6. Tests: delete the sharing suites (`UdpAssociationPoolTests`, `UdpAssociationCapabilityTests`,
      `UdpAssociationRecoveryTests`, `UdpAssociationHeadTests`, `UdpAssociationEvidenceLifetimeTests`,
      the pool-shaped parts of `UdpAssociationFakes`), and rewrite the fixtures that used the pool to
      the per-flow path (`UdpProxyCompositionTests`, the Socks5 lease/associate tests,
      `ScriptedSocks5UdpServerOrderingTests`, the benchmark construction sites). New tests: the 1:1
      ownership and teardown shape (control connection + relay socket released with the flow), and a
      control connection dying mid-flow failing the flow closed with re-establishment on the next
      datagram.

## Phase C — harness and history

- [ ] C1. `--reuse` and the `SoakReuseMode` enum leave `SoakOptions`; an old command line meets the
      harness's existing unknown-argument error. Rows drop the `reuse` parameter; `metadata.options`
      drops `reuseMode`.
- [ ] C2. `udp.sessionBudget` loses the pooling half (`--require-pooling`,
      `associationsPerSession`, the pooled verdict fields in `UdpSessionBudgetAcceptance`) and keeps
      the budget/descriptor/retention half.
- [ ] C3. Add the "removed feature" note to every historical results directory that carries
      `reuse`-column numbers (`2026-09-28-udp-reuse`, `2026-10-05-udp-reuse-ownership`,
      `2026-10-05-local-target`); never edit a number.
- [ ] C4. Re-run `udpChurn` and `udpBurstEstablishment` on the surviving columns (per-flow and
      `--target local`) into `benchmarks/results/<date>-no-association-sharing/`, so the shipped shape
      has a current series rather than only the historical `off` column. Scenario timings are
      cheap; the runs are not a new comparison, they are the confirmation that the surviving columns
      still hold their identity.

## Phase D — documentation and the decision record

- [ ] D1. README: drop the sharing/fallback documentation and the removed keys, remove the
      `proxyServer` rejection clause, rewrite the UDP resource-shape prose for one association per
      flow, state the guarantee (one flow per association; the shared reply ambiguity cannot occur),
      and make the local target the recommended DNS placement.
- [ ] D2. `research/decision-record.md`: the evidence, the rejected alternatives (concurrent sharing,
      the warm pool and its stale-reply window, L1's payload-inspection clause), the shipped
      guarantee, and the reopen condition (L2 + the seam it plugs into). This feeds parent R5.
- [ ] D3. `research/l2-readiness.md` per design §7 — written for the L2 task, not for this one.

## Phase E — gates

- [ ] E1. `dotnet build WinForward.slnx -c Release` — zero warnings.
- [ ] E2. `dotnet test WinForward.slnx -c Release` — green (the pre-existing
      `LayeredCaptureRunnerRefreshTests` flake is re-run before being reported red).
- [ ] E3. `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` — exit 0,
      empty output.
- [ ] E4. `jb inspectcode -f=Xml -e=HINT -o=<path> WinForward.slnx` — zero `<Issue>`; any finding is
      fixed or narrowly suppressed with a documented reason and recorded in this task's suppression
      inventory.

## Risky files and rollback points

| Area | Why risky | Guard |
| --- | --- | --- |
| `Socks5UdpTransport` / its factory | the shipped UDP path's setup and teardown | the recorded `--reuse off` columns are the target shape; setup/teardown tests stay green; add the ownership test |
| `UdpProxyCoordinator` / `UdpSessionSetup` | alias claim and teardown reasons | keep the alias-table contract and the `AssociationLost` path untouched |
| Config removal | an operator's existing configuration | removal diagnostics, tested |
| Harness | recorded command lines and historical comparability | refuse `--reuse`, annotate (never edit) history |

Rollback: revert the change set. The removed feature was configuration-driven and the replacement
shape is the one the recorded `--reuse off` columns already ran, so no state migration is involved.

## Before `task.py start`

- [ ] `prd.md`, `design.md`, and this file agree on the disposition table (`udpAssociationLost` stays,
  `udpAssociationRecovered`/`udpAssociationFallbacks` go) and on the L2 readiness note.
- [ ] `implement.jsonl` and `check.jsonl` carry real spec/research entries (not the seeded placeholder).
- [ ] The user has approved the final planning summary in a message after it was presented.
