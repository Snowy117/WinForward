# Implementation Plan

## 1. Configuration and Level Contract

- [x] Add `RuntimeLogLevel`, top-level `logLevel`, defaulting, normalization, and path-specific validation in `WinForward.Configuration`.
- [x] Carry the validated level and precomputed process-path logging need in `ValidatedConfiguration`.
- [x] Add tests for omitted/default, all five case-insensitive values, whitespace normalization, null/blank/unknown, and wrong JSON type.

Validation:

```bash
dotnet test tests/WinForward.Core.Tests/WinForward.Core.Tests.csproj -c Release --filter "FullyQualifiedName~Configuration"
```

## 2. Logger and Formatting

- [x] Extend `IRuntimeLogger`/`NullRuntimeLogger` with threshold checks and debug/trace support.
- [x] Move or expose the console logger as an independently testable component while retaining stderr output and existing prefixes.
- [x] Implement one stable event/key-value formatter with escaping, invariant values, endpoint formatting, and atomic line writes.
- [x] Add filtering and formatter tests, including hostile whitespace/control characters and absence of credentials/payload values.

Rollback point: logger contract and tests can be reverted without touching packet behavior.

## 3. Packet and Flow Correlation

- [x] Add capture-time packet sequence metadata to `CapturedFlowPacket` with low fixture churn.
- [x] Propagate `FlowState.Generation` after resolution/claim so executor and coordinator events retain one logical flow ID.
- [x] Inject the logger into capture processor and dispatcher through the existing CLI composition root.
- [x] Add trace tests for packet ID stability, flow correlation, terminal completion, and process-path privacy. Existing dispatcher tests retain self-traffic, reverse, capacity, and non-flow behavior coverage.

## 4. Decision and Final-Disposition Events

- [x] Instrument capture/classification, process attribution, existing/new decision resolution, self-traffic, reverse handling, action selection, and final completion/failure.
- [x] Ensure each normally handled trace packet has one terminal event and that logging cannot change `PacketLease` exactly-once completion.
- [x] Verify default `info` emits no per-packet records and avoids diagnostic formatting work.

## 5. TCP Diagnostic Coverage

- [x] Add debug lifecycle events for redirect claim/setup, relay establishment/completion, expiry, and teardown.
- [x] Add trace stage/outcome events for SYN classification, rewrite/reinjection, existing association reuse, reverse rewrite, and final handling outcome.
- [x] Include flow/association IDs, endpoints, server name, stage, and byte counts where useful; never include relay buffers or credentials.
- [ ] Extend coordinator tests for success, retransmit/reuse, reverse, blocked/failure, relay end, and expiry correlation.

## 6. UDP Diagnostic Coverage

- [x] Pass the logger through UDP composition and add debug lifecycle events for session creation/setup, reuse, receive failure, expiry, and teardown.
- [x] Add trace stage/outcome events for datagram parse, relay send/receive, response frame build, adapter target resolution, and reinjection.
- [x] Include flow/association IDs, endpoints, server name, stage, and byte counts only.
- [ ] Extend UDP coordinator/relay tests for send, response, setup failure, missing origin adapter, expiry, and disposal correlation.

## 7. Documentation and Examples

- [x] Document `logLevel`, accepted values, default, debug-vs-trace behavior, stderr single-line format, privacy boundary, and trace overhead in `README.md`.
- [x] Add an explicit `logLevel` to one representative example while keeping all examples valid.
- [x] Update project logging guidelines with the conventions learned from the implementation.

## 8. Full Verification

- [x] Search for direct sensitive-value logging and unguarded debug/trace formatting.
- [x] Run formatting/build analyzers and all tests; `git diff --check` is clean.
- [ ] Native AOT publish: blocked on Linux because the installed compiler does not support cross-OS `win-x64` native compilation.
- [x] Run Trellis quality review and resolve findings before completion.

```bash
dotnet build WinForward.slnx -c Release
dotnet test WinForward.slnx -c Release
dotnet publish src/WinForward.Cli/WinForward.Cli.csproj -c Release -r win-x64 --no-restore
rg -n --heading "Username|Password|Payload|Frame\.Span|GetFrame" src/WinForward.*
```

## Verification Notes

- Targeted configuration/logging tests: 84 passed.
- Full Release build: passed with 0 warnings and 0 errors.
- Full Release test suite: 265 passed, 0 failed, 0 skipped.
- Capture classification/dispatch failures emit a single trace `packet.failed` record when possible;
  UDP response reinjection emits trace drop/reinjection metadata without payloads.
- All `examples/*.json` files parse successfully.
- Windows Native AOT publish was attempted and failed only because this Linux host cannot cross-compile the Windows native binary; run it on Windows or a Windows build agent.

## Risk and Review Gates

- High-frequency files: `CapturePacketProcessor.cs`, `FlowDispatcher.cs`, and `NdisPacketActionExecutor.cs`. Review default-level allocation behavior before broad instrumentation.
- Cross-layer contract: configuration enum/default must agree with CLI filtering and README values.
- Concurrency: verify whole-line output from simultaneous capture and relay tasks.
- Behavior: packet dispositions, policy evaluation count, relay setup, and fail-closed outcomes must remain unchanged in existing tests.
- Privacy: tests must prove no raw bytes, username, password, or unnecessary process path reaches emitted records.
