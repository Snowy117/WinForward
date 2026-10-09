# Verification raw data — storage note

The task's reports cite raw files by their uncompressed names and record the sha256 of the
**uncompressed** bytes. Files above 256 KiB are stored gzipped so the working tree stays small;
`gzip -dk <file>` restores the original byte stream, which should then match the hash in the report.

| Path | Contents |
|---|---|
| `ac0/` | The AC0 baseline: `REL.jsonl` (result + `attempt` records), `run.json`, the target `ledger.jsonl.gz`, the target's stdout, and the Debug product log under `debug/` (`wf-debug.err.jsonl.gz`, plus the Information-level `wf-info.err.jsonl`). |
| `drain/` | The Phase D arms — `hh-nofin`, `hh-fin`, `cl-nofin`, `rel-nofin` — each an arm directory with `REL.jsonl`, `run.json` and the Debug product log; shared `ledger.jsonl.gz`, `singbox.log.gz`, `analysis.txt`, plan files, the driver script and the drain-only variant diff. |
| `drain-final/` | The final certification arm on the committed revision: `REL.jsonl`, `run.json`, the Debug product log, the target `ledger.jsonl`, and `ledger-window.jsonl` (the 601 rows inside the run window). |

The close evidence is the `tcp.redirect.drain` event (one per clean relay end, with its outcome and
elapsed milliseconds); the exit statistics in `../../drain-verification.md` are derived from the
product logs stored here.
