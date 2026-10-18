# E2E benchmark harness: release plan, gates, statistics

## Goal

Implement the agreed release caliber and make the instrument falsifiable: a new release plan set,
row profiles and committed configs; orchestrator P0 gates; a positive control; a band-aligned
verdict model with gate-into-verdict; a calibration-only `BASE-singbox`; and a tracked campaign
driver.

## Scope

- B2: `plans-release/`, orchestrator row plan, row profiles, committed configs, README.
- B3: clock gate, environment read-before-change, `failures.json`/failed rows, clean-slate,
  arm-order randomization; the `connectionId` caption is deferred to the release mode.
- B4: target delay injection, sensitivity plan, `stall` in `modeMix`.
- B5: release path (oracle strategy B): thresholds schema, primary family, Bonferroni-adjusted
  band, `unevaluable`, TOST family, REL capability, clock correction, gate matrix; legacy
  `tables.md`/`verdict.json` stay byte-identical.
- B6: `BASE-singbox` control.
- B7: tracked and parameterized campaign driver, `campaign.env`, `deploy.json`.

## Non-goals

- No `connectionId` implementation; only the caption correction.
- No BCa rewrite; no QUIC/HTTP3; no product changes.
- No campaign run in this task (the campaign child owns B8/B9).

## Acceptance Criteria

- [ ] Release plans load; row profiles and golden fixtures are updated; configs are committed.
- [ ] Every P0 gate is proven to fail at least once.
- [ ] The verdict model reports `unevaluable`, uses the pre-registered band and Holm family, and
      prints coverage.
- [ ] Gate failures invalidate verdict cells; UDP identity checks all prefixes; DNS has a
      `clientSendLoss` denominator.
- [ ] `BASE-singbox` runs as a calibration control.
- [ ] The four campaign scripts are tracked and parameterized; `deploy.json` records commit/dirty.
- [ ] `dotnet build/test`, analyzer gates, `bash -n` and `pwsh` parse checks pass.

## Dependencies

- B0 for the decisions and measurement model.
- B1 can run in parallel with B2.
