# Research: host UDP reply-source design (injection, ABI, tests)

- **Query**: What is the smallest safe driver/userland design that delivers a relayed host UDP reply
  with the original destination as source, and how is it versioned, validated and tested?
- **Scope**: internal (driver, contracts, CLI, gate scripts) + external API constraints from
  `wfp-reply-source.md`
- **Date**: 2026-10-26

---

## 0. Recommendation

**Mechanism: driver-side transport-receive injection** (the documented `ddproxy` pattern):

- the userland sink hands the driver `client endpoint + original destination + payload`;
- the driver builds one IPv4/IPv6 UDP packet with `source = original destination`,
  `destination = client endpoint`, and calls `FwpsConstructIpHeaderForTransportPacket0` +
  `FwpsInjectTransportReceiveAsync0`;
- the listener stops sending replies itself (no duplicate, no listener-source packet);
- failures are counted and dropped (no fallback to `sendto`, which would silently violate RS-AC1).

Why not the alternatives:

- **Source rewrite** is measured impossible on the packet path: the listener reply is local-to-local
  and invisible at `DATAGRAM_DATA` and `INBOUND_IPPACKET`
  (`route-a-findings.md:41-87`). A pre-ALE rewrite (`INBOUND_TRANSPORT`) would face the same
  firewall question and still requires drop-and-reinject.
- **Redirect records / return mapping** are TCP-only (`SIO_SET/QUERY_WFP_CONNECTION_REDIRECT_RECORDS`).
- **Userland raw sockets** are measured dead (A4).

**Two blocking experiments come before any product code:**

1. **A8-connected against the baseline (no code change).** Run the existing listener `sendto` reply
   against a connected client and print `socket.RemoteEndPoint` after `Connect()` plus the received
   `from=`.
   - Received ⇒ the connected peer is the listener ⇒ injection cannot serve connected clients
     (section 4). The mode-aware branch below is required.
   - Timeout ⇒ the connected peer stayed the original destination ⇒ injection serves both forms and
     the design is the simple one (section 4.1).
2. **Injection prototype behind `wfprobe`** (driver command only, no CLI wiring): one unconnected
   client bound to `192.168.100.2`, one `wfprobe host-reply`, firewall on and off. Go/no-go:
   - delivered with the firewall on ⇒ proceed;
   - blocked ⇒ do not build the sink; escalate (§8 F1/F2, §9 questions).

---

## 1. Packet construction

```
storage = ExAllocatePool2(POOL_FLAG_NON_PAGED, backfill + 8 + payload_length, 'jUfW')
          backfill = 20 (family 4) / 40 (family 6)
          UDP header at storage + backfill: src = original_port, dst = client_port,
          length = 8 + payload_length, checksum = 0
          payload at storage + backfill + 8
mdl     = NdisAllocateMdl(pool, storage, backfill + 8 + payload_length)
nbl     = FwpsAllocateNetBufferAndNetBufferList0(pool, 0, 0, mdl,
                                                 backfill, 8 + payload_length, &nbl)
FwpsConstructIpHeaderForTransportPacket0(nbl, 0, family,
                                         source = original_address,
                                         remote = client_address,
                                         IPPROTO_UDP, 0, nullptr, 0,
                                         /*flags*/ 0, nullptr, 0, 0)
FwpsInjectTransportReceiveAsync0(handle, nullptr, nullptr, 0, family,
                                 UNSPECIFIED_COMPARTMENT_ID, 1, 0,
                                 nbl, completion, nullptr)
completion: FwpsFreeNetBufferList0(nbl); NdisFreeMdl(mdl); ExFreePoolWithTag(storage, 'jUfW');
            InterlockedDecrement64(&outstanding);
```

Notes, each of which is a known failure mode:

- **Backfill.** `FwpsConstructIpHeaderForTransportPacket0` writes the IP header in front of the
  transport header; `dataOffset` must be the exact IP header size (20/40). A constructed NBL with
  `dataOffset = 0` has nowhere to put the header.
- **MDL.** `NdisAllocateMdl(pool, ...)` — `IoAllocateMdl` is measured to produce an MDL the inbound
  stack drops (`probe_forwarded_frame.cpp:456-464`).
- **Checksums** are computed by the construct call; do not hand-roll them
  (`probe_forwarded_checksum.h` stays unused on this path).
- **Interface index.** `0` is rejected; `1` (loopback) passes validation (10-21). The destination is
  local, so loopback is the defensible arrival interface. Carrying a per-request ifindex is the
  fallback if `1` is refused.
