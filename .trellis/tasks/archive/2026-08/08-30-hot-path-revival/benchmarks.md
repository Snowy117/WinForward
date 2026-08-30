# Dispatcher benchmark record — X1 evidence (2026-08-30, Linux dev box)

BenchmarkDotNet, `--filter '*Dispatcher*'`, Release. Allocation figures exact; ns carries the
±50% dev-box noise noted in hot-path contract 9.

## Pre-fix (production composition diverts every packet — the X1 reality)

| Method | Mean | Allocated |
|---|---|---|
| WarmPassDisabledTraceAsync (handler-less control) | 234.8 ns | 160 B |
| WarmProxyDisabledTraceAsync (handler-less control) | 272.4 ns | 160 B |
| WarmPassProductionAsync (reverse handler wired) | 477.5 ns | 352 B |
| WarmProxyProductionAsync (reverse handler wired) | 481.0 ns | 352 B |
| ReverseCandidateSlowPathAsync (diverted control) | 485.2 ns | 352 B |

The production variants equal the diverted control exactly (352 B ≈ 2.2× the warm 160 B
baseline; ~2× the ns): with `Program.cs` wiring a reverse handler, 100% of production packets
ran `DispatchSlowAsync` and the non-async warm entry was dead code. This is the X1 evidence
baseline; 352 B matches the pre-C2b slow-path figure from task 08-29-socks5-perf-fullpath.

## Post-fix (WantsPacket protocol gate + listener-port prefilter)

| Method | Mean | Allocated |
|---|---|---|
| WarmPassDisabledTraceAsync (handler-less control) | 229.8 ns | 160 B |
| WarmProxyDisabledTraceAsync (handler-less control) | 276.3 ns | 160 B |
| WarmPassProductionAsync (reverse handler wired) | 244.1 ns | **160 B** |
| WarmProxyProductionAsync (reverse handler wired) | 251.5 ns | **160 B** |
| ReverseCandidateSlowPathAsync (diverted control) | 476.6 ns | 352 B |

The production variants now equal the warm 160 B baseline exactly (AC4): UDP pass and
non-candidate TCP proxy decisions ride the non-async warm entry in the production composition
(352 B → 160 B, −55%; 477/481 → 244/252 ns, ~2×), while true candidates (claimed listener
source port) keep the diverted slow-path figure — reverse routing is unchanged. The prefilter
cost (protocol gate + Volatile port-array probe) is within noise of the handler-less shape.
