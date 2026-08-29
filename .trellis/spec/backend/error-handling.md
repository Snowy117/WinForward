# Error Handling

> How errors are handled in this project: fail-closed proxy semantics, bounded exemptions, and validation diagnostics.

---

## Current Conventions

- Unknown or ambiguous process attribution does not guess an owner; process rules do not match and evaluation continues to the next rule/fallback.
- A proxy setup failure is fail-closed in release 1. Proxy-selected packets are never silently downgraded to pass.
- One bounded exemption (2026-08-14): a packet on a proxy-decided TCP flow whose SYN was never observed (the connection pre-dates capture startup) was never proxyable — there is no relay it could join retroactively. That packet is passed (`TcpRedirectOutcome.NotRelevant` → reinject), so the pre-existing connection survives instead of hanging. This is not a downgrade of an established proxy path: the flow's decision stays `proxy`, every packet is re-evaluated, and the exemption ends with the connection's lifetime (the next connection has a SYN and is proxied normally). Locked by `ExecutorPassesTcpPacketWhenCoordinatorReportsNotRelevant`.
- A failed relay setup after a successful redirect is surfaced to the client as a crafted in-window RST|ACK from the original server endpoint (`TcpResetBuilder`), not left to hang; the reset degrades to plain teardown only when the SYN or SYN-ACK sequence numbers were never observed.
- UDP relay alias collisions are rejected because sharing an alias would make reverse routing nondeterministic.
- **UDP session setup never blocks the capture pump and never downgrades to pass** (task 08-28-udp-loss-design-flaws D1): `UdpProxyCoordinator.TrySendAsync` is a non-async entry; during `SettingUp`/`Flushing` datagrams are copied into a per-flow bounded setup queue (32 packets / 32KB, drop-oldest, trace `udp.setupqueue.dropped`), and setup runs in the background under a global `SemaphoreSlim(8)` cap. A failed setup drops the queued datagrams fail-closed, removes the entry, and writes a 1s tombstone cooldown (trace `udp.setup.cooldown`) so a dead SOCKS5 server cannot be hammered at datagram rate. Ready-path sends stay inline (zero-allocation steady shape); a Ready-path send failure follows the pre-existing immediate-teardown semantics without cooldown.
- Per-caller cancellation of a shared UDP send does not tear down a session unless the shared setup/send operation itself failed.
- Concurrent proxied TCP flows are bounded by the `tcpFlowCapacity` budget (default 4096, range 1..8192, `>4096` warns). Rejection at the budget gate is explicit capacity management, not an error: fail-closed `Blocked` with trace `reason=capacity`, an `Interlocked` counter, and a periodic info summary (`tcp.redirect.capacity`); it must stay an instantaneous synchronous check on the SYN path (no network waits). The session and redirect-table capacities derive from the same configured value at the wiring point.
- `ConfigurationLoader.TryParse` maps a `JsonException` to the serializer-supplied JSON path (or `$` when absent) and a fixed diagnostic template. It must not append `JsonException.Message` or raw JSON values, because malformed input can carry credential-like data.
- `ConfigurationLoader.TryValidate` reports invalid or null configuration collection elements at their indexed field paths and returns validation diagnostics; valid JSON must not escape as a null-reference failure.

## Common Mistakes

- Keying UDP state by PID, DNS transaction ID, or only the local port mixes independent datagrams.
- Returning an existing association solely because its relay alias matches can cross-wire two original flows.

