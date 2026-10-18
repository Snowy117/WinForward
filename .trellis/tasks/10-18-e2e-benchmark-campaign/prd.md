# E2E benchmark campaign: calibration, release run, report

## Goal

Run the calibration night to pre-register the decision band, then run the 7-pass release campaign
under the new plan and produce a report whose claims stay inside the measured evidence.

## Scope

- B8: preflight, three reference runs, `thresholds.json`, `BASE-singbox` record, sensitivity proof.
- B9: seven passes (one discarded + six measured), attempt archive, analysis, report, archive and
  manifest.

## Non-goals

- No code changes in this task; harness defects go back to the harness child.
- No product ranking; no absolute-latency or WAN claims.

## Acceptance Criteria

- [ ] Calibration band exists before B9; the file names the three runs and the formula.
- [ ] Release campaign has seven archived passes with attempt metadata; the discarded pass is
      identified.
- [ ] All gates are green; the clock gate and positive control have recorded evidence.
- [ ] The report contains compact/limited calibers, primary family, band/MDD, coverage, attempts and
      limits.
- [ ] Archive verifies with `sha256sum -c`; manifest matches.
- [ ] The user has a go/no-go summary before the release night.

## Dependencies

- B3–B7 complete; B1 archive manifest available for the new campaign.
