# Suppression inventory — task 09-22-session-creation-cost-redo

Per `.trellis/spec/backend/quality-guidelines.md` (JetBrains inspectcode gate entry): every
suppression added by a task carries a repo-verifiable reason and lands in the task's inventory
for the audit.

Scope: suppressions introduced by this task's harness change. Pre-existing suppressions in
touched files are not re-audited. The tree was verified green by the `trellis-check` pass
(2026-09-22: inspectcode 0 `<Issue>`, format exit 0 empty, build 0 warnings, 804 tests green).

| # | Site | Suppression | Reason (repo-verifiable) |
|---|---|---|---|
| 1 | `benchmarks/WinForward.Benchmarks/Stability/ExternalLoopbackSocks5UdpServer.cs`, `DescribeStandardError` | `#pragma warning disable VSTHRD002` (synchronous wait on a task) | The `.Result` read sits behind `IsCompletedSuccessfully`, so it cannot block; the member is a synchronous diagnostic accessor inside a `throw` path, and awaiting it would force the whole call site async for no behavior gain. |
| 2 | `benchmarks/WinForward.Benchmarks/Stability/ExternalLoopbackSocks5UdpServer.cs`, `StopChildAsync` | `// ReSharper disable once ConvertIfStatementToReturnStatement` | The latch guard reads as an early exit; the suggested ternary folds the "another caller already owns the stop" path into the return expression and hides the double-dispose path (same readability class the repo already keeps as early-exit guards). |

Removed during review rather than suppressed: an `UnusedAutoPropertyAccessor.Global` suppression
for `ExternalLoopbackSocks5UdpServer.EchoEndpoint` — the property had no consumer, so the
property itself was deleted (the child handshake still reports the echo port; no instrument
needs it in the parent).

Not added by this task (pre-existing at HEAD, unchanged): `UdpChurnScenario.cs`'s
`DuplicatedSequentialIfBodies` guard-clause-chain suppression.
