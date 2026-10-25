# Batch 5c-1 implementation plan

1. **Baseline.** Read `TcpProxyRelayFactory.cs`, `TcpRedirectListener.cs` (the internal accepted
   connection), `TcpProxyRelay.cs` (end kind), `TcpRedirectAcceptor.cs`, and the existing
   `WinForward.Runtime.TcpRedirect.Tests` plus `Socks5FakeUpstream`. Record the test count.
2. **Factory overload.** Add the public Socket overload as in the design; validate the remote
   endpoint, construct the internal accepted connection, delegate to the existing method. Keep the
   concrete-type check on the existing method and all packet-path behavior.
3. **End kind.** Make `RelayEndKind` and `ITcpRelayEndInfo` public; keep the `TcpProxyRelay`
   implementation and the acceptor unchanged. Document "meaningful after Completion" and the
   linger-0 consumer contract. Do not add timeouts/state.
4. **Tests.** Add a clean raw-socket relay test against the fake SOCKS5 upstream (both directions,
   clean end kind, socket disposed once) and a fault/end-kind test using the existing fault shape.
   Do not fake the 30-minute stall.
5. **Gates.** `TcpRedirect.Tests`, root build/test/format, Kernel fast gate; project-level jb for
   Runtime if feasible; full-solution jb remains an environment gap.
6. **Do not touch** the Kernel backend/device/listener/accept/redirect-context, host TCP/UDP,
   forwarded, ABI/contracts/driver/NdisApi, or unrelated dirty files. No commit.

## Report

API/tests changed, the raw-overload behavior, end-kind evidence, gates, any gap; no commit.
