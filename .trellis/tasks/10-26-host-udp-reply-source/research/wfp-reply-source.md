# Research: WFP return-source mechanisms for a host UDP local-redirect proxy

- **Query**: Does any documented WFP mechanism preserve the original UDP destination as the reply
  source? Can the driver inject an inbound UDP datagram with that source? Can it instead rewrite the
  listener reply's source? What can be reused from the forwarded injection code?
- **Scope**: mixed (Microsoft WFP/Winsock documentation, first-party WFP sample, internal driver and
  prior VM evidence)
- **Date**: 2026-10-26

---

## 0. Verdict

1. **No documented return-source mapping exists for UDP.** Connect/bind redirection is documented as
   a TCP mechanism; redirect records (`SIO_QUERY/SET_WFP_CONNECTION_REDIRECT_RECORDS`) are
   explicitly "the new TCP socket". There is no UDP equivalent that carries a reply source.
2. **The only documented, first-party mechanism for a transparent UDP proxy return is
   transport-receive injection**: clone/construct an IPv4/IPv6 UDP packet, rewrite the source to the
   original destination, and call `FwpsInjectTransportReceiveAsync0`. Microsoft's `ddproxy` WFP
   sample does exactly this at `FWPS_LAYER_DATAGRAM_DATA_V4/V6`
   (`sys/DD_proxy.c:727-899`), and its README states the behaviour is "transparent to the application".
3. **Injection is feasible API-wise but has two independent blockers that must be prototyped
   before committing:**
   - **ALE/firewall:** injected inbound packets re-enter from the bottom and traverse the transport
     layer and the *optional* ALE receive/accept layer, which is where the Windows Firewall decides
     the first UDP packet of a new remote tuple. Prior evidence is contradictory (a WFP sample and a
     general "source may be non-local" statement vs. a 10-21 probe that got
     `STATUS_NOT_ACCEPTED` and an OSR report of firewall-dependent failure).
   - **Connected sockets (premise conflict):** after `ALE_CONNECT_REDIRECT` the connected UDP
     socket's accepted peer *is the redirect target* (the listener), not the original destination.
     A datagram whose source is the original destination is filtered by Winsock. This makes the
     source contract unattainable for connected clients under local redirect, independent of the
     injection mechanism.
4. **Source rewrite is ruled out empirically.** The listener reply is a local-to-local datagram;
   the 10-21 probe measured it invisible at `DATAGRAM_DATA_V4` (inbound and outbound) and at
   `INBOUND_IPPACKET_V4` (`route-a-findings.md:41-87`). A callout cannot rewrite what it never sees.
   The only pre-ALE layer that might see it (`INBOUND_TRANSPORT`) is the wrong side of the firewall.
5. **The forwarded injection helpers are only partially reusable.** `probe_forwarded_inject_frame`
   is MAC-frame oriented (`FwpsInjectMacReceiveAsync0`, Ethernet header, own checksums); the reusable
   parts are the NBL/MDL pool pattern, the outstanding-injection bookkeeping and the checksum
   helpers. The host reply needs a new transport-injection TU.

---

## 1. Documented mechanisms

### 1.1 Connect/bind redirection is a TCP contract

