# Campaign hang evidence (2026-10-06, pass2 / `wf-aot-native`)

While this task was reproducing the half-close defect on the VM, the user's four-pass E2E campaign was
found **hung**, not finished: the client process had been sitting in the `MIX` arm since 07:56 MST
(>12 minutes for a 40 s arm), `sing-box` was gone, and the campaign heartbeat had not been touched for
22 minutes — the watchdog's 1500 s threshold, which had already fired once at 07:05.

The stuck client (`WinForward.E2E.exe`, PID 4896) reads no further than `arm MIX (mix) starting`;
`orch.log` shows the last row transitions and `watchdog.log` the earlier emergency teardown. Note the
`REL` arm's own line: `finished in 129.9s` against a 120 s budget — the 10 s overshoot is the
half-close hangs this task fixes, visible in the campaign's own transcript.

Files: `client.out`, `orch.log`, `watchdog.log`, `config.json` (the row's product configuration). The
arm result JSONLs from that row were preserved in the sandbox and are not kept in the repository.
