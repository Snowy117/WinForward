# Kernel research and decision archive

> Scope: provenance index for the evidence that came with the WinForward.Kernel import. Read it when
> a rule in the [Kernel spec family](../wfp-architecture.md) or a native gate needs the measurement,
> experiment or decision behind it.

## What is here

Copied from the Kernel repository's `.trellis/tasks/**` — every task below lived under
`archive/2026-10/` at import time — taking:

- every `research/**` document;
- every `verification.md`;
- every `d7-decision.md`.

Journals, workspaces, `task.json`, PRDs and designs were not copied. Source task names are preserved
as directory names, and each task keeps its original `research/` subdirectory. These copies are
verbatim source text in the language it was written (Chinese); the live spec copies changed only the
pointers that had to follow the new location.

These copies are verbatim. Internal paths they mention (for example
`.trellis/spec/backend/wfp-architecture.md` or `.trellis/tasks/...`) are historical and may not
resolve in this repository; use this index and the Kernel spec family for the live locations.

The source repository `WinForward.Kernel` is not archived yet, so the rest of each task tree — PRD,
design, journals, scripts and raw evidence directories — is still there. The source commit and the
branch tips are recorded in [`kernel/MIGRATION.md`](../../../../../kernel/MIGRATION.md).

## Documents

| Source task | Document | Topic | Feeds |
|---|---|---|---|
| `10-20-wfp-phase0-probe` | [phase0-findings.md](./10-20-wfp-phase0-probe/research/phase0-findings.md) | Phase 0 probe: redirect lands and its two destination branches, `localRedirectContext` round-trip and ownership, failed attempts, build/run recipes | [wfp-architecture.md](../wfp-architecture.md) §2 D5/D6, §6 O6, §7.1–7.4 |
| `10-20-wfp-csharp-blob` | [blob-contract.md](./10-20-wfp-csharp-blob/research/blob-contract.md) | Measured C#-side layout, two real blobs, compile-time normalization, differential fixtures, mutation record | [host-rule-blob.md](../host-rule-blob.md) §2–4; [csharp-conventions.md](../csharp-conventions.md) §1, §3 |
| `10-20-wfp-driver-rules` | [findings.md](./10-20-wfp-driver-rules/research/findings.md) | Rule-table concurrency and atomic replace, why block is not in the redirect layer, `ALE_APP_ID`, condition flags, loop-prevention gates, V6/dual-mode, mutation matrix, build pitfalls | [wfp-architecture.md](../wfp-architecture.md) §7.5–7.9; [host-rule-blob.md](../host-rule-blob.md) §5, §7, §8 |
| `10-20-wfp-mvp` | [perf-mvp.md](./10-20-wfp-mvp/research/perf-mvp.md) | MVP THRU-arm E2E performance against the kill criteria, re-run through a real sing-box, kernel CPU/memory/latency | [wfp-architecture.md](../wfp-architecture.md) §1 |
| `10-21-host-udp-classify` | [b2b-smoke.md](./10-21-host-udp-classify/research/b2b-smoke.md) | Host UDP B2b manual VM smoke: build/install, UDP cases, hot replace, counter identities, redirect-context lifetime, TCP regression | [wfp-architecture.md](../wfp-architecture.md) §7.11; [host-rule-blob.md](../host-rule-blob.md) §7 |
| `10-21-host-udp-classify` | [vm-crash.md](./10-21-host-udp-classify/research/vm-crash.md) | WinLTSC bugcheck evidence gathered during B2b and the environment rule it imposes on later gates | [wfp-architecture.md](../wfp-architecture.md) §7.11 (environment) |
| `10-21-host-udp-spike` | [spike-udp.md](./10-21-host-udp-spike/research/spike-udp.md) | UDP spike S1–S4: classify once per association, context lifetime and `WfPr`, UDP block is silent-drop, IPv6/dual-mode | [wfp-architecture.md](../wfp-architecture.md) §2 D4, §7.9, §7.11 |
| `10-21-host-udp-route-a-probe` | [route-a-findings.md](./10-21-host-udp-route-a-probe/research/route-a-findings.md) | Route-A probe: staged findings on unconnected UDP local redirect and the return path | [wfp-architecture.md](../wfp-architecture.md) §7.11 |
| `10-21-wfp-userland` | [userland-findings.md](./10-21-wfp-userland/research/userland-findings.md) | Controller/relay scaffolding: single-source IOCTL ABI, compile-time semantic checks, listener/redirect/relay, E2E results, pitfalls, left-over work | [wfp-architecture.md](../wfp-architecture.md) §3, §5; [csharp-conventions.md](../csharp-conventions.md) §1 |
| `10-22-host-udp-verification` | [connected-context.md](./10-22-host-udp-verification/research/connected-context.md) | Connected vs unconnected UDP redirect-context release windows measured at 1/10/60/180/300 s, and the U12 assertion rule | [wfp-connected-udp-record-refcount-leak.md](../wfp-connected-udp-record-refcount-leak.md) §1, §3; [wfp-architecture.md](../wfp-architecture.md) §7.11 |
| `10-23-fwd-b3-l2-spike` | [environment.md](./10-23-fwd-b3-l2-spike/research/environment.md) | VM environment matrix for L2: network, adjacency, topology verdict, injection API availability, risks, D1 impact | [wfp-architecture.md](../wfp-architecture.md) §7.12; [forwarded-rule-blob.md](../forwarded-rule-blob.md) §9 |
| `10-23-fwd-b3-l2-spike` | [spike-l2.md](./10-23-fwd-b3-l2-spike/research/spike-l2.md) | L2 visibility and injection spike: 2A visibility, 2B injection, failure codes, LWF coexistence, cost, D1 conclusion and conditions | [wfp-architecture.md](../wfp-architecture.md) §7.12; [forwarded-rule-blob.md](../forwarded-rule-blob.md) §9 |
| `10-23-fwd-b4b-tcp-dnat` | [verification.md](./10-23-fwd-b4b-tcp-dnat/verification.md) | B4b TCP DNAT verification: return-path loss rework, VM suites, product E2E, regression, uncovered cases | [wfp-architecture.md](../wfp-architecture.md) §7.13; [forwarded-rule-blob.md](../forwarded-rule-blob.md) §9 |
| `10-23-fwd-b5-udp-gateway` | [verification.md](./10-23-fwd-b5-udp-gateway/verification.md) | B5 UDP gateway root causes: cleaned on-link addresses, fragment fail-open fix, `back=0`, PREfast C6387, table-full/upstream failures, regression | [wfp-architecture.md](../wfp-architecture.md) §7.14; [forwarded-rule-blob.md](../forwarded-rule-blob.md) §10 |
| `10-23-fwd-b6-hardening` | [verification.md](./10-23-fwd-b6-hardening/verification.md) | B6a/B6b forwarded correctness: decoy, fragments, reject, acked, stop/UAF, mixed NBL, capacity 4096, LWF visibility, gate output | [wfp-architecture.md](../wfp-architecture.md) §7.15–7.16; [forwarded-rule-blob.md](../forwarded-rule-blob.md) §9 |
| `10-23-fwd-b6-hardening` | [d7-decision.md](./10-23-fwd-b6-hardening/d7-decision.md) | D7 product decision: WFP L2 stays a kernel-edition candidate instead of replacing NDISAPI/WinpkFilter; adoption conditions | [wfp-architecture.md](../wfp-architecture.md) §6, §7.16; `kernel/AGENTS.md` priorities |
| `10-24-block-classify-enforced-sink` | [verification.md](./10-24-block-classify-enforced-sink/verification.md) | Host block as classify first-hit plus sink port: conclusions, key experiments, gate results, known boundaries | [wfp-architecture.md](../wfp-architecture.md) §2 D4, §7.9; [host-rule-blob.md](../host-rule-blob.md) §7, §8 |
| `10-24-fwd-ipv6-e2e` | [environment.md](./10-24-fwd-ipv6-e2e/research/environment.md) | Forwarded IPv6 E2E environment and fixtures: address plan, code gaps, test semantics, evidence sources | [wfp-architecture.md](../wfp-architecture.md) §7.17 |
| `10-24-fwd-ipv6-e2e` | [verification.md](./10-24-fwd-ipv6-e2e/verification.md) | Forwarded IPv6 data-plane E2E: environment/driver, changes, full gate, per-stage assertions, debugging and negative verification | [wfp-architecture.md](../wfp-architecture.md) §7.17 |
| `10-24-host-rule-arrays-process-match` | [winforward-rule-semantics.md](./10-24-host-rule-arrays-process-match/research/winforward-rule-semantics.md) | WinForward's rule model, the kernel's expression limits, measured WFP block boundary, constraints handed to the design | [host-rule-blob.md](../host-rule-blob.md) §1, §6; [wfp-architecture.md](../wfp-architecture.md) §2 D3/D4 |
| `10-24-host-rule-arrays-process-match` | [plan-review.md](./10-24-host-rule-arrays-process-match/research/plan-review.md) | Plan review of the blob v2 batch: P0 must-fix, P1 missing work and order, P2 | [host-rule-blob.md](../host-rule-blob.md) §2–4 |
| `10-24-host-rule-arrays-process-match` | [structural-review.md](./10-24-host-rule-arrays-process-match/research/structural-review.md) | Structural review of the merged diff: must-fix items and same-batch suggestions | [host-rule-blob.md](../host-rule-blob.md) §2; [cpp-conventions.md](../cpp-conventions.md) §8 |
| `10-24-host-rule-arrays-process-match` | [final-check.md](./10-24-host-rule-arrays-process-match/research/final-check.md) | Final check: executed commands, saved gate output, PRD A1–A11, design/test/document consistency, issue list | [host-rule-blob.md](../host-rule-blob.md) §2–4, §9; [csharp-conventions.md](../csharp-conventions.md) §5; [quality-guidelines.md](../quality-guidelines.md) |
| `10-24-host-rule-arrays-process-match` | [verification.md](./10-24-host-rule-arrays-process-match/verification.md) | Blob v2 functional acceptance, batch record, known boundaries, final command list, mutation evidence | [host-rule-blob.md](../host-rule-blob.md) §2–4, §9 |

## Reading order

- Driver internals and why the WFP shape was chosen: `10-20-wfp-phase0-probe` →
  `10-20-wfp-driver-rules` → `10-21-wfp-userland`.
- Host UDP: `10-21-host-udp-spike` → `10-21-host-udp-route-a-probe` → `10-21-host-udp-classify` →
  `10-22-host-udp-verification`; the separate leak note is
  [wfp-connected-udp-record-refcount-leak.md](../wfp-connected-udp-record-refcount-leak.md).
- Host rules and block: `10-24-host-rule-arrays-process-match` (semantics, reviews, acceptance) →
  `10-24-block-classify-enforced-sink`.
- Forwarded: `10-23-fwd-b3-l2-spike` → `10-23-fwd-b4b-tcp-dnat` → `10-23-fwd-b5-udp-gateway` →
  `10-23-fwd-b6-hardening` → `10-24-fwd-ipv6-e2e`; the D7 acceptance question is
  `10-23-fwd-b6-hardening/d7-decision.md`.
- Performance against the kill criteria: `10-20-wfp-mvp`.
