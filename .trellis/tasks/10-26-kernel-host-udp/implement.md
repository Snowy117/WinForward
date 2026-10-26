# Implementation plan: Kernel provider host UDP

Parent PRD: `./prd.md`; architecture: `./design.md`; research: `./research/`.

The umbrella task is not started as an implementation target. The two children below are the
implementation batches; this file fixes their order, gates and rollback points.

## Batch 1 — `10-26-host-udp-baseline` (SOCKS5 UDP proxy)

1. **Config gate.** In `ConfigurationKernelCapabilities`, remove the rejection of Kernel host proxy
   rules whose protocol can match UDP, and remove the “UDP settings are inert” warnings. Keep the
   adapter, forwarded, `KernelPassCache` and process-path gates. Update the configuration test
   matrix (the currently failing cases in `BackendDriverConfigurationTests.cs` around the UDP gate
   and warnings; UDP-capable proxy accepted; TCP-only unchanged; local target still rejected in this
   batch). Rewrite the stale “host UDP relay is not served” diagnostics in
   `KernelPolicyCompiler`/the TCP resolver and update the pinned test; the batch-1 local-target
   rejection stays explicit and actionable.
2. **cmsg parser.** Add `WfpUdpContext` (pure parser) and unit tests: level 0/41, type 70,
   36-byte payload, magic/version, family 4/6, protocol 17, short/long/malformed cmsgs, multiple
   context cmsgs. Tests run on Linux and do not open sockets.
3. **Host listener.** Add `HostListenerSet` (v4 TCP + v6 TCP V6ONLY + dual-mode v6 UDP on the same
   port; `IP_WFP_REDIRECT_CONTEXT` levels 0/41; `SIO_UDP_CONNRESET` disabled; bind retries). Add the
   Windows `WfpUdpReceiver` using `WSARecvMsg` (pooled payload/control buffers, source endpoint,
   cmsg payload, `WSAECONNRESET` skip, socket-close cancellation). The cmsg payload is decoded by
   `RedirectDestination.TryParse`, not a second ABI decoder.
4. **Host UDP service.** Add `KernelHostUdpService`: receive loop, context validation, client
   endpoint normalization (v4-mapped → IPv4 for family 4), **admission-time 4096 length check**,
   per-datagram exception/false-return containment, `FlowKey` construction, target resolution,
   `UdpProxyCoordinator.TrySendSpanAsync`, activity-clock tick (500 ms cadence) and idle sweep. Add
   `HostUdpResponseSink` sending via the receiving UDP socket and mapping an IPv4 `Flow.Local` back
   to `::ffff:` for the AF_INET6 listener. Add unit tests for normalization, target resolution, the
   sink endpoint and the drop paths.
5. **Composition.** Extend `UdpProxyComposer` with the core sink/composition overload; make the
   packet `Create` delegate without behavior change. Compose the host coordinator with the host cap
   (4096), the shared `SelfTrafficRegistry`, `Socks5AddressCache`, `SetupExecutor`, two
   `NativeBufferPool`s and an `ActivityBucketClock` in `KernelBackend`.
6. **Host lifecycle refactor.** Extract `KernelHostService` (ABI check, stale-state disable, compile,
   bind `HostListenerSet`/`BlockSink`, start UDP loop, publish/verify blob and config, start TCP
   accept loops, shutdown order). Reduce `KernelHostTcpService` to TCP accept/relay/capacity and
   pass it the bound listeners, target table and scope. Preserve the verified TCP start order:
   disable stale → bind → publish/verify → enable → accept. Shutdown: disable config → cancel →
   dispose the listener set (closes the UDP socket, unblocking `WSARecvMsg`) → await UDP join/
   coordinator dispose → await TCP drain → dispose block sink → clear forwarded; `ShutdownFaulted`
   moves to the orchestrator.
7. **Observability.** Ready/listener-bound/stop lines include UDP enabled/port/capacity; a
   rate-limited drop line with reasons; existing `RuntimeCounters` UDP counters plus
   `kernel.udp.*` accepted/dropped/active where useful. No full heartbeat section in this batch.
