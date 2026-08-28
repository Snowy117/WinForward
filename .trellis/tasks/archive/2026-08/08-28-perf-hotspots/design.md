# Design — Hot-Path Performance Optimization

Style mandate: the hot path is written C++-like — value types, spans into
existing buffers, raw fixed-size addresses, `unsafe` where it removes copies.
Classes/`IPAddress`/delegates survive only on cold edges (config load, process
attribution, SOCKS5 socket calls, logging, tests).

## Baseline evidence (Linux, .NET 10, Release)

- capturePump 1400B: ~2.0M pps, **669 B/pkt**, gen0 ≈ 35/M pkts.
- dispatcher warm pass: 1268 ns, 672 B/op.
- parser.ipv4Udp: 80 B/op (2× `new IPAddress`).
- flowTable cross-adapter hit @65k: 763 ns, 19.5 B/op.
- socks5Udp.encode @1472B payload: 1512 B/op.
- Full JSON: `bench-baseline-linux.json`.

## Architecture of the hot path (as-is)

```
NdisCapturePump (per adapter, own batch of NdisPacketBuffer)
  → CapturePacketProcessor.ProcessAsync
      GetFrame() span → ArrayPool.Rent + CopyTo          [managed copy #1]
      new PacketLease(frame)                             [alloc]
      IpTcpUdpPacket.TryParse → PacketView(2×IPAddress)  [2 allocs]
      PacketFlowClassifier.ClassifyFlow → FlowContext    [alloc]
      new CapturedFlowPacket(record class)               [alloc]
  → FlowDispatcher.DispatchAsync
      _flows.TryResolve (lock, 2 dictionaries)
      packet with { FlowGeneration = .. }                [alloc]
      CompleteAsync(closure)                             [2 allocs]
  → NdisPacketActionExecutor.PassAsync
      pool.Rent native buffer + SetFrame                 [native copy #2]
      reinject kernel call
```

Threading: one pump per adapter; all pumps await their handler per packet, so a
pump never reads batch slot N+1 while slot N's handler is running. Multiple
pumps share the FlowTable (lock) and the dispatcher.

## Change A — value-type core (Endpoint / PacketView / FlowContext / CapturedFlowPacket)

### A1. `Endpoint` raw address storage

```csharp
public readonly struct Endpoint : IEquatable<Endpoint>
{
    private readonly UInt128 _address;   // v4: low 32 bits; v6: all 128
    public AddressFamilyKind AddressFamily { get; }
    public ushort Port { get; }
}
```

- `AddressFamily` derived bit stored alongside; layout ≤ 24 B.
- Compatibility surface preserved: `Endpoint.From(IPAddress, ushort)` converts
  once (cold edge); new `Endpoint.FromV4(uint/be bytes, ushort)` /
  `FromV6(ReadOnlySpan<byte>)` zero-alloc constructors used by the parser.
- `ToIPAddress()` only on cold edges (SOCKS5 connect, logging).
- Manual `GetHashCode`/`Equals`: mix `(uint)(_address & 0xFFFFFFFF)`,
  `(uint)(_address >> 64)`, family, port — no virtual `IPAddress` calls.
- `IpPrefix` / `SelfTrafficRegistry` / policy CIDR matching switch to raw
  compare (`_address & mask == prefix`), keeping public constructors that take
  `IPAddress` for config/tests.

### A2. `PacketView` carries raw addresses

Replace `IPAddress SourceAddress/DestinationAddress` with `EndPoint`-style raw
fields (or directly `Endpoint` minus port). `TryParse` fills them from the frame
span — **zero allocation** (fixes the false "no allocation" doc claim too).

### A3. `FlowContext` record class → `readonly record struct`

Fields as today (FlowKey + nullable process strings). `with` copies become
stack copies. Process strings stay null on the hot path until attribution.

### A4. `CapturedFlowPacket` record class → `readonly record struct`

Holds lease reference, context struct, metadata struct, sequences. Dispatcher's
`packet with { FlowGeneration = ... }` becomes a stack copy. `IPacketActionExecutor`
signature unchanged (struct passed by value; ~56 B — fine, or `in` later).

### A5. Closure-free completion

Replace `CompleteAsync(packet, disposition, Func<ValueTask>)` with
`CompleteAsync(packet, disposition, PacketAction action, Socks5Server? server)`
executing the executor via a switch — no delegate, no closure.

## Change B — zero-copy pass fast path

### B1. Dispatch on the native span

`CapturePacketProcessor.ProcessAsync` parses and classifies directly on
`packet.Buffer.GetFrame()` (native memory span). The `ArrayPool` copy is made
**lazy**: only materialize a detached frame when a consumer needs the frame to
outlive the synchronous dispatch:

