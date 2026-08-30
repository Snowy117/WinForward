# Implementation Plan: Hot dispatch path revival

Read order: `implement.jsonl` entries → `prd.md` → `design.md` → this file. All
file:line references were re-verified 2026-08-30 post-fast-hardening; re-check before
editing anyway.

## Step 0 — Production-path benchmark variants (D4) — LAND FIRST

- [ ] Add `WarmPassProduction` / `WarmProxyProduction` (and optional
      `ReverseCandidateSlowPath` control) to `DispatcherBenchmarks`: dispatcher wired
      with a fake `ITcpReverseHandler` whose `WantsPacket` runs the real predicate
      (protocol + real `TcpRedirectTable` port array; pure managed, benchmark-safe) and
      whose `HandleReverseIfApplicableAsync` returns `NotRelevant`.
      - If landing these before the interface change is awkward, land Step 1's
        interface first and the benchmarks immediately after — but the PRE-FIX numbers
        must be recorded before (a)/(b) change any dispatch behavior: measure with a
        `WantsPacket`-always-true fake to capture today's "everything diverts" reality.
- [ ] Record pre-fix numbers in the task notes (this is the evidence baseline proving
      X1: production shape allocates like the slow path, not the warm 160 B).

## Step 1 — Fix (a): `ITcpReverseHandler` + protocol gate (D1)

- [ ] Add `internal interface ITcpReverseHandler` (design D1) with
      `WantsPacket(in CapturedFlowPacket)` + `HandleReverseIfApplicableAsync(...)`.
- [ ] `TcpProxyCoordinator` implements it (existing method satisfies the handle
      signature; add `WantsPacket` stage (a): protocol == Tcp only).
- [ ] `FlowDispatcher`: `_reverseHandler` field/constructor param change type
      `Func<...> → ITcpReverseHandler?`; warm entry uses
      `_reverseHandler is { } handler && handler.WantsPacket(packet)` for the
      diversion; `TryHandleReverseAsync` calls the interface method (full handler,
      never `WantsPacket`).
- [ ] `WinForward.Cli/Program.cs:271`: wiring unchanged in effect (coordinator now
      implements the interface — adjust only if the constructor call shape changes).
- [ ] Update test fakes that passed lambdas to small fake `ITcpReverseHandler` objects.
- [ ] Update the `DispatchAsync` XML doc for the new diversion semantics.
- [ ] Tests: UDP warm shapes with handler wired → warm decisions, handler not invoked;
      TCP → diverted (handler invoked); handler-null → unchanged.
- [ ] `dotnet test` green; benchmark production variants now warm for UDP/non-TCP.

## Step 2 — Fix (b): port prefilter in `WantsPacket` (D2)

- [ ] `TcpRedirectTable`: `int[65536] _candidatePorts`; `Interlocked.Increment` in
      `TryClaim` success path (before injection, under `_gate`); `Interlocked.Decrement`
      in `TryRemove` removal (under `_gate`); `internal bool IsReverseCandidatePort(
      ushort port)` via `Volatile.Read != 0`.
- [ ] `TcpProxyCoordinator.WantsPacket` stage (b): protocol == Tcp &&
      `_table.IsReverseCandidatePort(key.Local.Port)`.
- [ ] Tests: prefilter unit tests (claim/remove/concurrent/read-before-inject);
      dispatcher integration (candidate → diverted+handled; non-candidate TCP → warm,
      handler not invoked; tombstone-straggler fall-through → slow path → `Dropped`
      pins the safety theorem).
- [ ] `dotnet test` green; `dotnet build -warnaserror` clean.

## Step 3 — Gates & records

- [ ] `DispatcherBenchmarks` production variants: WarmPass/WarmProxy Production must
      equal the warm 160 B baseline and ns in the warm ballpark (AC4); candidate
      control shows slow-path figure. Record before/after in the task report.
- [ ] `TcpRelayBenchmarks` spot-check (no relay changes expected).
- [ ] Stability quick gate: tcp + udp clean (otherErrors == 0, loss 0).

## Validation commands

```bash
dotnet build -warnaserror
dotnet test
dotnet run --project benchmarks/WinForward.Benchmarks -c Release -- --filter '*Dispatcher*'
dotnet run --project benchmarks/WinForward.Benchmarks -c Release -- --stability --quick
```

(Check `benchmarks/WinForward.Benchmarks/Program.cs` for the exact arg shape.)

## Review gates

- After Step 1: `WantsPacket` semantics (conservative-for-diversion-only) and the
  unchanged slow-path completeness.
- After Step 2: Inc-before-inject ordering + the fall-through theorem's test pin.

## Rollback

Each step is one logical commit. (a) alone is production-safe if (b) slips; benchmark
variants are inert additions.
