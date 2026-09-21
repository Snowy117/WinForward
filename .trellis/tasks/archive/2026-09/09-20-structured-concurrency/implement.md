# Implementation: quiescence scope and lifetime enforcement

> Program-level execution plan. This parent is an integration coordinator, not the implementation
> target: the work happens in C1–C4, each of which is planned, implemented, checked, and archived
> independently. This file owns the ordering, the shared gates, and the final integration review.

## Program sequence

```
C1 quiescence-scope ──┐
                      ├─→ C3 lifecycle-migration-cluster ─→ C4 lifecycle-migration-rest ─→ parent integration review
C2 lifetime-analyzers ┘
```

- **C1** and **C2** are independent and may be worked in either order or in parallel.
- **C3** depends on C1 (needs the primitive). C2 should land before C3 so new violations are caught
  during migration; C2 lands with a reason-documented per-file allowlist for the six legacy sites.
- **C4** follows C3 and depends on C1.
- The program is **complete only when the C2 allowlist is empty** — C3 removes the cluster's entries
  and C4 removes the rest.

## Per-child checklist (applies to C1–C4)

1. Complete the child's own planning artifacts (`prd.md`, and `design.md` + `implement.md` since
   each child is a complex task) referencing this program's `design.md` §2/§3/§4 as the shared
   contract. Do not restate the shared design; link to it.
2. `task.py start` the child (after its own review gate).
3. Implement, following `.trellis/spec/backend/*` via `trellis-before-dev`.
4. Run the shared gates below; all must pass with the stated exit status.
5. `trellis-check` the child; resolve every finding.
6. Update the affected specs (`udp-relay.md`, `tcp-local-redirect.md`, `error-handling.md`,
   `hot-path.md`, `quality-guidelines.md`) as the child changes contracts, plus the new
   `async-lifetime.md` (created by C1, extended by C2/C3/C4 — see `design.md` §3.5).
7. Commit the child on its branch; archive the child.

## Shared validation gates

Run from the repo root; do not pipe in a way that hides the exit code.

```bash
dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore
dotnet build WinForward.slnx -c Release
dotnet test WinForward.slnx -c Release
jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-inspectcode.xml WinForward.slnx
```

- `dotnet format` must exit 0 with **empty** output.
- `dotnet build -c Release` must be **zero-warning**.
- `dotnet test -c Release` must stay green (baseline 744 tests; children add theirs).
- `jb inspectcode` report must contain **zero** `<Issue>` entries (the exit code is not trusted).

Child-specific gates:

- **C1**: an allocation-gate test proves a warm `TryEnter`/`Exit` pair allocates 0 bytes; the
  `HotPathAllocationGateTests` suite stays green. The gate implementation (`lock` vs packed-word CAS)
  is chosen here by comparing both variants with the full `benchmarks/WinForward.Benchmarks` suite
  (BDN Perf + Stability soak) and recording the numbers — `design.md` §2.3. C1 also creates
  `.trellis/spec/backend/async-lifetime.md` with the primitive's durable contract (`design.md` §3.5).
- **C2**: analyzer tests cover each rule's positive and negative cases, including the benign
  discards in `src/` (`_ = Interlocked.Add(...)`, `_ = TryAdd(...)`, `_ = _socket.SendTo(...)`),
  which must **not** fire.
- **C3/C4**: for every migrated owner, a test proves `DisposeAsync` returns only after registered
  work completes; the C2 allowlist shrinks by exactly the migrated files, and the program is not
  complete until it is empty.
- **C3 additionally**: (a) a fault-injection test proves that after deleting `TcpRelayFaultObserver`
  and `ObservePump` no unobserved task exception results and `tcp.relay.faulted` is still recorded
  (`design.md` §4.4); (b) a test proves a stalled/cancelled pump still reaches the drain, i.e. that
  `DrainAsync` does not hang (`design.md` §6).

## Review gates

- **Before each child's `task.py start`**: the child's `prd.md`/`design.md`/`implement.md` are
  reviewed against this program's design; the user approves that child's planning summary.
- **Parent integration review (Phase 3)**: after C1–C4, verify the parent acceptance criteria in
  `prd.md` — one primitive type, no remaining hand-rolled copies, build fails on each banned
  pattern, escape hatch auditable, quiescence tests present, allocation gates green, all gates
  green, and the allowlist empty. This review is done on the parent; the parent is not archived
  until it passes.

## Risky files and rollback points

- `UdpProxySession.cs`, `UdpProxyCoordinator.cs` (Send.cs), `TcpRedirectSessionStore.cs`,
  `TcpProxyRelay.cs` — the highest-risk edits; each migration is its own commit.
- `Directory.Build.props` / `Directory.Packages.props` / `.editorconfig` — touched by C2; a bad
  analyzer reference can break every project's build. Verify with a scratch build before commit.
- Rollback: each child is an independent, revertable commit. Reverting C2 disables enforcement but
  leaves C1/C3/C4's primitive migrations intact and useful.

## Follow-up checks before `task.py start` (parent)

- [ ] `prd.md` has passed the convergence pass (no duplicated facts, no resolved-question stubs).
- [ ] `design.md` and `implement.md` exist and are internally consistent.
- [ ] `implement.jsonl` and `check.jsonl` hold real spec/research entries (not seed placeholders).
- [ ] The four children exist and their `prd.md` dependencies match this file's ordering.
