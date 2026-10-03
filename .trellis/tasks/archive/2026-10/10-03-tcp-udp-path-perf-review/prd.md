# TCP/UDP path structural performance review (2026-10)

## Goal

Second-pass structural performance review of the TCP/UDP data paths (capture pump, dispatcher,
flow table, TCP redirect, UDP proxy, SOCKS5 transport), following up on
`09-29-tcp-udp-path-structural-perf` now that its F1–F8 findings have landed. The output is a
current-state findings document plus a ranked optimization roadmap. Documentation-only task:
no code changes; every finding is a candidate child task for the parent program
(`08-30-proxy-perf-stability`).

## Scope

- Read: the whole capture → classify → dispatch → execute trunk (`NdisApi`, `Core`,
  `Runtime/Capture`, `Runtime` root), the full `Runtime/TcpRedirect/`, `Runtime/UdpProxy/`,
  `Runtime/Socks5/`, and `Protocols` checksum/rewrite layers, against the tree at 2026-10-03.
- Re-verify which 2026-09-29 research findings (F1–F8, A1–A6) are landed, so nothing already
  fixed is re-proposed.
- Anchor claims to file:line evidence and to the recorded benchmark/baseline numbers where
  they exist.

## Out of scope

- Code changes, benchmark runs, and Windows-host experiments. This task produces the findings
  document only; implementation happens in child tasks.
- Per-packet managed-allocation micro-audits of the warm dispatch path — contract-locked at
  zero by `hot-path.md` and its gates; re-auditing adds nothing.

## Requirements

- The report must separate "already landed (do not re-tread)" from "still present in the
  current tree".
- Every finding names: evidence (file:line), the cost mechanism, and a concrete optimization
  algorithm/approach — not generic advice.
- Findings are ranked by benefit/cost so the parent backlog can pick child tasks directly.

## Acceptance Criteria

- [x] `research.md` records the landed-state of the 2026-09-29 findings (F1–F8/A-series).
- [x] `research.md` records the new findings with file:line evidence, grouped and ranked.
- [x] `research.md` records the reviewed-and-sound list (areas verified as non-issues, to
  prevent repeat audits).
- [x] A ranked roadmap with rough cost/benefit per item is included.

## Deliverable

- `research.md` in this directory — the full review report.
