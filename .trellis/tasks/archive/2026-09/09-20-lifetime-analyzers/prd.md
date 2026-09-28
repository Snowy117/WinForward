# Lifetime enforcement analyzers

## Goal

Create `analyzers/WinForward.Analyzers/` with three rules — `WF0001` (discard of an **unawaited**
awaitable), `WF0002` (`.ContinueWith`), `WF0003` (`Task.Run`/`Task.Factory.StartNew` outside the
allowlist) — wire them into `src/**`, and land them with a reason-documented per-file allowlist for
the legacy sites.

## Requirements

- Project at `analyzers/WinForward.Analyzers/` (netstandard2.0, `Microsoft.CodeAnalysis.CSharp`),
  referenced from a new `src/Directory.Build.props` that first `<Import>`s the repo-root one;
  Roslyn packages are pinned in `Directory.Packages.props` (`design.md` §3.1, E3).
- **Four rules**: `WF0001` (unawaited awaitable discard; must not fire on `_ = await X`), `WF0002`
  (`.ContinueWith`), `WF0003` (`Task.Run`/`Task.Factory.StartNew`), `WF0004` (bare unawaited
  awaitable expression statement — restored 2026-09-21 because `CS4014` fires only inside `async`
  methods; `design.md` §3.2, E1).
- Rules key on **awaitable types**.
- The escape hatch is `QuiescenceScope.Run` (C1's type), not a `Detach` method or attribute (E2).
- Severity is explicit `error` in `.editorconfig`; add `AnalyzerReleases.Shipped.md` /
  `AnalyzerReleases.Unshipped.md` (E5).
- Allowlist: per-file, written reason, glob-scoped (E4). Every temporary entry is deleted by C3/C4,
  so C2 must leave `src/**` byte-identical — the rules land with the allowlist, not with migration.

  | File | Rules | Why | Removed by |
  |------|-------|-----|-----------|
  | `src/WinForward.Runtime/TcpRedirect/TcpProxyRelay.cs` | `WF0001` `WF0002` | `:289` `_ = first.ContinueWith(...)` | C3 |
  | `src/WinForward.Runtime/TcpRedirect/TcpRelayFaultObserver.cs` | `WF0001` `WF0002` | `:27` `_ = relay.Completion.ContinueWith(...)` | C3 (file deleted) |
  | `src/WinForward.Runtime/TcpRedirect/TcpRedirectSessionStore.cs` | `WF0001` | `:157` `if (start is not null) _ = RunDisposeAsync(start);` | C3 |
  | `src/WinForward.Runtime/UdpProxy/UdpProxySession.cs` | `WF0001` | `:312` `_ = receiveFailureHandler(this);` | C3 |
  | `src/WinForward.Runtime/Capture/MultiAdapterCaptureLoop.cs` | `WF0001` | `:110` `_ = ForwardDegradationAsync(...)` | C4 |
  | `src/WinForward.Runtime/Capture/LayeredCaptureRunner.cs` | `WF0003` | `:144` `Task.Factory.StartNew`, `:149` `Task.Run` | C4 |

  One **permanent** exemption, not on the shrink list: `WF0001` for
  `src/WinForward.Runtime/QuiescenceScope.cs` — the primitive's own `_ = RunChildAsync(...)` (`:133`)
  and `_ = DrainCoreAsync(...)` (`:170`) are tracked children whose leases and faults it owns.
- Adds the WF rule table to `.trellis/spec/backend/async-lifetime.md` (created by C1).

## Acceptance Criteria

- [ ] Each rule fails the build on a scratch violation and is proven **not** to fire on the benign
      shapes enumerated in `design.md` §3.2. `WF0004` specifically fires on a bare awaitable
      statement in **both** an `async` method and a **non-async** method.
- [ ] `src/**` is byte-identical to its pre-C2 state.
- [ ] Analyzer tests exist in `tests/WinForward.Analyzers.Tests`.
- [ ] The allowlist contains exactly the six temporary entries above plus the one permanent
      `QuiescenceScope.cs` exemption, each with a written reason.
- [ ] `tests/**` and `benchmarks/**` are not flagged.
- [ ] Full gates green (format / Release zero-warning / tests / `jb inspectcode`).

## Notes

- The analyzer code is independent of C1. The `async-lifetime.md` rule table is appended **after** C1 has
  created that file, to keep the file single-owner and avoid a conflict.
- This child is a complex task: write its own `design.md` and `implement.md` before `task.py start`,
  referencing the parent `design.md` §3.
- Verified follow-up notes (check pass, 2026-09-21, non-blocking, **not** fixed because neither has a
  trigger in `src/` and a speculative "fix" would be unreviewable): `AwaitableClassifier.cs:56` accepts
  *any* parameterless delegate for `OnCompleted`, while the compiler requires `System.Action` — a type
  with `OnCompleted(MyDelegate)` would be classified awaitable but is not, i.e. the rule errs toward
  over-firing (strict). Conversely `AwaitableClassifier.cs:41` reads declared members only, so an
  awaiter that *inherits* `IsCompleted`/`OnCompleted` is missed (under-firing, silent). Both are
  candidates for a hardening pass if the primitive ever meets a custom awaitable.
