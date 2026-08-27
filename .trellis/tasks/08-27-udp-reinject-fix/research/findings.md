# Host UDP reinjection investigation findings

## Code-path finding

The relay response path completed before reinjection: `FlowKey` retained the host packet's
`OriginAdapterId`, and `Program.CreateUdpCoordinator` already built a case-insensitive map from each
capture-scope stable ID to its NDISAPI enumeration handle and MAC. Before this task,
`UdpResponseReinjector.TryResolveTarget` consulted that map only for forwarded flows. Every host
response instead used the startup-selected `scope[0]` handle and MAC, regardless of the adapter on
which the client datagram was captured.

This asymmetry accounts for the path-specific observations without implicating SOCKS5 framing,
UDP checksums, or the native send return value:

- forwarded UDP already used the flow's origin-adapter target;
- host TCP uses local redirect and relay sockets rather than rebuilding a UDP response frame;
- TUN state changes routing and therefore whether the flow adapter happens to equal `scope[0]`;
- adapter/routing settling can make consecutive fresh flows leave through different adapters.

The implementation now resolves host responses through `FlowKey.OriginAdapterId`; an adapter that
disappears mid-flow uses the startup fallback with a rate-limited warning. Focused tests assert the
selected handle, direction flag, and Ethernet MACs.

## Controlled hardware experiment (2026-08-27, second session — COMPLETE)

The earlier endpoint limitations were resolved: the official `tools_bin_x64.zip` from the
WinpkFilter v3.6.1 release contains the x64 `ndisapi.dll` userspace sidecar (binary-compatible
with the installed 3.6.2.1 driver), a non-AOT self-contained Windows build was published from
Linux (`-p:PublishAot=false`, cross-OS native compile is unsupported), and a SOCKS5 UDP-capable
server (sing-box 1.13.18, socks inbound + direct outbound) ran on the Linux dev host reachable
from the guest on both subnets (192.168.100.3, 192.168.77.3).

Test fixture `192.168.100.2` (WINLTSC, Win11 IoT LTSC 26100, ndisrd 3.6.2.1 running):

| Adapter | StableId | IPv4 | MAC | ifIndex |
|---|---|---|---|---|
| External | `{A738399F-…}` | 192.168.77.2 (default route 192.168.77.1) | 00-15-5D-03-72-76 | 14 |
| Internal | `{671E1446-…}` | 192.168.100.2 | 00-15-5D-03-72-75 | 7 |

Lowercase-GUID sort puts Internal first, so `scope[0] = Internal` while every public-DNS query
egresses External — the exact `scope[0] ≠ flow adapter` topology of the field report. Config:
pass rules for the two LAN subnets, then a catch-all `proxy` rule to the remote SOCKS5 server;
`logLevel: trace`. Client: PowerShell `UdpClient` loop, fresh source port per attempt, A query
for example.com to 223.5.5.5, 2 s timeout.

### Pre-fix reproduction (HEAD 6fb9262 build)

- Loop result: **0/20 succeeded** — every attempt timed out at ~2 s.
- Trace log shows the complete relay path per attempt, e.g.
  `udp.packet.sent 192.168.77.2:59534 → 223.5.5.5:53 bytes=29`,
  `udp.packet.received … bytes=56`,
  `udp.response.reinjected origin=host target=mstcp bytes=56` — mirroring the field logs
  exactly: reinjection executed, the client socket never received the frame.
- pktmon capture of one failing attempt gives the mechanical drop point:

  ```
  18:56:34.1389  Tx 71 B  192.168.77.2.49482 > 223.5.5.5.53   (query, External filter stack)
  18:56:34.2314  Rx 98 B  00-15-5D-03-72-75 > 00-15-5D-03-72-75
                          223.5.5.5.53 > 192.168.77.2.49482   (reinjected response frame;
                          BOTH MAC slots = Internal's MAC → injected via Internal's handle)
  18:56:34.2315  DROP (tcpip, IP layer) DropReason "Not locally destined"
                          DropLocation 0xE0004136
  ```

  H-A is proven: `SendPacketToMstcp` is adapter-bound; a receive-path frame indicated on an
  interface that does not own the destination IP is dropped by Windows' strong-host receive
  validation ("Not locally destined"). This also explains every field observation (TUN on/off
  changing which adapter owns the flow's source IP; forwarded flows always using the origin
  adapter; TCP unaffected because it never rebuilds a response frame).

### Post-fix verification (working-tree build)

- Fresh service start, 5 s settle: **20/20 succeeded** (first attempt 243 ms, median ~100 ms).
- Restart round with only 1 s settle: **5/5 succeeded**, first attempt 243 ms — the
  "first attempt after service start" failure mode is gone.
- Log audit: 0 `[error]`/`[warn]` lines, 21 `udp.session.created` vs 21
  `udp.response.reinjected` — exactly one session per query, no ASSOCIATE storm, loop
  prevention intact.

Artefacts: `research/wf-test-config.json` (remote config), `research/dns-loop.ps1` (query
loop), `research/sing-box-test.json` (SOCKS server config). The remote test directory
`C:\Users\Neko\winforward-test` was left in place with both builds and logs (service stopped,
pktmon filters removed) for potential re-runs.

## Remote validation attempt (2026-08-27, first session)

The supplied WinRM endpoint `192.168.100.2` was reachable, but it was not the Windows gateway host
described in the original field report. It identified as `WINLTSC`, Windows 11 IoT Enterprise LTSC
10.0.26100, with two Hyper-V synthetic adapters:

| Adapter | IPv4 | ifIndex | MAC |
|---|---|---:|---|
| External | 192.168.77.2/24 | 14 | 00-15-5D-03-72-76 |
| Internal | 192.168.100.2/24 | 7 | 00-15-5D-03-72-75 |

The `ndisrd` service was running and Windows Packet Filter x64 3.6.2.1 was installed. However:

- no `ndisapi.dll` application sidecar was installed anywhere under Program Files or Windows;
- the cached installer (`C:\Windows\Installer\27918a3.msi`) extracted only driver/certificate
  files, not the userspace DLL;
- no previous WinForward, `q`, sing-box, or .NET SDK/runtime deployment was present;
- mapped install-source drives `T:` and `Z:` were unavailable;
- direct GitHub downloads from both the Linux runner and the remote host failed during TLS
  transfer, so the missing sidecar and SOCKS server could not be staged;
- `192.168.100.1:5985` was not reachable from the guest, so the actual host could not be managed
  through the supplied credential path.

Consequently no pre-fix packet could be generated on this endpoint and pktmon could not identify
an exact Windows drop component. The strong-host/adapter-binding explanation remains the leading
mechanism, supported by the source-path asymmetry and the original field observations, but this
session did **not** mechanically prove the drop point. Acceptance still requires a run on the
actual multi-adapter host (or another fixture with the full `ndisapi.dll` + SOCKS deployment):
capture the outbound query adapter, capture the indicated response adapter, then demonstrate at
least 20/20 host queries after the fix including the first query after startup.

## Secondary observation

The process-attribution issue for sing-box was not touched by this code path. Keep it as a separate
follow-up task; no process lookup changes were made here.
