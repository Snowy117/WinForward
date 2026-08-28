# Implement — Ordered Execution Plan

Each step ends with a validation gate. `cd /home/neko/Projects/WinForward`.

## Step 1 — Raw-address Endpoint & PacketView (Change A1+A2)

- [ ] `src/WinForward.Core/Domain.cs`: `Endpoint` storage → `UInt128` +
      family + port; manual `Equals/GetHashCode`; keep `From(IPAddress, ushort)`
      + `Address`→`ToIPAddress()`; add zero-alloc `FromV4Raw`/`FromV6(span)`.
- [ ] Migrate `IpPrefix`, `Policy.cs`, `ProcessSelectors.cs` CIDR/endpoint
      matching to raw compares (public ctors keep `IPAddress` overload).
- [ ] `src/WinForward.Protocols/IpTcpUdpPacket.cs`: `PacketView` address fields
      → raw; parser fills from span (zero alloc). Same for `IpUdpPacket`.
- [ ] Fix stale doc comment claiming zero-alloc parse.
- Gate: `dotnet build -c Release && dotnet test`; bench: parser.ipv4Udp
  allocatedBytesPerOperation == 0.

## Step 2 — Struct contexts & closure-free dispatch (Change A3+A4+A5)

- [ ] `FlowContext` → `readonly record struct`.
- [ ] `CapturedFlowPacket` → `readonly record struct` (executor interface
      signatures unchanged; adjust null-checks → default checks).
- [ ] `FlowDispatcher.CompleteAsync` → switch on (disposition, action, server),
      no `Func<ValueTask>` closures.
- [ ] Update `PacketFlowClassifier`, executors, coordinators, tests' fakes
      (compile-driven; tests keep semantics).
- Gate: build + tests; bench: dispatcher.warmPass alloc ≤ 64 B/op,
  ns/op improves vs baseline 1268.

## Step 3 — Zero-copy dispatch & in-place pass (Change B)

- [ ] `CapturePacketProcessor`: parse/classify/dispatch directly on native
      `GetFrame()` span; drop the unconditional `ArrayPool` rent/copy; lease =
      native-buffer lease (bookkeeping only).
- [ ] `NdisPacketActionExecutor.PassAsync`: in-place reinjection path — take
      the capture `NdisPacketBuffer`, set enumeration `AdapterHandle`, send in
      captured direction; keep pooled-copy path for modified frames.
- [ ] Thread the capture buffer (or a small native-lease struct) through
      `CapturedFlowPacket` so the executor can reach it without new abstractions.
- [ ] Document + test the pump contract: no batch-slot reuse before handler
      completion (`NdisCapturePumpTests` extension).
- Gate: build + tests (incl. pump ordering tests); bench: capturePump
  allocatedBytesPerOperation ≤ 64 on all frame sizes; steady-state pps ≥
  baseline.

## Step 4 — FlowTable & SOCKS5 codec (Change C)

- [ ] Verify flow-table resolve cost with raw keys; only micro-optimize
      (manual `FlowKey.GetHashCode/Equals`) if still > 300 ns @65k.
- [ ] `RemoveExpired`: drop LINQ, manual sweep.
- [ ] `Socks5UdpCodec.TryEncode(span)` overload; UDP relay send path uses it
      with pooled buffers; decode path raw addresses for the reinjector.
- Gate: build + tests; bench: flowTable 65k ≤ 300 ns/op; socks5Udp.encode
  0 B/op for ≤1514 B payloads.

## Step 5 — Full verification & report

- [ ] `dotnet test` full suite.
- [ ] Full benchmark run → `bench-after-linux.json`; write comparison table
      (before/after per scenario) into `report.md` in the task dir.
- [ ] Optional (if user wants): deploy to Windows test machine via
      `evil-winrm-py -i 192.168.100.2 -u neko -p "$NEKO_PASS"` and smoke-test
      pass-through under real NDIS.
- [ ] Spec update (trellis-update-spec): capture the hot-path conventions
      (raw addresses, native lease lifetime contract) into
      `.trellis/spec/backend/`.

## Rollback points

- After each step: `git add -A && git commit` is NOT automatic — user commits.
  Steps are ordered to be independently compilable; revert = `git checkout --
  <files>` of the step's file set.
- Baseline benchmark JSON retained for before/after even if a step is rolled
  back.

## Review gates

- Step 3 (lifetime change) needs a careful self-review of every frame consumer
  before proceeding: executor, TcpProxyCoordinator.HandlePacketAsync,
  UdpProxyCoordinator.TrySendAsync, reverse handler, ClientResetInjector —
  none may retain the native span past the synchronous dispatch.