- **Compartment** `UNSPECIFIED_COMPARTMENT_ID`; no per-request compartment.
- **IRQL.** Called from the IOCTL dispatch (PASSIVE). Documented IRQL is `<= DISPATCH_LEVEL`, and
  the first-party sample calls it from a system worker at PASSIVE. If the prototype shows a failure
  attributable to the caller context, queue the same call on a work item; the packet construction is
  context-independent.
- **Completion at DISPATCH**: only the three non-paged frees, copied from
  `probe_forwarded_frame.cpp:199-241`.

---

## 2. Driver changes

### 2.1 New translation unit

`kernel/src/driver/probe_host_reply.h` / `probe_host_reply.cpp`

- `struct host_reply_request { uint8_t family; uint8_t client_address[16]; uint16_t client_port;
  uint8_t original_address[16]; uint16_t original_port; const uint8_t* payload;
  uint32_t payload_length; uint32_t rule_index; uint32_t process_id; };`
- `[[nodiscard]] bool validate_host_reply(const void* buffer, uint32_t length,
  host_reply_request& request) noexcept;` — WDK-free, in the header, so a gtest TU can include it
  (same parse/inject split as `probe_forwarded_frame`).
- `[[nodiscard]] NTSTATUS probe_host_reply_inject(const host_reply_request&) noexcept;` — the
  builder + injector, a sibling of `probe_forwarded_inject_frame` but transport-level.
- `NTSTATUS probe_host_reply_apply(const void* buffer, uint32_t input_length) noexcept;` — the IOCTL
  entry: validate, copy the payload, count, call the injector.
- `void probe_host_reply_stop() noexcept;` — drain + destroy handle/pool (see 2.4).

### 2.2 Control ABI (`kernel/src/driver/probe_ioctl.h`)

ABI `probe_abi_version` 8 → **9** (`probe_ioctl.h:16`).

```cpp
inline constexpr std::uint32_t host_udp_reply_magic = 0x31525557U; // memory "WUR1"
inline constexpr std::uint16_t host_udp_reply_version = 1;
inline constexpr std::uint32_t host_udp_reply_max_payload = 4096;  // matches the CLI cap

struct probe_host_udp_reply
{
    std::uint32_t magic;
    std::uint16_t version;
    std::uint8_t  family;            // 4 / 6
    std::uint8_t  reserved0;         // 0
    std::uint8_t  client_address[16];// destination of the injected packet
    std::uint16_t client_port;
    std::uint16_t original_port;     // source port of the injected packet
    std::uint8_t  original_address[16];
    std::uint32_t payload_length;
    std::uint32_t rule_index;        // diagnostic; 0xFFFFFFFF when unknown
    std::uint32_t process_id;        // must equal the caller and the configured listener
    std::uint32_t reserved1;         // 0
};
static_assert(sizeof(probe_host_udp_reply) == 60, "...");
// offsets 0,4,6,7,8,24,26,28,44,48,52,56
inline constexpr std::uint32_t ioctl_inject_host_udp_reply = make_ioctl(0x811);
```

Layout: `magic 0`, `version 4`, `family 6`, `reserved0 7`, `client_address 8`, `client_port 24`,
`original_port 26`, `original_address 28`, `payload_length 44`, `rule_index 48`,
`process_id 52`, `reserved1 56`; payload starts at 60. IPv4 addresses use the first four bytes and
the remaining twelve must be zero (strict, to avoid two encodings of one address).

Validation (all failures count `udp_reply_rejected` and return `STATUS_INVALID_PARAMETER`):

1. `buffer != nullptr`, `length >= 60`, `length <= 60 + 4096`.
2. magic/version; `reserved0 == 0`, `reserved1 == 0`.
3. `family` is 4 or 6; the address families implied by the stored endpoints are consistent.
4. `client_port != 0`, `original_port != 0`.
5. `payload_length == length - 60` (no truncation, no slack) and `<= 4096`.
6. reserved bytes of each IPv4 address are zero.
7. `process_id == PsGetCurrentProcessId()` and the snapshot `config.listener_process_id` equals it;
   `config.enabled != 0` and `config.listener_port != 0`. Copy `config` under the existing spin lock
   (`probe_lock`, `probe_driver.cpp:355-381`).

The request is not checked against a stored association. Rationale and the optional hardening are in
section 5.

### 2.3 Statistics

Append five `uint64_t` plus one `uint32_t` + reserved to `probe_stats` (`probe_ioctl.h:294-338`):

