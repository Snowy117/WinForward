# Synthetic verification tree

`make_tree.py` writes a fabricated multi-pass campaign tree that follows the harness's own
record schema, for exercising `analysis/analyze.py` without waiting for a campaign. Every
number in it is invented.

```bash
python3 synthetic/make_tree.py /tmp/wf-synth/raw
python3 analyze.py --raw /tmp/wf-synth/raw --out /tmp/wf-synth/out
```

It deliberately contains the seven measured rows with their differing plans, both control
blocks, a dual phase with a `directLeak`, a `DNSALT` arm, a `PERSIST` arm, a
`foreignConnection` finding, a UDP accounting-identity violation, a zero lane witness, a
`samplerError` record, a sample carrying `readError`, a mid-run product restart, a
`scheduledAttempts` mismatch, a `control-post` drift and a per-pass `target-ledger.jsonl`.
