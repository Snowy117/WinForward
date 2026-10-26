# Implementation plan: host UDP baseline

Parent: `../10-26-kernel-host-udp/README` artifacts; this batch implements the SOCKS5 UDP
baseline. Do not implement local targets, DNS/EDNS0 or UoT here.

## Ordered checklist

1. **Read the seams.** `UdpProxyCoordinator`/`UdpSessionSetup`/`UdpProxySession`/`UdpProxyOptions`/
   `UdpTransportContracts`, `UdpProxyComposer`, `KernelHostTcpService`, `KernelBackend`,
   `ConfigurationKernelCapabilities`, `KernelPolicyCompiler`, `ListenerSet`, `RedirectDestination`,
   `FlowKey`/`Endpoint`, and the UDP specs (`udp-relay.md`, `udp-flow-setup.md`,
   `udp-session-lifecycle.md`, `udp-relay-transport.md`, `idle-expiry-sweep.md`,
   `async-lifetime.md`, `packet-shape-and-flow-identity.md`).
2. **Config gate.** Remove the Kernel UDP-proxy rejection and the inert UDP-setting warnings. Keep
   the adapter/forwarded/path gates and the local-target rejection. Update the named
   `BackendDriverConfigurationTests` cases for the removed gate/warnings (UDP proxy accepted,
   TCP-only unchanged, local target still rejected). Rewrite the stale “host UDP relay is not
   served” diagnostics in the compiler/TCP resolver and update the pinned
   `KernelPolicyCompilerTests` case.
3. **cmsg parser.** Add `WfpUdpContext` (pure): `TryParseControl` for the cmsg chain (type 70,
   level 0/41, alignment, bounds) extracts the 36-byte payload; decode it with the existing
   `RedirectDestination.TryParse` (no second ABI decoder). Unit test the chain extraction and the
   `RedirectDestination` decode on Linux; no sockets. Add a direct
   `WinForward.Kernel.Runtime` project reference to `WinForward.Integration.Tests`.
4. **Listener/receiver.** Add `HostListenerSet` (v4 TCP + v6 TCP V6ONLY + dual-mode v6 UDP, WFP
   redirect-context option, `SIO_UDP_CONNRESET` disabled, port retries) and `WfpUdpReceiver`
   (`WSARecvMsg`, pooled payload/control buffers, source endpoint + cmsg payload,
   `WSAECONNRESET` skip, socket-close cancellation). These are Windows-only; compile them and
   leave the runtime proof to the E2E.
5. **Sink.** Add `HostUdpResponseSink : IUdpResponseSink`; send via the receiving UDP socket and map
   an IPv4 `flow.Local` to `::ffff:` for the AF_INET6 listener; ignore the source/MAC; count/log a
   send failure. Unit test the endpoint mapping and a loopback send on Linux.
6. **UDP service.** Add `KernelHostUdpService`: validation, client-endpoint normalization
   (v4-mapped → IPv4 for family 4), **pre-admission 4096 length check**, per-datagram exception/false
   containment, `FlowKey` + target resolution (SOCKS5 only in this batch), `TrySendSpanAsync`,
   activity-clock tick (500 ms cadence)/idle sweep, counters, dispose. Unit test normalization,
   flow/target resolution, over-cap/no-context drops and loop survival.
7. **Composition.** Add the `UdpProxyComposer` core overload and make the packet `Create` delegate;
   keep the packet behavior identical. Add a smoke test that both paths construct the coordinator
   with the expected options. Add the host pools (4096 cap) and `ActivityBucketClock` in
   `KernelBackend`.
8. **Host lifecycle.** Extract `KernelHostService` (ABI/disable/compile/bind/start UDP/publish/
   config/start TCP/ready/shutdown) and reduce `KernelHostTcpService` to TCP accept/relay/capacity.
   Preserve the 5f start order; shutdown is disable → cancel → dispose the listener set (closes the
   UDP socket) → await UDP join/coordinator dispose → await TCP drain → dispose block sink → clear
   forwarded. `ShutdownFaulted` moves to the orchestrator and `KernelBackend` reads it there.
   Re-run the TCP E2E after the refactor.