- Pass/block: consumed synchronously (reinject or drop) → no copy ever.
- TCP proxy: rewrite happens synchronously into a new frame (already builds
  its own buffers) → no pre-copy; the original frame is only read.
- UDP proxy: payload + client MAC are copied into the relay datagram (SOCKS5
  encode) → no pre-copy; encode's destination buffer is the relay's own.

`PacketLease` semantics: the lease becomes a *native-buffer lease* — completion
is bookkeeping only (disposition + exactly-once), no pool return. On unexpected
exception the packet is simply not reinjected (fail-closed as today).

### B2. In-place pass reinjection

For an unmodified pass frame, reinject the **original capture buffer**: set
`AdapterHandle = enumeration handle` (NDISAPI contract, spec
windows-ndisapi.md), keep captured `DeviceFlags`/`Flags`/`Length`/payload, then
`SendPacketToAdapter`/`SendToMstcp`. Zero frame copies.

Safety argument:
1. The pump awaits the handler per packet; batch slot N is not reused until the
   handler returns (pump reads the *next batch* only after the current batch's
   handlers complete). Reinjection is a synchronous kernel call inside the
   handler → the buffer is consumed before the pump can reuse it.
2. Kernel copies the frame during `SendPacketToAdapter/Mstcp` (WinpkFilter
   semantics — synchronous send), so no aliasing afterwards.
3. Multi-adapter: each pump owns its buffers; the reinjector receives the
   buffer by reference within the handler's stack frame.
4. Any path that must hold the frame beyond the handler (none today after B1;
   TCP redirect rewrites synchronously) must copy explicitly.

Fallback: keep the pooled-copy `PassAsync` for frames that were *modified*
(TCP rewrite path uses its own injector already) — in-place applies only to the
unmodified pass disposition.

### B3. Block path

BlockAsync needs no frame access at all — already true; with B1 it also stops
paying the ArrayPool copy.

## Change C — flow table & codecs

### C1. FlowTable

- With raw-address `FlowKey`: hashing is arithmetic; expected resolve cost at
  65k drops sharply (target ≤ 300 ns incl. cache misses).
- Keep the single `Lock` (uncontended when one adapter is busy; correctness
  first), but touch (`DateTimeOffset.UtcNow`) stays only on the slow path:
  warm resolves use `Environment.TickCount64`-cheap refresh? — NO: keep
  `Touch(UtcNow)` semantics (idle sweep depends on it), it is ~20 ns. Revisit
  only if benchmarks show it matters.
- `RemoveExpired`: replace LINQ Where/Select/ToArray with a manual loop over a
  rented snapshot buffer (sweep path, moderate win, low risk).

### C2. Socks5UdpCodec

- Add `TryEncode(destination, port, payload, Span<byte> destination, out int written)`
  for datagrams ≤ MTU (caller stackallocs/pools). Keep allocating overloads for
  tests/edges. `UdpProxySession`/`Socks5UdpTransport` send path switches to the
  span overload with a pooled per-session buffer.
- Decode's `new IPAddress` per datagram: switch the UDP relay path to raw
  addresses; `Socks5UdpDatagram` keeps `IPAddress?` for compatibility but gains
  raw fields (or a second struct) used by the reinjector.

## Compatibility & rollout

- Public test-facing APIs (`Endpoint.From(IPAddress…)` etc.) keep working —
  tests compile unchanged (only `FrameBuilders`/helpers extended).
- Benchmarks extended: `parser.ipv4Udp` must report 0 B/op; dispatcher warm
  pass ≤ 64 B/op; capturePump allocations collapse; add a
  `passInPlace.reinject` micro-bench if feasible without native driver (keep
  managed-only: measure the managed side up to the reinjector seam).
- Windows validation (optional, if test machine reachable): run the CLI under
  real NDIS with a pass-all config, verify throughput/CPU with typeperf; the
  evil-winrm access `evil-winrm-py -i 192.168.100.2 -u neko -p $NEKO_PASS` is
  available.

## Risks

| Risk | Mitigation |
|---|---|
| Native-buffer lifetime bugs (use-after-reuse) | pump contract documented + NdisCapturePumpTests extended to assert no batch-slot reuse before handler completion |
| Struct copies of CapturedFlowPacket regress perf | sizes ≤ 64 B, measure; `in` modifiers if needed |
| IPAddress removal breaks edge code | keep conversion helpers; compiler-guided mechanical migration |
| Record-struct `with` on hot path allocates via defensive copy? | `readonly record struct` avoids defensive copies for getters |

## Validation

- `dotnet build -c Release` (all projects)
- `dotnet test` (all tests, any OS)
- `dotnet run benchmarks … --output bench-after-linux.json` → compare table in
  task report; acceptance numbers in prd.md.
