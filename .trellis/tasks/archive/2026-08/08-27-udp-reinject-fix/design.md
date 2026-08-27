# Design — Fix host UDP reinjection delivery failure

## Current architecture (as read from source)

```
client socket (q, 192.168.3.114:port)
   │ UDP out (captured ON_SEND on WLAN adapter, host flow)
   ▼
CapturePacketProcessor → PacketFlowClassifier (FlowKey carries OriginAdapterId=WLAN)
   ▼
FlowDispatcher (rule 5 catch-all → proxy main)
   ▼
NdisPacketActionExecutor.HandleUdpProxyAsync — consumes frame, records client MAC
   ▼
UdpProxyCoordinator.TrySendAsync → Socks5UdpTransport (control TCP + relay UDP socket
bound to wildcard, self-traffic registered) → sing-box mixed inbound
   ▼
response arrives on relay socket → UdpProxySession.ReceiveLoopAsync
   ▼
UdpResponseReinjector.InjectAsync(flow, source, payload, clientMac)
   ├─ forwarded flow: SendToAdapter(flow's origin adapter, dst=client MAC)   ✅ works
   └─ host flow:       SendToMstcp(_host = scope[0] adapter)                 ❌ flaky
```

`_host` is chosen once at startup in `Program.cs:293` as `scope[0]` where scope is
`OrderBy(StableId)` (lowercase GUID). With 11-12 adapters this is effectively a random
adapter; it is almost never the adapter the flow actually used. With TUN on, scope[0] is
the Meta TUN adapter; with TUN off it is 本地连接 2. The client query leaves via WLAN.

## Hypotheses to discriminate experimentally (R2)

- H-A (primary): MSTCP drops a receive-path frame injected on an adapter that does not own
  the destination IP (no weak-host receive by default). UNIFIED EXPLANATION of all field
  observations under H-A (added after user confirmed TUN correlation):
  * TUN off: host flows egress WLAN (src 192.168.3.114), scope[0] = 本地连接 2
    {0D8855B4-...} → reinject on 本地连接 2 whose IP set does not contain 192.168.3.114 →
    strong-host drop → system DNS over UDP broken (matches user: "关闭TUN则无法解析DNS").
  * TUN on (settled): sing-box TUN attracts host routes (auto_route or metric), host flows
    egress Meta with src 198.18.0.1, scope[0] = Meta {0AFD3ED9-...} → reinject on Meta which
    OWNS 198.18.0.1 → delivered (matches user: "开启TUN之后上网就正常了").
  * TUN on, first q after service restart (12:56:45 fail): routing/adapter settling window —
    query still egressed WLAN, captured there, but reinjected on Meta → dropped. Second
    attempt 12s later (12:56:57) routed via Meta → success. Explains the "intermittency".
  * Forwarded flows bypass scope[0] (origin-adapter map) → always fine; TCP uses redirect,
    no frame reinjection → always fine.
  Experiment must still verify H-A mechanically (pktmon) before the fix ships.
- H-B: NDISAPI `SendPacketToMstcp` frames must carry the *receiving adapter's* MAC as the
  Ethernet destination (and maybe source) to pass MSTCP's indicaton path; the reinjector
  uses host-adapter MAC on both slots, which is correct only for scope[0].
- H-C: timing/ARP state — the destination MAC used for host flows is the host adapter's own
  MAC, which MSTCP should accept; intermittency may come from UDP checksum offload or
  source-address validation (weak host send model) rather than the adapter choice.
- Experiment plan on test machine 192.168.100.2:
  1. Build instrumented WinForward (timestamps first) and a minimal SOCKS5 UDP test server.
  2. Loop `q`-equivalent (PowerShell UDP client or q if present) at steady state; log
     success/failure per attempt vs scope[0] identity vs query adapter.
  3. pktmon capture on all adapters during a failing attempt to see where the reinjected
     frame dies (which adapter it is indicated on, whether MSTCP swallows it).
  4. Patch candidate fix (use flow's OriginAdapterId for host flows); rerun loop ≥20x.

## Fix shape (R3)

`UdpResponseReinjector.TryResolveTarget` for host flows: prefer
`_byStableId[flow.OriginAdapterId]` when present (with that adapter's MAC as both Ethernet
src/dst, towardMstcp=true on that adapter's handle); fall back to `_host` only when the
origin adapter cannot be resolved (adapter disappeared mid-flow), keeping fail-closed
logging rate-limited. `Program.cs` keeps constructing the full `adaptersByStableId` map
already; `_host` remains as fallback.

Loop prevention: unchanged — the response pass-back branch in FlowDispatcher keys on the
flow table, not on adapters.

## Logging change (R1)

`RuntimeLogging.cs` plain-text sink: prefix every line with local wall-clock
`yyyy-MM-dd HH:mm:ss.fff`. Keep `[level] event key=value` shape after the prefix. Update
README's log-format paragraph. Unit tests that assert exact line output get the prefix
accounted for (regex `^\[\d{4}-...` etc.).

## Risks / compatibility

- Host flows captured on an adapter that left the capture scope mid-run: fallback path must
  not throw; rate-limited warn + drop (fail-closed) as today for forwarded flows.
- `SendToMstcp` on a Hyper-V vEthernet adapter: behavior to validate in the experiment
  phase before shipping the fix (H-B).
- Log-line format is parsed by humans only; additive prefix is low risk but README must be
  updated in the same change.
