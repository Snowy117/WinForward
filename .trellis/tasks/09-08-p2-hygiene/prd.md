# P2: dead-surface deletion and hygiene

Parent: `09-08-design-review-remediation` · Evidence: `../09-08-design-review-remediation/research/00-synthesis.md` (P2 table) + area reports 01/02/05

## Goal

Dead-surface deletion, TestHelpers convergence, indexed config diagnostics, Cli test coverage.
Lightweight task: PRD-only, executed as ordered mechanical batches.

## Global constraints

- Deletion rule (directory-structure.md:72): before removing any dead public member, rg the whole
  repo (src/ + tests/ + benchmarks/) and record zero call sites. **ABI P/Invoke declarations stay
  even when their managed wrappers die** — only managed helper methods are deletion candidates.
- Every batch: zero-warning build + green tests; record before/after totals.

## Requirements

### R1 — `FlowTable` dead/duplicate surface

- Delete `TryGet` (zero callers anywhere); delete or fold `TryClaim` (behaviorally ≡
  `TryClaimResolved`, test-only users migrate to `TryClaimResolved`); delete `Claim` (test-only;
  migrate its tests). Evidence: research `01-core-configuration.md` §2.
- Production must still compile using only `TryResolve` / `TryClaimResolved` / `RemoveExpired`.

### R2 — Dead interface + junk drawer in WinForward.Windows

- Delete `IWindowsAdapterInventory` (zero consumers; call sites bind the concrete class; tests
  inject delegates). Evidence: research 02 issue 1.
- Split `Platform.cs` (5 unrelated public types in 58 lines) into cohesive files
  (PlatformRequirements / AdapterSelector+WindowsAdapter / IProcessAttributor+NdisAdapter).

### R3 — Dead managed wrappers over retained native imports

- rg-verify then delete `EnsureDriverVersion` (+ its consumption, if any) and `InterpretReadResult`
  managed helpers; **keep** `GetDriverVersion`/`ReadPacket` P/Invoke declarations per the ABI
  preservation rule. Evidence: research 02 issue 4.

### R4 — Visibility tightening

- `NormalizePath` (ProcessSelectors.cs:37): public with no external callers → internal/private.
- `FlowKey.GetHashCode`/`Equals` Origin-asymmetry + TransportTuple parallel: add the missing
  why-comments (no behavior change). Evidence: research 01 issue 7.

### R5 — TestHelpers convergence (directory-structure.md:82-83)

- Promote to TestHelpers (≥2-file rule): `FakeModes`, `CompletingCapture`/`BlockingCapture`,
  `ScriptedReader`, `NoopResponseSink`.
- Remove private `NoSelfTraffic`/`NeverSelfTrafficGuard` duplicates ×3 (promoted `FakeGuard`
  already covers).
- Resolve the `FakeExecutor` name collision (FlowDispatcherTests private fake vs TestHelpers fake):
  rename one so names map 1:1 to behavior.
- Result: zero cross-file private fake duplication for the audited seams (research 05 §4 list).

### R6 — Indexed diagnostics for invalid config array values

- `ParseNetworks`/`ParseSet`/`ParsePorts` (ConfigurationModels.cs:355,368,383,390) must report
  `[i]`-indexed paths for invalid elements, matching the null-element behavior and the
  error-handling spec ("invalid or null ... at their indexed field paths"). Add the missing
  `LogLevel` typing consistency check while there (`JsonElement` vs `string?` divergence,
  research 01 issue 5) — if it balloons, split into its own follow-up.

### R7 — Cli coverage gap

- Sink `Program.LogAdapterTransientRetry` (rate-limit CAS algorithm) into Runtime next to
  `RuntimeLogging`; unit-test the rate-limit window semantics.
- Add tests for `DurableCaptureBundle.UpdateUdpTargets` (zero-MAC filter/fallback/empty-scope
  clearing) via the already-configured `InternalsVisibleTo`. Evidence: research 05 §1/§2.

### R8 — Doc correction

- `NdisPacketBuffer` XML doc overpromises double-`Dispose` no-op semantics vs pool re-rental
  reality (research 02 issue 5): correct the doc to state the rental-window contract.

## Acceptance criteria

- [x] All deletions carry an rg re-verification note (command + zero-hit result) in the task notes
      or commit message. (All in `implement-notes.md` — R5–R8 by the implement agent, R1–R4
      backfilled by the check agent.)
- [x] Production code compiles with no reference to any deleted symbol; tests migrated, not
      weakened (assertion semantics unchanged). (5 FlowTable tests migrated to
      `TryClaimResolved`; 3 helper-only NdisApiAbi tests deleted — expected −3, documented.)
- [x] TestHelpers audit clean: none of the R5-listed fakes remain privately duplicated; no
      same-name-different-behavior fakes. (FakeModes/capture loops → CaptureLifecycleFakes.cs;
      ScriptedReader → own file; NoopResponseSink → UdpTransportFakes.cs; FakeGuard gained
      `Owned` switch; private FakeExecutor subset deleted — promoted fake is a superset.)
- [x] Config tests cover indexed paths for invalid (not just null) array elements. (3 new exact
      path assertions; LogLevel JsonElement→string? unification deferred — needs custom
      converter to distinguish explicit-null from absent; see Notes.)
- [x] Cli: `LogAdapterTransientRetry` semantics + `UpdateUdpTargets` policy under test.
      (`AdapterTransientRetryLogGate` relocated to Runtime with injectable clock, 4 deterministic
      tests; 5 `DurableCaptureBundleTests` cover zero-MAC/fallback/empty-scope/snapshot.)
- [x] Zero-warning build; green tests; before/after totals recorded.
      (569 → 566 (−3 deleted helper-only tests) → 578 (+12 new); final 578/578. Commits:
      de1152a, 01326fc, a8836b7, bcdf50a, a8e5cfc on branch `p2-hygiene`. trellis-check: PASS.)

## Notes

- Execute before `09-08-p1-structure` (shrinks the surface P1 moves). Independent of P0.
- If R6's LogLevel piece turns out non-mechanical, defer only that piece with a note here.
- **R6 LogLevel deferral (2026-09-08, R5–R8 pass)**: the `JsonElement` → `string?` unification is
  deferred — a `string?` property cannot distinguish `"logLevel": null` (a pinned validation
  error) from an absent field (defaults to Info) without custom-converter machinery. See
  `implement-notes.md` for details.
