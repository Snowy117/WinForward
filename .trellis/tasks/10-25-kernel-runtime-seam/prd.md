# Batch 5a: extract the Kernel runtime seam

Parent: `.trellis/tasks/10-25-kernel-backend-integration`

## Goal

Create `src/WinForward.Kernel.Runtime` and move the userland helpers the Kernel host-TCP backend
will need out of the Kernel scaffolding: the redirect-context query/parse, the host/forwarded
context sources, `BlockSink` and `ListenerSet`. The old `WinForward.Userland` project references the
new project, so the native tests, Controller and `wfprobe` keep building. No device client
configuration, backend accept loop or data path is added in this batch.

## Requirements

- **R5a-1 New project.** Add `src/WinForward.Kernel.Runtime/WinForward.Kernel.Runtime.csproj`
  (root build props, `net10.0`, no Runtime/NdisApi dependency; references
  `WinForward.Kernel.Contracts`), the root solution entry and the native
  `WinForward.Userland.slnx` entry. Keep the moved namespaces `WinForward.Userland.Relay` for this
  extraction; document the transitional project/namespace mismatch.
- **R5a-2 Moved helpers.** `git mv`:
  `Relay/SocketIoctl.cs`, `Relay/RedirectDestination.cs`, `Relay/IRedirectContextSource.cs`,
  `Relay/HostRedirectContextSource.cs`, `Relay/ForwardedRedirectContextSource.cs`,
  `Relay/BlockSink.cs`, `Relay/ListenerSet.cs` into the new project. The old project gets a
  `ProjectReference`; its remaining `Relay`/`Control`/`Configuration` code compiles through it.
  Grant `InternalsVisibleTo` for `WinForward.Userland.Tests` only if the moved internals need it.
- **R5a-3 Behaviour unchanged.** No changes to socket options, redirect-context magic/version,
  `BlockSink` ports, `ListenerSet` bind/retry semantics, forwarded context fields or the ABI
  structs. The Kernel native tests and the Controller/wfprobe builds are the oracle.
- **R5a-4 Quality.** Root analyzers/format/build/test and the Kernel `check.sh --full` pass. Narrow
  analyzer suppressions are allowed with a reason; no global opt-out. The known `jb` environment gap
  stays recorded.
- **R5a-5 Scope.** No Kernel backing device configuration, no host TCP accept/relay, no raw-socket
  relay overload (next batch), no host UDP/forwarded, no Runtime/NdisApi/driver changes, no
  unrelated dirty files.

## Acceptance Criteria

- [x] R5a-AC1: root build, test (1958) and format pass; the new project is in both solutions and
      builds Release in the native solution.
- [x] R5a-AC2: Kernel `check.sh --full` is 8 PASS; the native solution, test runner, Controller and
      `wfprobe` still build against the moved helpers.
- [x] R5a-AC3: no ABI/socket/bind/redirect-context behavior change; no device/backend/data path
      added.
- [x] R5a-AC4: no Runtime/NdisApi/driver/BackendDriver changes and no unrelated dirty files.

## Out of Scope

- `KernelBackend`, device config publication, host TCP accept/relay, raw-socket relay/end-kind,
  host UDP, forwarded, ARM64, packaging.