| field | meaning |
|---|---|
| `udp_reply_calls` | requests that passed validation |
| `udp_reply_injected` | injections the API accepted (queued) |
| `udp_reply_rejected` | malformed/unauthorized/not-enabled requests |
| `udp_reply_failed` | synchronous injection API failure |
| `udp_reply_complete_failed` | completion `NBL->Status != NDIS_STATUS_SUCCESS` |
| `udp_reply_outstanding` | `u32` snapshot of queued-but-not-completed injections |
| `reserved` | `u32`, zero |

`sizeof(probe_stats)` 264 → **312**. Existing field offsets are unchanged; `last_status`/`abi_version`
stay at 256/260. `get_stats` (`probe_control.cpp:132-143`) must snapshot the outstanding counter at
read time, like `get_forwarded_stats` does.

Alternative considered: a separate `probe_host_reply_stats` IOCTL. Rejected because the host path's
counters already live in `probe_stats` and a second readout doubles the CLI plumbing for six values.

### 2.4 Lifecycle

- In `probe_driver.cpp` `start_wfp()` (after `FwpsRedirectHandleCreate0`, `:289`):
  `FwpsInjectionHandleCreate0(AF_UNSPEC, FWPS_INJECTION_TYPE_TRANSPORT, &state.host_reply_handle)`
  and `NdisAllocateNetBufferListPool` with tag `'jUfW'` (same parameters as
  `probe_forwarded.cpp:243-250`).
- In `stop_wfp()` (before `FwpmEngineClose0`): delete the handle
  (`FwpsInjectionHandleDestroy0` waits for outstanding injections), then drain explicitly (reuse the
  forwarded drain with a counter parameter or duplicate the four-line loop,
  `probe_forwarded_frame.cpp:513-526`), then free the pool only when outstanding is zero; otherwise
  leak the pool with a `DbgPrint` warning (`finalize_forwarded_injection_pool` precedent,
  `probe_forwarded_state.h:261-274`).
- No callout/filter objects: nothing to register or unregister, so no notify/classify work and no
  layered teardown ordering beyond the handle.
- `probe_driver_unload` already holds `rule_mutex`; injection has no rule-table reference.

### 2.5 Control dispatch

`probe_control.cpp:307-450`: add

```cpp
case ioctl_inject_host_udp_reply:
    status = probe_host_reply_apply(buffer, input_length);
    break;
```

`dispatch_device_control` already enforces `caller_is_admin()` at PASSIVE (`:292-298`) and the device
DACL restricts open to SYSTEM/Administrators (`probe_driver.cpp:34-45`). No new access check beyond
the PID/config gate.

---

## 3. Userland changes

### 3.1 Contracts

| File | Change |
|---|---|
| `src/WinForward.Kernel.Contracts/Device/ProbeIoctl.cs` | `AbiVersion = 9`; `InjectHostUdpReplyFunction = 0x811` + `Code`; `HostUdpReplyMaxPayload = 4096`. |
| `src/WinForward.Kernel.Contracts/Device/ProbeHostUdpReply.cs` (new) | Blittable 60-byte header mirror + magic/version constants. |
| `src/WinForward.Kernel.Contracts/Device/ProbeStats.cs` | Append the five counters and the outstanding/reserved pair; doc size 312. |
| `src/WinForward.Kernel.Contracts/Device/DeviceLayout.cs` | `ProbeStatsSize = 312` + offsets; `ProbeHostUdpReplySize = 60` + offsets; `VerifyProbeStats` additions; `VerifyHostUdpReply`. |
| `src/WinForward.Kernel.Contracts/Device/WfpDeviceClient.cs` | `SendHostUdpReply(IPEndPoint client, IPEndPoint originalDestination, uint ruleIndex, ReadOnlySpan<byte> payload)`: one `byte[]` of `60 + payload.Length`, header via `MemoryMarshal.Write`, payload copy, `Ioctl(..., input, 0)`. Validation: payload `0..4096` (a zero-length UDP datagram is legal and must pass unchanged), families equal, IPv4 storage rules. |
| `kernel/tools/wfprobe/Protocol.cs` | `AbiVersion = 9`; IOCTL constant; `ProbeHostUdpReply` struct; `ProbeStats` fields; `ProtocolLayout.Check<ProbeHostUdpReply>(60, ...)` and the updated `ProbeStats` size. |
| `kernel/src/userland/WinForward.Userland.Tests/LayoutTests.cs` | `AbiVersionEight` → nine; new layout case. |

