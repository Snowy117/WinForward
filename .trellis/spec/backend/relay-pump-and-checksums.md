# Relay Pump and Checksums: the byte pipe and its arithmetic

> The TCP relay's per-direction pump loop and the checksum primitive it feeds. Part of the
> [hot-path family](./hot-path.md); read it when you change `TcpProxyRelay`, `StallWindow` or
> `PacketChecksums`.

## The pump loop

- `TcpProxyRelay` owns one reusable `StallWindow` (a linked CTS) **per direction per session**.
  `Arm()` runs before every `ReadAsync`/`WriteAsync`, throttled to at most once per second (task
  08-30-fast-hardening X8a): the first arm is unconditional, later arms only when more than 1 s
  (`Stopwatch` ticks, internal static `StallWindow.IsRearmDue(lastArmTicks, nowTicks)`, strictly
  greater) has elapsed.
- `Arm()` is `TryReset()` + `CancelAfter(StallTimeout)`; the CTS is recreated **only** when
  `TryReset` returns false (raced with the timer firing), and then re-linked to the lifetime token so
  lifetime/peer cancellation still lands instantly. `TryReset` clears any pending `CancelAfter`
  timer (verified on .NET 10), so each window arms from its own `Arm()` with no residual-timer
  leakage. The throttle only skips `Arm()` re-invocations: it never disarms, never recreates the CTS
  and never breaks the lifetime link, so the armed timer stays armed and the 30-minute stall window
  drifts by at most 1 s. **Do not reintroduce per-chunk `CreateLinkedTokenSource` + `CancelAfter`.**
  Measured effect: pump allocation 160 B → 0 B per op, `TcpRelayBenchmarks.OneWayAsync` chunk-8192
  allocation −77 % (828 → 188 KB/op) with no throughput regression.
- Each pump direction rents one 64 KiB buffer from `ArrayPool<byte>.Shared` (`PumpBufferSize =
  64 * 1024`, task 08-30-fast-hardening X5) for its whole lifetime — one rent per direction
  (half-close gives the two directions independent lifetimes), returned exactly once in `finally` on
  every exit path. Loop bounds use `buffer.Length`: the pool may return a larger array than asked.
- Both relay legs set `NoDelay = true` (the listener accept and the upstream `ConnectAsync` success).
  Nagle has no upside for a byte-pipe relay and produces 40–200 ms delayed-ACK cliffs on interactive
  traffic.

## Checksum endpoint rewrite (P2a: RFC 1624 incremental)

- `PacketChecksums.TryRewriteIpv4Tcp` / `TryRewriteIpv6Tcp` are **private static** helpers behind the
  span entry point, not callable API. The internal full-recompute oracle is kept for property tests,
  under `InternalsVisibleTo` grants to **both** `WinForward.Protocols.Tests` and
  `WinForward.Integration.Tests`.
- `HC' = ~(~HC + Σ(~m + m'))` folded over only the changed words (IPv4: the address words counted in
  header + pseudo-header, and the ports; IPv6: the 16 address words + ports, no header checksum). New
  words are read back **from the frame after the write** — one encoding source. **Precondition: the
  input checksum is valid**; that is the capture pipeline's contract, and on invalid input the result
  differs from a full recompute (garbage-in-garbage-out, documented in-code). On valid input it is
  bit-identical to the full recompute: property-pinned over 512 + 512 random frames, a 65,536-port
  sweep including computed-zero, three-way against independent test helpers.
  `FrameRewriterBenchmarks.TryRewriteEndpointsDirect` micro: 626.2 → 33.4 ns @1400 B, 0 B
  (`benchmarks/results/2026-08-29-proxy-hardening/`).
- **Benchmark frames must carry valid checksums.** The incremental rewrite's precondition means
  `BenchmarkShared` frames with zeroed checksums measure an invalid-input shape; build frames with
  correct checksums before and after so the measured delta is real.

## Vectorized `Sum` (P2b) — the fold invariants are load-bearing

- Three tiers: a `Vector512` loop gated on `Vector512.IsHardwareAccelerated`, a `Vector<T>` loop
  gated on **span length alone**, and a scalar fold-while-adding tail. The `Vector<T>` tier is what
  keeps a vector path in a Native AOT build at `IlcInstructionSet=base`, where a
  `Vector256.IsHardwareAccelerated` gate is folded `false` (task 10-06-aot-isa-fdd).