8. **Gates.** Root `dotnet format`/build/test, Kernel `check.sh --full`, Windows `gate.ps1` with
   `udp-rules` U1–U13 and U10. Then the Windows host UDP E2E A1–A5, A8, A10 (research
   `host-udp-seams.md` §6) with the test-signed driver, sing-box SOCKS5 and Linux UDP echo/pass/
   block targets. Prime the SOCKS5 address cache at startup. A1 sends 3 datagrams to separate the
   association count from the datagram count; A3 records the host stop line's `BlockSink.Received
   == 0`; A5's post-expiry client uses a **new source port**; A4 is asserted only on the
   unconnected arm. Use three labeled UDP target instances (40010/40011/40013) plus a TCP 40012
   target. Record the client source endpoint, sing-box relay, driver counters, capacity/idle,
   dual-mode/native v6 results and cleanup.
9. **Commit/archive batch 1** only after the E2E evidence is complete. If A4 (reply source) or A8
   (dual-mode delivery) fails, stop and revise the design; do not commit a partial workaround.

> **Status 2026-10-26:** the baseline listener/coordinator/CLI implementation is in the working
> tree and the Windows gate passes, but A4 failed: the client sees the listener source. Raw-socket
> source spoofing is not viable on Windows. The baseline is **not committed/accepted**. The
> `host-udp-reply-source` child must implement a driver-injected/source-rewritten reply and pass
> A4/A8; then the combined work is committed and this batch is re-verified.

## Batch 2 — `10-26-host-udp-local-uoT` (local target/DNS + UoT)

1. Remove the compiler's local-target rejection for Kernel host UDP; keep the TCP runtime rejection
   as defense. Add config/compiler tests: a `Protocol:["udp"]` local-target rule compiles; the TCP
   resolver rejects it; a UDP resolver accepts it.
2. Add the DNS-shaped E2E A6 with a local target and an EDNS0 response >512 bytes, asserting the
   reply source and the one-shot expiry. Add the UoT v2 E2E A7 with the sing-box transport evidence.
3. Record the connected-UDP leak acceptance/operations note and the `WfPr`/`Arcl` samples (A9);
   update the Kernel spec and README/ops docs.
4. Run the parent acceptance A1–A10 plus the Windows gate and root/Kernel gates; attach the evidence
   to the parent; commit/archive batch 2 and then the umbrella task.

## Validation commands

```bash
dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore
dotnet build WinForward.slnx -c Release
dotnet test WinForward.slnx -c Release
nix develop ./kernel --command bash -lc 'kernel/tools/check.sh --full'
# Windows VM
pwsh -File C:\workspace\WinForward\kernel\tools\windows\gate.ps1 `
  -Root C:\workspace\WinForward\kernel -Remote 192.168.100.4:40001
# Host UDP E2E: build/publish the CLI, start sing-box and the Linux UDP target, run the client
# arms, save the evidence under the batch verification/ and clean the VM.
```

## Risky files / rollback points

- `KernelHostTcpService` → `KernelHostService` refactor: the TCP startup/shutdown order is the
  rollback signal; re-run the TCP E2E and driver gate before proceeding to UDP.
- `UdpProxyComposer`: the packet overload must stay behavior-identical; keep the existing UDP tests
  and benchmark signals green.
- `ListenerSet`/`HostListenerSet`: do not change the forwarded bind semantics; the host listener is a
  new type.
- `ConfigurationKernelCapabilities`/`KernelPolicyCompiler`: the capability gates are product
  contracts; keep path-qualified diagnostics and the local-target/TCP defense.
- The UDP service's receive loop and sink: a failure here must not crash the process or leak sockets;
  stop/cleanup and revert to the disabled UDP gate.

## Review gates

- The PRD/design/implement are reviewed against the research documents and the current code.
- Each child has real `implement.jsonl`/`check.jsonl` entries (specs + research), a task start, and
  a verification record before commit.
- The umbrella is complete only after both children are archived and A1–A10 pass on the VM.