`AbiConsistencyTests` (`kernel/src/userland/WinForward.Userland.Tests/AbiConsistencyTests.cs`) walks
every `ioctl_*` constant in the header and compares the product/fixture constants, and every listed
struct's size/field layout. Adding the header struct + IOCTL + product struct + fixture constants and
adding `probe_host_udp_reply` to `ProbeStructNames`/`FixtureStructNames` is what keeps the three
copies locked. `probe_stats`'s new size must be reflected in `DeviceLayout.ProbeStatsSize` and the
fixture check.

### 3.2 Reply sink

`src/WinForward.Cli/Backends/Kernel/HostUdpResponseSink.cs` becomes:

```csharp
internal sealed class HostUdpResponseSink(IHostUdpReplyInjector injector, ILogger logger)
    : IUdpResponseSink
{
    public ValueTask InjectAsync(FlowKey originalFlow, Endpoint remoteSource,
                                 ReadOnlyMemory<byte> payload, MacAddress clientMac, CancellationToken ct)
    {
        // client = originalFlow.Local, original = remoteSource (the relay's decoded source)
        // failure -> kernel.udp.responseFailed + rate-limited log, never throw
    }
}
```

- `remoteSource` is the decoded relay response source; it is normally equal to `flow.Remote` and
  is the value the packet edition uses as the frame source even when the relay reports a foreign
  source (`UdpProxySession.cs:437` passes it, `UdpResponseReinjector.cs:139-149` uses it).
- `clientMac` is ignored, as today.
- Payload `> 4096` is already excluded before admission (`KernelHostUdpDatagram.MaximumDatagramBytes`);
  the sink still guards, counts and drops.
- Introduce a narrow `IHostUdpReplyInjector` (platform-neutral, in `WinForward.Kernel.Runtime` or
  `WinForward.Cli/Backends/Kernel`) implemented by `WfpDeviceClient`, so the Linux test project can
  assert the exact request bytes with a fake. Do not make the sink depend on `WfpDeviceClient`
  directly. `tests/WinForward.Integration.Tests` already references both the CLI and
  `WinForward.Kernel.Runtime` (`WinForward.Integration.Tests.csproj:11,15`), so no new project
  reference is needed.

### 3.3 Composition

- `KernelHostService` already owns `_device`; pass it into `KernelHostUdpService`
  (`KernelHostService.cs:144`) and into the sink construction
  (`KernelHostUdpService.cs:94-105`).
- `KernelHostUdpService` no longer needs the `udpSocket` for replies; it still needs it for the
  receiver. Its constructor signature gains the injector.
- `KernelHostUdpComposition` does not need a change if the device is passed explicitly; keep the
  composition record for the borrowed pools/clock.

### 3.4 Counter/log

Keep `kernel.udp.responseFailed` for inject failures; add `kernel.udp.responseRejected` if the device
returns `STATUS_INVALID_PARAMETER`/`STATUS_NOT_FOUND`, so A4 can distinguish
"relay produced a reply" from "driver refused it". Reuse `UdpProxyLog.UdpInjectionFailed` with the
Win32 error text.

---

## 4. Reply mode: unconnected vs connected

### 4.1 If A8-connected is not received (peer stays the original)

Injection serves both forms. The sink is unconditional; no mode. This is the clean branch and the
one to hope for.

### 4.2 If A8-connected is received (peer = the listener, expected)

Evidence and mechanism are in `wfp-reply-source.md` §2.4. A connected client drops an
original-sourced datagram; only a listener-sourced one is accepted. The bit is not observable:

- no connected flag at `ALE_CONNECT_REDIRECT` (conditions/metadata lists),
- no member in `FWPS_CONNECT_REQUEST0`,
- the Windows UDP endpoint table has no remote field (measured: connected v4 and connected dual-mode
  sockets both show `remote=:`).

Therefore the reply mode must be an explicit product decision, not a runtime heuristic. Options, in
order of preference:

1. **Per-target configuration** (`UdpReplySource: original | listener`, default `original`) resolved
   by the same rule-index → target table the UDP service already uses. Smallest semantic surface
   that still lets an operator keep a connected app working. Needs a config model/DTO/validation
   change and must be mirrored in the packet edition's documentation if the setting is shared.
2. **Global Kernel-backend setting**, same values. Cheapest, but all-or-nothing.
3. **Documented limitation**: always inject, connected UDP apps see no replies. Only acceptable if
   the user decides connected UDP is out of scope.

