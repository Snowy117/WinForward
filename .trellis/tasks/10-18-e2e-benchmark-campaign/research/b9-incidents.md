# B9 campaign incidents and instrument fixes

## Proxifier service profile was not loaded in pass 1

- Pass 1's proxifier row (started 2026-10-18 21:22:14 VM, main phase ended 21:30:23) reported
  proxy truth tcp=0 udp=0 uot=0; the dual phase repeated the zero. The row's run.json says
  failed=false because every arm completed, but no connection traversed the proxy.
- Pass 1 is the discarded warm-up, so this row is not part of the six measured passes. It is still
  an instrument failure and is disclosed rather than treated as a product result.
- Root cause: the Proxifier service runs as LocalSystem with
  `Proxifier.exe service-mode`. deploy-campaign.sh only stages
  `C:\wfbench\proxifier\bench.ppx`; the service reads a global profile
  (`C:\ProgramData\Proxifier\Default.ppx` or the install directory), which did not exist.
  The service was therefore running with an empty configuration.
- Fix before pass 2 reached its proxifier row: copied `bench.ppx` to
  `C:\ProgramData\Proxifier\Default.ppx` and
  `C:\Program Files (x86)\Proxifier\Default.ppx`.
  Profile SHA-256: `E2E3B10ED5B4D2FD22D20775E589BC8519ED329A2C058F12F12D59E4AE0AF4B9` (1457 B).
  The service was left stopped; the orchestrator starts it for the row.
- During the fix the ProxiFyre `app-config.json` was accidentally overwritten with the Proxifier
  profile. It was restored from `pass1/proxifyre/app-config.json` (408 B, SHA-256
  `FE69DA07C5027916FB8B49E084897021F7150ACA27A343D2D63CA4DE483984F6`) before pass 2's
  proxifyre row.
- Verified in pass 2 at 2026-10-18 22:16:42 VM: proxifier proxy truth tcp=1430, udp=0,
  uot=0, directLeak=0. The service loaded the global profile. The pass 1 row remains the invalid
  discarded warm-up; the measured passes 2-7 use the fixed profile. The loss family excludes
  proxifier because Proxifier does not carry UDP; the TCP/proxified cells are valid.