9. **Observability.** Ready/listener/stop logs mention UDP enabled/port/capacity; add a rate-limited
   UDP drop line and a stop line with `BlockSink.Received` (A3's zero-sink evidence); use the
   existing UDP `RuntimeCounters` and the `kernel.udp.*` counters. No full heartbeat.
10. **Linux/root gates.** `dotnet format`, `dotnet build -c Release`, `dotnet test -c Release`,
    `nix develop ./kernel --command bash -lc 'kernel/tools/check.sh --full'`.
11. **Windows driver gate.** Build/sign/install the 5f driver through `gate.ps1` (no driver source
    change); confirm `rules`, `udp-rules` U1–U13/U10, `selftest` and `userland-e2e` remain green.
    Record the installed SHA-256.
12. **Windows host UDP E2E.** Publish the current CLI (`win-x64`, framework-dependent), prime the
    SOCKS5 address cache at startup, start sing-box and the CLI via scheduled tasks (SYSTEM), use the
    config below, and run A1–A5/A8/A10. Run three labeled Linux UDP target instances
    (40010/40011/40013; the 40013 instance logs any datagram so a block failure is visible) and a
    TCP 40012 target. A1 sends three datagrams and asserts one association-classify increment; its
    unconnected `ReceiveFrom.RemoteEndPoint` must equal `192.168.100.4:40010` (A4; the connected arm
    checks payload only). A3 records the host stop line's `BlockSink.Received == 0`. A8 covers
    native v4, v4-mapped connected and native v6 (local `::1:40010` echo if the Linux target has no
    IPv6 route). A10 sends a context-free datagram to the listener port and verifies a drop/counter
    while the service survives.
13. **Capacity/idle.** Restart the CLI with `UdpSessionCapacity=1`, `UdpSessionIdleSeconds=5`; two
    concurrent clients with different source ports — only the first gets a reply; after expiry a
    **new source-port** client gets a reply and reclassifies; record the target log and driver
    counters. Restore the normal config.
14. **Evidence/cleanup.** Save clients, driver stats/events, sing-box/target/wf logs and cleanup
    under `verification/`; stop tasks/processes, zero the driver config, delete scheduled tasks,
    stop the Linux targets, verify the VM network. Commit only after the evidence is complete;
    archive the task.

> **Status 2026-10-26 (deferred):** steps 1–13 are implemented and preserved, but A4 failed
> (`verification/a4-failure.md`): the listener reply source is the listener, not the original
> destination. The batch closes with the host UDP proxy disabled — `ConfigurationKernelCapabilities`
> refuses UDP-capable proxy rules (reply-source reason + `Provider = WinpkFilter`), the non-default UDP
> budgets warn as inert, `KernelHostService.HostUdpProxyEnabled` is false with a TODO and the receive
> leg never starts (the service/coordinator stay constructed), the listener/ready/started lines say
> the proxy is disabled, and README/spec record the deferral. The working tree is **not committed**.
> The `10-26-host-udp-reply-source` child owns the driver-side reply injection and the A1–A5/A8/A10
> rerun; when it passes, the gate lifts, `HostUdpProxyEnabled` flips, and this batch's code is
> committed with it. See `verification/deferred.md`.

## E2E configuration

```json
{
  "WinForward": {
    "BackendDriver": { "Provider": "WinForward.Kernel", "WinForwardKernel": { "DriverServiceName": "wfprobe" } },
    "ProxyUnavailableAction": "block",
    "ProcessingFailureAction": "block",
    "Socks5Servers": [ { "Name": "main", "Host": "127.0.0.1", "Port": 1080 } ],
    "Host": {
      "FallbackAction": "pass",
      "Rules": [
        { "Process": ["sing-box.exe"], "Action": "pass" },
        { "Protocol": ["udp"], "RemotePort": ["40013"], "Action": "block" },
        { "Protocol": ["udp"], "RemotePort": ["40011"], "Action": "pass" },
        { "Protocol": ["udp"], "RemotePort": ["40010"], "Action": "proxy", "Target": "main" },
        { "Process": ["pwsh.exe"], "Protocol": ["tcp"], "RemoteCidr": ["192.168.100.4/32"], "RemotePort": ["40012"], "Action": "proxy", "Target": "main" }
      ]
    }
  }
}
```

The TCP rule revalidates the 5f path after the host-service refactor. The sing-box pass rule is
first so its own TCP/UDP upstream traffic is never redirected; the CLI self-PID set covers the UDP
transports. The listener/block ports come from the CLI startup log.

## Validation commands

```bash
dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore
dotnet build WinForward.slnx -c Release
dotnet test WinForward.slnx -c Release
nix develop ./kernel --command bash -lc 'kernel/tools/check.sh --full'
# Windows: gate.ps1, then the host UDP E2E
```

## Risks / rollback

- A4 (reply source) or A8 (dual-mode) failing stops the batch; do not commit a workaround.
- The host-service refactor must not regress TCP; the 5f TCP client is part of the E2E.
- The receive loop must not allocate per datagram or die on `WSAECONNRESET`.
- The no-context listener path must drop without a session/crash.
- If the UDP path cannot meet the acceptance, revert the new files and keep the UDP capability
  gate; the TCP Kernel and WinpkFilter paths are the rollback point.
- Do not touch the pre-existing dirty paths (`.trellis/tasks/08-30-...`,
  `.trellis/tasks/10-10-...`, `benchmarks/results/...`). Do not commit before the E2E evidence.