Do not choose this silently: it changes the product contract for connected UDP applications and
contradicts the baseline's current (accepted-but-wrong-source) behaviour. Raise it as a decision.

---

## 5. Association table: optional

The request is authorized by device DACL + admin + "caller is the configured listener process".
Without a table the residual primitive is: an admin process (already able to load a kernel driver)
can inject a UDP datagram with an arbitrary source to any local endpoint. The PRD's RS-AC3 asks for
malformed/oversized/unknown-family rejection, which the header validation covers.

A host UDP association table would add:

- insert at `redirect_classification` when `protocol == UDP` and the redirect applied
  (`probe_classify.cpp:226-229`), keyed by (family, client addr/port, original addr/port), storing
  rule index, PID, generation;
- request validation against it, so only destinations the driver itself redirected are accepted;
- an idle sweep and a config-disable clear.

Cost: a new bounded table (~60 B/entry × capacity), a sweep path, and tests. It does **not** solve
the connected-mode problem and adds a second lifetime to reason about. Recommendation: **phase 1
without the table**; add it only if the acceptance requires "reject injection after the association
is gone" (which is unobservable for UDP anyway — "UDP connection termination is not indicated at any
WFP layer").

---

## 6. Tests and gates

### 6.1 Driver C++ (host, Linux gtest)

`kernel/tests/host/host_reply_test.cpp` (add to `kernel/tests/host/CMakeLists.txt`):

- validation: magic/version, family 4/6, zero-port, payload length mismatch/slack, >4096,
  IPv4 tail bytes non-zero, reserved non-zero, short input (0/59 bytes), exact 60+0;
- v4/v6 address extraction and endianness (host byte order, same convention as
  `redirect_context`);
- size/offset `static_assert`s already enforce the ABI at compile time.

No WDK dependency in the validation half; the inject half is not unit-testable on Linux.

### 6.2 Linux userland tests

- `HostUdpResponseSink` with a fake `IHostUdpReplyInjector`: exact client/original endpoints and
  payload bytes; IPv4 vs IPv6; a `remoteSource` that differs from `flow.Remote` uses `remoteSource`;
  a thrown `Win32Exception` increments `kernel.udp.responseFailed` and does not throw.
- `WfpDeviceClient` buffer construction can be tested through the injector seam without a device.
- Existing coordinator/session tests must stay green (the sink is the only changed collaborator).

### 6.3 `wfprobe` command (the smallest source proof)

```
wfprobe host-reply --client 192.168.100.2:PORT --original 192.168.100.4:40010 \
                   [--payload TEXT] [--family 4|6] [--rule-index -1] [--pid N]
wfprobe host-reply --negative magic|version|family|length|pid|disabled   # expects rejection
```

Pairing: `wfprobe udp-client --bind 192.168.100.2 --mode unconnected --remote <original>` in one
process (prints `from=`), `wfprobe host-reply` in another. Assertions:

- `RECV <n>B from=192.168.100.4:40010` (source preserved);
- `udp_reply_calls/injected +1`, `udp_reply_failed/complete_failed +0`, `outstanding` returns to 0;
- `pooltag WfUj` returns to its pre-test outstanding count;
- negative cases grow `udp_reply_rejected` and leave the listener alive.

Also run the same pair with `--family 6` (`::1` client if the destination has no v6 route) and with
the firewall on/off to isolate ALE (§0 experiment 2).

### 6.4 E2E (CLI)

- **A4**: unconnected IPv4; `ReceiveFrom.RemoteEndPoint == 192.168.100.4:40010`.
- **A8**: native IPv4, v4-mapped connected, native IPv6; payload-only for the connected arm (per the
  parent design), plus `RemoteEndPoint`/`from=` printed for evidence.
- **Firewall**: normal (enabled) state for all acceptance runs; one diagnostic run with the firewall
  off to attribute failures to ALE, recorded but not accepted.
- **A9**: unconnected `WfPr` returns to baseline; `WfUj` returns to 0 after stop.
- **A10**: no-context datagram still drops; injection of an unrelated endpoint fails closed.
- Regressions: TCP Kernel E2E, WinpkFilter path, the existing UDP baseline suites.

### 6.5 Gates

