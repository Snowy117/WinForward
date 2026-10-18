# Design: harness workstream

See the parent `design.md` for the release-plan contract, thresholds file, orchestrator artifacts
and verdict rules. This child owns B2–B7.

## File map

| Batch | Files |
| --- | --- |
| B2 | `benchmarks/WinForward.E2E/scripts/plans-release/*.json`, `orchestrator.ps1` (row table only), `WinForward.E2E.Analysis/Model/RowProfiles.cs`, `verification/row-profiles.json`, `scripts/configs/*.json`, README tables |
| B3 | `benchmarks/WinForward.E2E/scripts/orchestrator.ps1`, `Client/` order flag, `Analysis/Loading/*`, `Analysis/Findings/*`, `Tables/TableEnvironment.cs` |
| B4 | `benchmarks/WinForward.E2E/Target/*`, release plan `modeMix`, sensitivity plan, tests |
| B5 | `Analysis/Cli/AnalysisOptions.cs`, `Analysis/Stats/BootstrapPair.cs`, `Analysis/Verdict/*`, `Analysis/Metrics/*`, `Analysis/Checks/*`, tests, golden fixtures |
| B6 | `Client/` SOCKS5 flag, orchestrator control row, control plan, analysis row support |
| B7 | `scripts/{wf,deploy-campaign,start-targets,publish-campaign}.sh`, `campaign.env`, `.gitignore`, `deploy.json` |

## Rules

- Read the spec family that owns the change before editing: `measurement-record-contract.md` for
  keys/records, `measurement-judgement.md` and `measurement-tooling.md` for gates, `benchmark-
  methodology.md` for "prove it can fail", `quality-guidelines.md` for the commit gates.
- Do not regenerate a golden to hide a semantic change; record the change in `FROZEN.md` and add a
  boundary tree.
- Do not commit. The main session commits.
