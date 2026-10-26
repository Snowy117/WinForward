# Batch 5c-2: Kernel host TCP backend

Parent: `.trellis/tasks/10-25-kernel-backend-integration`

## Goal

Implement `KernelBackend` for host TCP: open the WFP device, publish the compiled host blob and
target table, bind the listener and block sink, accept redirected TCP connections, resolve the
target through the rule-index table, relay through the existing TCP relay, enforce capacity, and
tear down in the fail-closed order. Wire `Program` so the Kernel provider's `run` constructs this
backend instead of refusing. Linux build/tests and static review are this batch; the Windows VM E2E
is Batch 5d.

## Requirements

- **R5c2-1 Selection.** `Program.RunCaptureAsync` selects `KernelBackend` for
  `Provider = WinForward.Kernel` and keeps `WinpkFilterBackend` for WinpkFilter. The Batch 2
  Kernel refusal is removed. The provider guard (backend name == configured provider) stays. The
  Kernel backend is `[SupportedOSPlatform("windows")]`; the existing Windows/platform checks stay.
- **R5c2-2 Startup.** Open `WfpDeviceClient`, verify the control ABI; disable host config; disable
  forwarded config and install an empty forwarded blob (clear stale state); compile the policy with
  the Windows process-path normalizer and the rule-index target table (`ContainsBlock` from the
  compiler); bind `ListenerSet` (wildcard TCP/UDP; TCP is the host path) and `BlockSink` when the
  blob contains block; publish the host blob and verify the readback (installed/rule count/fallback
  /size); publish `ProbeConfig.EnabledFor(pid, listenerPort, selfPids, blockSinkPort)` and verify;
  log ready (listener, block sink, rule count, ABI).
- **R5c2-3 Accept/relay.** Accept TCP connections; read the host redirect context; require TCP;
  resolve `TargetsByRuleIndex[ruleIndex]` (out-of-range/sentinel/missing/local = fail closed);
  enforce `TcpFlowCapacity`; bound concurrent relay establishment by `SetupWorkerCount`; establish
  the relay through the Batch 5c-1 raw-socket overload with the target `Socks5Server`; observe
  completion; on `Stalled`/`Faulted` set linger 0 before disposing; log/count the outcome. One
  owner per accepted socket; no leaks.
- **R5c2-4 Shutdown.** Disable the host config first, stop accepting, cancel/drain sessions, dispose
  the block sink after the driver is disabled, clear the forwarded blob, dispose the device. Return
  the same exit-code surface (0 clean, 1 device/config/ABI, 3 runtime); device faults are logged as
  Kernel backend errors, not as NDISAPI errors.
- **R5c2-5 Quality.** Linux build/test/format and the Kernel fast gate pass; the CLI project-level
  jb stays 0; the compiler `ContainsBlock` is tested; the Kernel backend's device path is
  code-reviewed but not claimed as E2E-tested (5d).
- **R5c2-6 Boundaries.** No WinpkFilter behavior change, no host UDP/forwarded, no driver/ABI
  changes, no unrelated dirty files. The empty forwarded blob uses the Contracts compiler, not a
  copied layout.

## Acceptance Criteria

- [x] R5c2-AC1: `Program` selects the Kernel backend for the Kernel provider and the WinpkFilter
      path/exit codes are unchanged; the provider guard still runs.
- [x] R5c2-AC2: the startup/shutdown order and resource ownership match the design; the driver is
      disabled before the listener/block sink are released; no double dispose.
- [x] R5c2-AC3: the accept/relay path resolves the rule-index target, fails closed on every mapping
      error, enforces capacity, and applies linger 0 on stall/fault.
- [x] R5c2-AC4: Linux build/test/format/Kernel fast/CLI jb pass; the Windows E2E is explicitly
      deferred to Batch 5d (no false claim).
- [x] R5c2-AC5: no unrelated changes.

## Out of Scope

- Windows VM E2E, host UDP, forwarded, ARM64, production packaging, full counters/parity (Batch 5d).