- `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` (empty output).
- `dotnet build/test -c Release` zero-warning / green.
- `jb inspectcode` zero issues.
- `kernel/tools/check.sh --full` (clang-format, clang-tidy/cppcheck, host gtest, wfprobe layout).
- Windows `kernel/tools/windows/gate.ps1` (driver load, config/rule suites, new `host-reply` cases);
  PREfast and CodeQL per the existing scripts. New IOCTL buffer handling must use SAL annotations,
  integer-safe length math, and no pointer from the IOCTL buffer escaping the dispatch.
- Mutation: if the new validation contains a mutation-marked branch (mirroring
  `<mutation id="invalid-blob-accepted">`), add the corresponding mutation so the gate proves the
  rejection is tested.

---

## 7. Risks

| Risk | Mitigation |
|---|---|
| **Firewall/ALE drops the injected datagram** | §0 experiment 2 before product code. If blocked: F1/F2 below or escalate. |
| **Connected clients drop an original-sourced reply** | §4; A8-connected baseline experiment decides; config or documented limitation. |
| Loopback-bound client (`127.0.0.1`) | 10-21 `STATUS_NOT_ACCEPTED`; count and drop; document. A4/A8 clients bind `0.0.0.0`. |
| Interface index unknown for fabricated packets | prototype 1 first; add a request ifindex or a per-address lookup if 1 fails. |
| Completion at DISPATCH | non-paged frees only; copy `probe_forwarded_frame.cpp:199-241`. |
| Double free / UAF | three-resource ownership; free in the caller only on synchronous failure, in the completion on success; the drain keeps the pool alive while outstanding > 0. |
| Inject after shutdown | config disabled first in `KernelHostService.DisposeAsync`; handle destroy waits; device deleted last. |
| ABI drift | header is authoritative; `AbiConsistencyTests` forces the three copies; lockstep bump in one change. |
| `probe_stats` growth breaks old readers | accepted (RS-D3 lockstep); documented in the header comment. |
| Security: arbitrary-source injection primitive | admin-only device + caller-is-listener PID gate; optional association table (§5). |
| Pool tag accounting | new tag `'jUfW'` (memory `WfUj`), distinct from `WfPr`/`WfT`/`WfIj`; assert in E2E. |

---

## 8. Fallbacks if injection is refused or blocked

- **F1 — scoped ALE permit.** Register a callout/filter at `ALE_AUTH_RECV_ACCEPT_V4/V6` permitting
  exactly (family, client endpoint, original destination) for the association lifetime. A permit from
  `FWPM_SUBLAYER_UNIVERSAL` cannot override the firewall (`probe_driver.cpp:253-257`); it would need a
  higher-weight sublayer, i.e. a deliberate, auditable firewall bypass. This is a user-level security
  decision, not an implementation detail.
- **F2 — post-ALE in-place rewrite.** If `DATAGRAM_DATA` inbound can see an injected copy (it cannot
  see the listener's local-to-local reply), clone-modify-reinject is the sample pattern; it re-enters
  ALE and hits the same question, so it is not an escape from F1.
- **F3 — accept the listener source.** Keep the baseline and re-scope RS-AC1. This abandons the task.

---

## 9. Questions that need the user

1. **Connected UDP.** After the A8-connected experiment: may connected clients keep the listener
   source (payload-only acceptance), or must connected proxying be dropped/failed closed? If they
   must work with the original source, the local-redirect architecture cannot deliver it and the
   alternative is a per-datagram rewrite design (new entry path, not this batch).
2. **Firewall.** If injection is blocked with the firewall enabled, is a scoped ALE permit (F1) an
   acceptable product behaviour, or should the UDP capability stay gated?
3. **Reply-mode setting.** If connected clients must keep working via the listener path, is a
   per-target `UdpReplySource` setting acceptable in the shared configuration?

---

## 10. Files touched (checklist)

Driver: `probe_ioctl.h`, `probe_driver.h`, `probe_driver.cpp`, `probe_control.cpp`,
`probe_host_reply.h` (new), `probe_host_reply.cpp` (new), `kernel/tests/host/CMakeLists.txt`,
`kernel/tests/host/host_reply_test.cpp` (new).

Contracts/CLI: `ProbeIoctl.cs`, `ProbeHostUdpReply.cs` (new), `ProbeStats.cs`, `DeviceLayout.cs`,
`WfpDeviceClient.cs`, `HostUdpResponseSink.cs`, `KernelHostUdpService.cs`, `KernelHostService.cs`,
plus the injector seam and its Linux tests.

Tooling: `kernel/tools/wfprobe/Protocol.cs`, `Program.cs`, a new `HostReply.cs`, and the gate
scripts that run the new command.