- `uint` wraparound is **not** one's-complement neutral (`2^32 ≡ 1 mod 65535`), so any deferred-fold
  scheme must keep partial sums strictly bounded. Packet paths never reach the risky sizes (IP ≤
  64 KiB), but the function is **size-public** and the `ProtocolAuditTests` shape (131,076 B of 0xFF)
  is the guard — a bare accumulation already broke host-independence there once.
- Every tier **and the scalar tail** fold periodically, never only at the end. The bound is on the
  *whole reduction*, not per lane: each lane takes two words per block, so 2,048 blocks puts 4,096
  words = `0x0FFF_F000` in a lane; the 512-bit tier's sixteen lanes reduce to `0xFFFF_0000`, and with
  `sum` fully folded (≤ `0xFFFF`) first the add peaks at exactly `0xFFFF_FFFF`. Folding every 4,096
  blocks instead would put 8,192 words (`0x1FFF_E000`) in a lane and **wrap the reduction itself** —
  the 16-lane case is hard-coded safe even though no shipped knob reaches it: the environment-only
  `DOTNET_MaxVectorTBitWidth=512` does widen `Vector<T>` to 16 lanes, but where
  `Vector512.IsHardwareAccelerated` is true the 512-bit tier preempts it, and where it is false the
  runtime does not offer a 512-bit `Vector<T>`.
- The `Vector<T>` tier stays gated on span length alone, and its byte swap stays two shifts
  (`(w >> 8) | (w << 8)`). A `Vector256.IsHardwareAccelerated` gate folds to `false` at `base` and
  removes the tier outright, and a `Vector256.Shuffle`-based swap reintroduces exactly that AVX2
  gate; `Vector<T>` has no shuffle whose width stays build-chosen. (It is *not* that SSSE3 is
  missing: .NET 10's x64 baseline already includes SSE4.2 and POPCNT — `ssse3`/`sse41`/`sse42` fold
  to `true` in a `base` image, and `base` and `sse4.2` produce identical profiles.)
- The bound is proven by construction, not by measurement: `ChecksumMath` is the oracle only up to
  131,074 B of `0xFF`, above which its own unfolded `u32` accumulation saturates, so the audit test
  owns the larger shapes.
- Timings: 989 → 61.7 ns @1514 B (the 2026-08-29 `Vector256` landing, 16×). For the three-tier
  rewrite quote only the recorded clean harness, because absolute times vary 1.5–4× between
  harnesses: `benchmarks/results/2026-10-06-aot-instruction-set/` has the production entry point at
  **215.67 ns in an AOT build at `base`** (960.69 ns before the rewrite) and **61.53 ns under the
  JIT**, with the `base`-versus-`avx2` gap at 4.0×.

## What keeps this honest

- Relay: the existing half-close/sibling-cancel/fast-fail suite unchanged, plus the multi-rearm
  mid-stream failure regression `MidStreamFailureCancelsSiblingPumpAfterRepeatedStallWindowRearms`.
- Checksums: `TcpEndpointRewriteIncrementalTests` (random-frame equivalence, port sweep, three-way
  against independent helpers) and `ProtocolAuditTests`; the 131,076 B audit shape stays green on
  every host.
- Throughput acceptance anchor: `tcp.throughput` in socks5 mode must stay ≥70 % of bare mode (same
  workers, echo and transfer size; the relay leg without SOCKS5 establishment). Measured 93.9 %
  (141.4 vs 150.6 MB/s, 16 conc × 1 MiB quick). `TcpRelay OneWayAsync` (~0.94 GB/s) is the
  single-connection theoretical ceiling, **not** the ratio denominator.
- `Sum` on any size and fill, including 131,076 B of 0xFF and ≥393,216 B adversarial input, must be
  bit-identical scalar versus vector with no overflow.

```csharp
// Wrong: bare uint accumulation "folded at the end" — overflows on ≥131k B of 0xFF
// (65,538 × 65,535 > 2^32; the result is no longer a one's-complement sum).
uint acc = 0;
foreach (var w in words) acc += w;

// Correct: fold periodically. The shipped cadence is every 4,096 words, which bounds the partial
// sum at 0xFFFF + 4,096 * 0xFFFF = 0x1000_EFFF — far below 2^32.
uint acc = 0;
foreach (var w in words) { acc += w; while (acc > 0xFFFF) acc = (acc & 0xFFFF) + (acc >> 16); }
```