[Using Bind or Connect Redirection](https://learn.microsoft.com/en-us/windows-hardware/drivers/network/using-bind-or-connect-redirection)

- "A WFP connection redirection callout redirects an application's connection request so that the
  application connects to a proxy service instead of the original destination."
- The proxy flow is described with `WSAAccept`/`SIO_QUERY_WFP_CONNECTION_REDIRECT_RECORDS`/
  `SIO_QUERY_WFP_CONNECTION_REDIRECT_CONTEXT` on the accepted socket and
  `SIO_SET_WFP_CONNECTION_REDIRECT_RECORDS` on "the new (proxied) socket" — all TCP socket calls.
- The redirectable traffic list does include UDP, but only for *routing the outbound association* to
  the proxy; nothing in the page describes how a reply regains the original source.

[Using Proxied Connections Tracking](https://learn.microsoft.com/en-us/windows-hardware/drivers/network/using-proxied-connections-tracking)
says "A callout driver calls the `FwpsRedirectHandleCreate0` function to create a handle that can be
used to redirect TCP connections." The redirect state API is the documented anti-loop gate; the
driver already implements it (`probe_classify.cpp:377-395`).

[SIO_SET_WFP_CONNECTION_REDIRECT_RECORDS](https://learn.microsoft.com/en-us/windows-hardware/drivers/network/sio-set-wfp-connection-redirect-records)
— "specify the redirect record to the new TCP socket used for connecting to the final destination".
[SIO_QUERY_WFP_CONNECTION_REDIRECT_RECORDS](https://learn.microsoft.com/en-us/windows-hardware/drivers/network/sio-query-wfp-connection-redirect-records)
— "can only be used if the connection was redirected at the `FWPS_LAYER_ALE_CONNECT_REDIRECT_V4` or
`FWPS_LAYER_ALE_CONNECT_REDIRECT_V6` layer by a WFP client." Both are Winsock/TCP-shaped.

**Conclusion:** redirect records are not a UDP reply-source mechanism. They belong on the proxy's
outbound TCP socket, and the Kernel host TCP path already uses them
(`src/WinForward.Kernel.Runtime/Relay/SocketIoctl.cs`).

### 1.2 What the injection documentation actually promises

[Packet Injection Functions](https://learn.microsoft.com/en-us/windows-hardware/drivers/network/packet-injection-functions)

- `FwpsInjectTransportReceiveAsync0` is listed for "packet data from the transport, datagram data,
  ICMP error, or ALE layers" with destination "the receive data path".
- **Re-entry semantics:** "injected incoming packets reenter from the 'bottom' of the stack and WFP
  layers … a UDP packet injected from the incoming datagram data layer will reenter the stack and
  traverse the network layer, the transport layer, the ALE receive or accept layer (optional), and
  back into the datagram data layer."
  This is the single most important sentence for the feasibility question: ALE is in the path, but
  the documentation calls it *optional* and does not define when it is skipped.
- "A packet injected by a WFP callout driver will be re-indicated to the callout … callouts should
  permit self-injected packets." Loop prevention is the driver's job via
  `FwpsQueryPacketInjectionState0`.
- "For raw send operations, the net buffer list must contain only a single net buffer."

[FwpsInjectTransportReceiveAsync0](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/fwpsk/nf-fwpsk-fwpsinjecttransportreceiveasync0)

- "injects packet data from the transport, datagram data, or ICMP error layers into the receive data
  path."
- `netBufferList`: "The `NET_BUFFER_LIST` structure must begin with an IP header."
- "If the return value is not `STATUS_SUCCESS`, the completion function will not be called. In this
  case, the net buffer list … must be freed by … `FwpsFreeNetBufferList0`."
- "Due to the TCP protocol locking semantics, TCP can only be injected Out of Band at any transport
  layer or equivalent layer, so `FwpsInjectTransportReceiveAsync0` and
  `FwpsInjectTransportSendAsync0` must be queued and run by a DPC."
- "This function can be called from one of the following transport layers if the
  `FWPS_METADATA_FIELD_ALE_CLASSIFY_REQUIRED` metadata flag is not set:
  `FWPS_LAYER_INBOUND_TRANSPORT_V4/V6`, `FWPS_LAYER_DATAGRAM_DATA_V4/V6` (inbound), …". The sample
  calls it from a system worker thread (`sys/DD_proxy.c:977-981`), i.e. PASSIVE, and it works.
- IRQL `<= DISPATCH_LEVEL`; "The injected packet can be indicated to the callout driver again."

[FwpsInjectionHandleCreate0](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/fwpsk/nf-fwpsk-fwpsinjectionhandlecreate0)

- Use `FWPS_INJECTION_TYPE_TRANSPORT`; `AF_UNSPEC` is allowed for transport/stream/forward handles.
  `FwpsInjectionHandleDestroy0` "will wait for their completion before returning" — the existing
  forwarded stop path relies on this and drains explicitly anyway (`probe_forwarded.cpp:300-311`).

[FwpsQueryPacketInjectionState0](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/fwpsk/nf-fwpsk-fwpsquerypacketinjectionstate0)
— the only documented loop guard; the forwarded callouts already use it.

### 1.3 The documented UDP proxy return: `ddproxy`

Microsoft's WFP packet-modification sample is the reference implementation of exactly this problem:

- https://github.com/microsoft/Windows-driver-samples/tree/main/network/trans/ddproxy
- `README.md`: "For inbound traffic, Ddproxy.sys redirects the traffic back to the original address
  and UDP port values. This redirection is transparent to the application." and "Packet modification
  is done out-of-band by a system worker thread by using the reference-drop-clone-modify-reinject
  mechanism."
- `sys/DD_proxy.c:727-899` (`DDProxyCloneModifyReinjectInbound`):
  1. `NdisRetreatNetBufferDataStart` to the IP header, `FwpsAllocateCloneNetBufferList`;
  2. rewrite the UDP source port in place, set `udpHeader->checksum = 0`;
  3. `FwpsConstructIpHeaderForTransportPacket(cloned, ipHeaderSize, family, toRemoteAddr /* new
     source */, localAddr /* destination */, IPPROTO_UDP, 0, NULL, 0, 0, NULL, 0, 0)`
     (`:857-877`);
  4. `FwpsInjectTransportReceiveAsync(gInjectionHandle, NULL, NULL, 0, family, compartmentId,
     interfaceIndex, subInterfaceIndex, cloned, DDProxyInjectComplete, packet)` (`:885-898`).
- `sys/DD_proxy.c:318-327`: `FwpsQueryPacketInjectionState` first, permit `INJECTED_BY_SELF`.
- `sys/DD_proxy.c:373`: `FwpsReferenceNetBufferList(..., TRUE)` to keep the NBL valid out of band.
- `sys/DD_drv.c:302-306`, `:594-672`: terminating callout filters at
  `FWPM_LAYER_DATAGRAM_DATA_V4/V6` in its own sublayer with `FWP_EMPTY` weight.

**Conclusion:** transport-receive injection of a locally constructed packet with a non-local source
is a documented, first-party mechanism. The docs even state it explicitly for
[FwpsConstructIpHeaderForTransportPacket0](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/fwpsk/nf-fwpsk-fwpsconstructipheaderfortransportpacket0):
"The source IP address can be modified to be another locally defined IP address or **another address
that does not exist on the local computer**. Packets so modified can then be sent or injected into
the receive or forwarding data path."

The same page also removes a checksum concern: "Full checksums are calculated for upper-level
protocols (TCP, UDP, and ICMP). The IP checksum is recalculated when the IP header is reconstructed."
`FwpsConstructIpHeaderForTransportPacket0` disables LSO/checksum offload for the resulting NBL.

### 1.4 The layer model and the firewall

[UDP Packet Flows](https://learn.microsoft.com/en-us/windows/win32/fwp/udp-packet-flows) gives the
received-packet order:

```
INBOUND_IPPACKET_V4 -> INBOUND_TRANSPORT_V4 -> ALE_AUTH_RECV_ACCEPT_V4
                    -> ALE_FLOW_ESTABLISHED_V4 -> DATAGRAM_DATA_V4 (INBOUND)
```

[ALE Layers](https://learn.microsoft.com/en-us/windows/win32/fwp/ale-layers): "A filter at the
`FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V{4|6}` layer is matched for … first UDP packets (unicast) from a
unique remote address/port tuple". [ALE Stateful Filtering](https://learn.microsoft.com/en-us/windows/win32/fwp/ale-stateful-filtering):
"A UDP ALE flow … represents the remote peer that the application is communicating with. A remote
peer is identified by a (Destination IP Address and Destination Port) tuple."

The Windows Firewall is implemented as WFP filters at the ALE layers. The driver's own comment
records the arbitration fact: the firewall lives in a higher-weight sublayer, so a terminating
permit from `FWPM_SUBLAYER_UNIVERSAL` cannot override its block
(`probe_driver.cpp:253-257`; `tools/windows/firewall-check.ps1` has the evidence).

Consequence: an injected inbound UDP datagram whose source is the original remote is a **new inbound
UDP ALE flow** (new remote tuple on that local port). Whether the firewall permits it is the
open question; documentation says ALE traversal is "optional" but never states the condition.

Two documented anti-loop facts reduce the other risks:

- `ALE_CONNECT_REDIRECT` is an outbound-connect layer only; an inbound injected packet does not
  re-enter it, so it cannot create a new redirect association. There is no redirect loop.
- "UDP connection termination is not indicated at any WFP layer" (UDP Packet Flows). The listener's
  existing association lives until idle closure; the driver cannot observe its end.

### 1.5 Prior internal evidence (this repo)

| Evidence | Result |
|---|---|
| `.trellis/tasks/10-26-host-udp-baseline/verification/a4-failure.md:16-30` | Listener `sendto` reply arrives as `192.168.100.2:<listener-port>`; the pass arm is correct. A4 fails. |
| `a4-failure.md:32-42` | Userland raw `IP_HDRINCL` with a remote source is not delivered, firewall on or off. |
| `.trellis/spec/backend/kernel/research/10-21-host-udp-route-a-probe/research/route-a-findings.md:41-57` | The relay's local-to-local reply is invisible at `DATAGRAM_DATA_V4` (inbound and outbound). |
| `route-a-findings.md:72-87` | Also invisible at `INBOUND_IPPACKET_V4`; "A1 (relay reply + driver rewrite on the packet path) is not viable". |
| `route-a-findings.md:96-114` | Hand-built IPv4+UDP NBL injected with `FwpsInjectNetworkReceiveAsync0` and `FwpsInjectTransportReceiveAsync0`, source `1.2.3.4:4444`, destination `127.0.0.1:50451` → completion `0xC000021B` `STATUS_NOT_ACCEPTED`; `--interface 0` fails synchronously with `STATUS_INVALID_PARAMETER`, interface 1 (loopback) passes validation. |
| `route-a-findings.md:124-145` | A **connected** client accepted the relay's ordinary reply (`from=127.0.0.1:5555`), i.e. the redirect re-pointed the accepted peer to the target. |

The last row is the basis for section 2.4. The `STATUS_NOT_ACCEPTED` row is *not* conclusive for the
A4 shape because its destination was a loopback address (`127.0.0.1:50451`); the A4 client's local
endpoint is a NIC address (`192.168.100.2`).

---

## 2. Transport-receive injection: feasibility

### 2.1 API sequence (documented, sample-proven)

```
handle  = FwpsInjectionHandleCreate0(AF_UNSPEC, FWPS_INJECTION_TYPE_TRANSPORT, &h)
pool    = NdisAllocateNetBufferListPool(NULL, {fAllocateNetBuffer=TRUE, DataSize=0, PoolTag})
storage = ExAllocatePool2(NON_PAGED, ip_backfill + 8 + payload_len, tag)
          # UDP header at storage+backfill, checksum 0, length = 8+payload
mdl     = NdisAllocateMdl(pool, storage, ip_backfill + 8 + payload_len)
nbl     = FwpsAllocateNetBufferAndNetBufferList0(pool, 0, 0, mdl, ip_backfill, 8+payload_len, &nbl)
FwpsConstructIpHeaderForTransportPacket0(nbl, 0, family, /*source*/ original, /*remote*/ client,
                                         IPPROTO_UDP, 0, NULL, 0, 0, NULL, 0, 0)
FwpsInjectTransportReceiveAsync0(h, NULL, NULL, 0, family, UNSPECIFIED_COMPARTMENT_ID,
                                 ifindex, 0, nbl, completion, NULL)
completion: FwpsFreeNetBufferList0(nbl); NdisFreeMdl(mdl); ExFreePoolWithTag(storage, tag)
```

Details that matter:

- **Backfill is mandatory.** `FwpsConstructIpHeaderForTransportPacket0` builds the IP header in
  front of the transport header. `FwpsAllocateNetBufferAndNetBufferList0`'s `dataOffset` is the
  unused space before the data, so the storage must reserve 20 bytes (IPv4) or 40 (IPv6) and
  `dataOffset` must be exactly that size. The forwarded code never needed this because it copies a
  whole Ethernet frame (`probe_forwarded_frame.cpp:369-385`).
- **MDL allocation.** The forwarded path learned that `IoAllocateMdl` produces an MDL the inbound
  stack discards; `NdisAllocateMdl(pool, ...)` is required (`probe_forwarded_frame.cpp:456-464`).
  Reuse that finding.
- **Checksums.** Let `FwpsConstructIpHeaderForTransportPacket0` do them; do not hand-roll UDP
  checksums for this path. (IPv6 UDP checksum is mandatory; the construct API computes it.)
- **IRQL.** The documented IRQL is `<= DISPATCH_LEVEL`; the "must be queued and run by a DPC"
  sentence is motivated by TCP locking. The first-party sample calls it from a system worker at
  PASSIVE. Calling it from the IOCTL dispatch (PASSIVE) is allowed by the API contract and has
  sample precedent; if it returns `STATUS_INVALID_DEVICE_STATE`/`STATUS_FWP_TCPIP_NOT_READY`, fall
  back to a queued work item.
- **Completion.** It can run at `DISPATCH_LEVEL`; only non-paged frees
  (`FwpsFreeNetBufferList0`, `NdisFreeMdl`, `ExFreePoolWithTag`) are allowed there. The forwarded
  completion is the template (`probe_forwarded_frame.cpp:199-241`).
- **Interface index.** `0` is rejected (`STATUS_INVALID_PARAMETER`, 10-21). The destination is a
  local address, so the arrival interface is conceptually the loopback interface (index 1 on this
  VM). The sample uses the original packet's interface index, which does not exist for a fabricated
  packet. This is a prototype parameter; `subInterfaceIndex = 0`.
- **Compartment.** `UNSPECIFIED_COMPARTMENT_ID`; the client is in the default compartment.

### 2.2 Firewall / ALE

Documented facts:

- Injected inbound packets traverse the transport layer and then ALE receive/accept "optional"
  (Packet Injection Functions).
- ALE receive/accept is where the first inbound unicast UDP packet from a *new* remote tuple is
  authorized (ALE Layers).
- The Windows Firewall's decisions are WFP filters at ALE, in a higher-weight sublayer than any
  filter this driver can install at `FWPM_SUBLAYER_UNIVERSAL` (`probe_driver.cpp:253-257`).

Contradictory secondary evidence:

- `ddproxy` injects with a rewritten source and claims transparency; it is a Microsoft sample but
  documents no firewall requirement.
- An OSR report of the same pattern (`FwpsInjectTransportReceiveAsync0` at
  `DATAGRAM_DATA_V4` inbound) shows `STATUS_PROTOCOL_UNREACHABLE` in the completion **only when the
  Windows Firewall is enabled**, and success when it is disabled:
  https://community.osr.com/t/fwpm-layer-datagram-data-v4-injection-error/57958
- A StackOverflow report of a loopback UDP proxy using the same mechanism gets
  `STATUS_DATA_NOT_ACCEPTED` when rewriting the source:
  https://stackoverflow.com/questions/79790785/wfp-fwpm-layer-datagram-data-v4-modify-loopback-inbound-udp-packet-address-faile
- The 10-21 probe got `STATUS_NOT_ACCEPTED` for a fabricated source (loopback destination).

**Assessment:** the firewall is expected to classify the injected datagram as a new inbound UDP
flow and apply the inbound default policy, which normally blocks it for a client application. The
"optional" ALE wording is not a promise of exemption. This must be measured before the design is
accepted. A driver-side permit cannot override the firewall from
`FWPM_SUBLAYER_UNIVERSAL`; overriding it would require a higher-weight sublayer, i.e. a deliberate
firewall bypass and a user-level security decision.

### 2.3 Loop and re-redirect behaviour

- No connect-redirect re-entry: the layer is outbound-connect only.
- No new host UDP association: injection never creates an outbound association, so `udp_classify_calls`
  does not grow and no session is created. RS-AC2 holds structurally.
- The **listener's** outbound association still exists (it was created when the client's datagram
  arrived). If the listener keeps using `sendto`, that reply exists as today; the injection design
  must drop the listener `sendto` to avoid duplicate delivery.
- The injected packet is indicated to any callout that owns the layer. This driver has no callout at
  `DATAGRAM_DATA`/`INBOUND_TRANSPORT`, so there is nothing to re-indicate to; if a
  `DATAGRAM_DATA` callout is ever added, it must permit `FWPS_PACKET_INJECTED_BY_SELF`.

### 2.4 Connected UDP sockets: the premise conflict (critical)

Measured on the VM (2026-10-26, raw `Socket`, IPv4 loopback):

```
1) connected <- other source: SOCKERR TimedOut(10060)
2) connected <- peer        : RECV 3B from=127.0.0.1:50903
3) unconnected <- other     : RECV 2B from=127.0.0.1:50905
```

Windows filters inbound datagrams on a connected UDP socket by peer. Combined with
`route-a-findings.md:124-145` (a connected client accepted the relay reply whose source was the
redirect target), the peer after `ALE_CONNECT_REDIRECT` is the **listener**, not the original
destination. ALE Layers states the redirect applies "for the duration of that connection".

Therefore, for a **connected** client:

- a datagram injected with the original destination as source is **dropped by Winsock** (source does
  not match the peer);
- the only accepted source is the listener endpoint, i.e. the baseline's `sendto` shape.

This is a structural property of local redirect, not of the injection implementation. It also means
the parent design's A8 note ("the connected arm checks payload only",
`.trellis/tasks/10-26-kernel-host-udp/design.md:241`) is only satisfiable by a listener-sourced
reply, while A4's source assertion is only satisfiable by an injected reply. **One mechanism cannot
serve both without knowing whether the association is connected, and no documented API exposes
that bit**:

- `FWPM_LAYER_ALE_CONNECT_REDIRECT_V4` exposes no connected/uconnected condition
  ([conditions list](https://learn.microsoft.com/en-us/windows-hardware/drivers/network/filtering-conditions-available-at-each-filtering-layer));
  `FWP_CONDITION_FLAG_IS_IMPLICIT_BIND` is documented only for Vista/2008 and describes an
  implicit bind, not a connected peer.
- `FWPS_CONNECT_REQUEST0` has no such member; the metadata list at that layer has no such field.
- The Windows UDP endpoint table has no remote field: a connected AF_INET and a connected dual-mode
  socket both appear as `local=0.0.0.0:<port> remote=:` in `Get-NetUDPEndpoint` (measured on the VM,
  2026-10-26). `MIB_UDPROW_OWNER_PID` is `{dwLocalAddr, dwLocalPort, dwOwningPid}` only.

The first decisive experiment is therefore **cheap and needs no new code**: run the existing
baseline A8 connected arm (listener `sendto`) and print `socket.RemoteEndPoint` after `Connect()`
plus the received `from=`. If it is received (expected), connected clients must keep a
listener-sourced reply and the source contract cannot apply to them.

### 2.5 What injection cannot fix

- The client's local endpoint may be loopback (`127.0.0.1`). Injecting a remote source to a loopback
  destination is the 10-21 `STATUS_NOT_ACCEPTED` case and the userland raw-socket failure. Loopback-
  bound clients cannot receive a spoofed-source reply through this path.
- A connected client (above).
- A client that has already closed its port: the injected packet is dropped/ICMP; counted, not an
  error path.

---

## 3. Source rewrite: feasibility

### 3.1 On the packet path — ruled out

`route-a-findings.md:41-87` measured the listener's local-to-local reply invisible at
`FWPS_LAYER_DATAGRAM_DATA_V4` (both directions) and `FWPS_LAYER_INBOUND_IPPACKET_V4`. A WFP callout
cannot rewrite a packet it never receives. `INBOUND_TRANSPORT_V4` was not tested, but it is *before*
ALE receive/accept in the receive order, so a rewrite there would present the spoofed tuple to the
firewall anyway.

`FwpsAcquireWritableLayerDataPointer0`/`FwpsApplyModifiedLayerData0` are documented for the
bind/connect redirect layers (and stream layers); there is no writable-packet facility at the
transport/datagram packet layers that would avoid drop-and-reinject.
[Using Bind or Connect Redirection](https://learn.microsoft.com/en-us/windows-hardware/drivers/network/using-bind-or-connect-redirection)
also states: "Changing the local address and port of a flow is only supported in the bind-redirect
layer."

### 3.2 On the listener's outbound reply — ambiguous by construction

If the driver instead intercepted the listener's outbound reply (at `DATAGRAM_DATA` outbound or
`OUTBOUND_TRANSPORT`) and rewrote its source, it would need to know *which* original destination
that reply belongs to:

- the reply's only identifying fields are the listener source port and the client endpoint;
- an unconnected client socket may talk to many remotes through the same listener port, so
  `(listener port, client endpoint)` is not unique;
- the payload is opaque (relay data), so it cannot carry a marker;
- IP options are not usable from userland and are stripped/limited;
- per-association reply ports (a fresh socket per association) would make the mapping unique, but
  each new socket is a new outbound association. It would be permitted only by gate 4
  (self-PID), adds an ALE flow per association, and the rewrite layer is still invisible for
  local-to-local traffic. This is strictly worse than injection.

### 3.3 Comparison

| | Injection | Source rewrite |
|---|---|---|
| Documented | yes (ddproxy, Construct+IInject) | no |
| Sees the reply | n/a (constructs it) | no — measured invisible at packet layers |
| Association identity | caller supplies the full 4-tuple; unambiguous | ambiguous for unconnected multi-remote sockets |
| Firewall | traverses ALE (risk) | n/a |
| Connected client | dropped (peer = listener) | dropped or invisible |
| Diff size | new IOCTL + injection TU + stats | new callout layer + table/ports + rewrite + checksums |

---

## 4. Reusable forwarded code

| Forwarded asset | Reusable? | Notes |
|---|---|---|
| `probe_forwarded_inject_frame` (`probe_forwarded_frame.cpp:350-511`) | **No** | MAC frames: Ethernet/VLAN parsing, `FwpsInjectMacReceiveAsync0/SendAsync0`, `interface_index`+`ndis_port`, own IP/UDP/TCP checksums. A transport NBL must start at the IP header and has no MAC. |
| NBL pool + MDL + storage + completion bookkeeping (`probe_forwarded_frame.cpp:199-241`, `456-509`) | **Yes, pattern** | Same three-resource ownership and the same "free in completion; on synchronous failure free in the caller" ordering. Reuse the shape with a new pool tag. |
| `NdisAllocateMdl` requirement, not `IoAllocateMdl` (`probe_forwarded_frame.cpp:456-464`) | **Yes** | Measured; applies to any inbound injection. |
| outstanding-injection counter + drain (`probe_forwarded_state.h:49`; `probe_forwarded_frame.cpp:513-526`; `probe_forwarded.cpp:300-311`) | **Yes, generalize** | The drain helper is coupled to `g_forwarded_state`; parameterize by counter or duplicate four lines for the host state. |
| Checksum helpers (`probe_forwarded_checksum.h:1-87`) | **Mostly unused** | `FwpsConstructIpHeaderForTransportPacket0` computes IP+UDP checksums. Keep them for host tests of a hand-built packet if the prototype bypasses Construct. |
| Injection handle creation (`probe_forwarded.cpp:236-241`) | **Pattern** | New handle with `FWPS_INJECTION_TYPE_TRANSPORT` (not `_L2`). |
| Anti-loop `FwpsQueryPacketInjectionState0` (`probe_forwarded_classify.cpp`, `probe_classify.cpp:377-395`) | **Only if a callout is added** | The host reply path adds no callout, so no loop check is needed on the injection path itself. |
| `redirect_context` (`kernel/src/common/redirect_context.h:36-57`) | **No change** | The reply request carries the same original destination; the context layout stays v1. |

**WDK availability.** The driver targets `WindowsTargetPlatformVersion 10.0.28000.0`
(`kernel/src/driver/wfprobe.vcxproj:12`) with `TargetVersion=Windows10`; the VM SDK is
10.0.28000.2526 and the OS is 26100. `FwpsConstructIpHeaderForTransportPacket0`,
`FwpsInjectTransportReceiveAsync0`, `FWPS_INJECTION_TYPE_TRANSPORT` and
`FwpsConstructIpHeaderForTransportPacket0`'s `flags` parameter are all present. Pass `flags = 0`
(the `FWPS_CONSTRUCT_IPHEADER_FOR_SEND/RECEIVE` values are documented as 23H2+ and are only needed
for USO/URO).

---

## 5. Evidence ledger

| Claim | Status |
|---|---|
| Listener `sendto` source is the listener | measured (A4) |
| Raw-socket userland spoofing is not viable | measured (A4) |
| Loopback reply invisible at `DATAGRAM_DATA`/`INBOUND_IPPACKET` | measured (10-21) |
| `FwpsInjectTransportReceiveAsync0` with fabricated non-local source | measured once, loopback destination → `STATUS_NOT_ACCEPTED` (10-21) |
| ddproxy pattern is the documented mechanism | documented + first-party sample |
| `FwpsConstructIpHeaderForTransportPacket0` may use a non-local source | documented |
| Connected UDP socket filters by peer | measured (VM, 2026-10-26) |
| Redirect re-points the connected peer to the listener | inferred from 10-21 #8 + the filtering measurement; **must be re-measured in A8** |
| Injected inbound UDP is subject to ALE/firewall | documented (traversal) + secondary reports; **must be measured** |
| Injection interface index 1 (loopback) is accepted | 10-21 partial (`--interface 0` rejected, 1 passed validation) |
| No UDP ALE bypass / return-source mapping | documentation search found none |

## Caveats / not found

- Microsoft does not document whether an injected inbound datagram is exempt from ALE; the word
  "optional" is the only hint. No official sample states a firewall requirement.
- The exact completion status for a firewall-blocked injection is not documented; the OSR report
  shows `STATUS_PROTOCOL_UNREACHABLE`, and 10-21 shows `STATUS_NOT_ACCEPTED` for a loopback
  destination. They may be different failure classes.
- `INBOUND_TRANSPORT_V4` visibility of a local-to-local reply was never measured; it is not useful
  for a rewrite anyway (pre-ALE), but it matters if someone later proposes a transport-layer
  observer.
- The connected-peer conclusion should be re-measured against the baseline (one script change), not
  carried over from the probe driver.
