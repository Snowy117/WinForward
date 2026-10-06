# AOT instruction-set floor and a framework-dependent release artifact

## Goal

Stop the Native AOT release build from silently deleting the product's vectorized packet code, and
give operators a second release artifact that trades a runtime prerequisite for the JIT's full
steady-state performance and a much smaller download.

## Background (repository evidence)

### The release artifact and how it is built

`.github/workflows/aot-build.yml:27` publishes `src/WinForward.Cli/WinForward.Cli.csproj` for
`win-x64` on `windows-latest`. `WinForward.Cli.csproj:6-9` sets `PublishAot=true`,
`PublishSingleFile=true`, `RuntimeIdentifier=win-x64` and `InvariantGlobalization=true`, so that
command produces a **single native executable** with no managed-runtime dependency, uploaded as the
`WinForward-win-x64` artifact (`:30-33`). The documented output is exactly that (`README.md:29`), and
the documented platform floor is "Windows 10 22H2 x64, Windows 11 x64, or Windows Server 2022 or
later x64" (`README.md:11`).

### The defect: the AOT build silently compiles out the vector paths

Native AOT folds `Vector256.IsHardwareAccelerated` and friends into **compile-time constants** for
whatever instruction set ILC targets, and nothing in the repository sets `IlcInstructionSet`.
Measured on a CPU with AVX-512, in the same process image:

| capability | JIT | Native AOT (default) |
| --- | --- | --- |
| `Vector256.IsHardwareAccelerated` | true | **false** |
| `Avx2.IsSupported` | true | **false** |
| `Avx512F.IsSupported` | true | **false** |

Setting `-p:IlcInstructionSet=avx2` yields `Vector256=true, Avx2=true, Avx512F=false`; `native` and
`avx512` yield `Vector512=true` as well.

`PacketChecksums.cs` is the **only** file in `src/` that is ISA-gated, and its vector paths are the
ones that disappear. It is not a cold path:

- `TcpFrameRewriter.cs:25,27,41` → `PacketChecksums.TryRewriteTcpEndpoints`, per rewritten TCP packet
  on both legs.
- `UdpFrameBuilder.cs:120,124,142` → per rebuilt UDP response.
- `TcpResetBuilder.cs:110,114,131` → per injected reset.

Measured against the production entry point (`PacketChecksums.InternetChecksum`, 1514 bytes,
20M iterations, warmed past tier-0):

| build | ns/op | vs JIT |
| --- | --- | --- |
| JIT | 64.3 | 1.0x |
| Native AOT, default ISA | **960.7** | **15.0x slower** |
| Native AOT, `IlcInstructionSet=avx2` | 186.7 | 2.9x slower |
| Native AOT, `IlcInstructionSet=native` | 181.9 | 2.8x slower |

All four produce the identical checksum, so this is code quality, not a correctness difference. The
`avx2` floor recovers **5.1x** on this path; the residual 2.9x is the JIT's dynamic PGO plus its use
of AVX-512 on this host.

**This invalidates a recorded decision.** `ChecksumBenchmarks.cs:9-14` states that the vectorized
version "lands in production only if it clears a >20 % win here" — that benchmark runs under the JIT,
where the vector path exists and wins 0.74x. The shipped AOT artifact never executes it.

### AOT versus JIT, measured (same benchmark code, BenchmarkDotNet 0.15.8, linux-x64)

| benchmark (production hot path) | JIT | AOT | ratio |
| --- | --- | --- | --- |
| FlowTable same-orientation hit | 39.8 ns | 83.6 ns | 2.10x |
| FlowTable reverse-alias hit | 53.4 ns | 115.0 ns | 2.15x |
| FlowTable warm hit | 24.8 ns | 65.2 ns | 2.63x |
| **FlowDispatcher warm proxy path** (async, production) | 152.7 ns | 289.1 ns | **1.90x** |
| FlowDispatcher warm pass path | 143.6 ns | 229.1 ns | 1.60x |
| FrameRewriter forward-leg rewrite | 29.4 ns | 36.4 ns | 1.24x |
| Checksum 1514 B | 49.6 ns | 180.7 ns | 3.65x |

Allocation is **identical** in every pair (for example 160 B on both dispatcher variants, `-` on the
zero-allocation paths), so the gap is code generation only. Only `PacketChecksums` is ISA-gated;
`FlowTable`, `FlowDispatcher` and `FrameRewriter` contain no ISA branch at all, so their 1.2-2.6x gap
is the JIT's dynamic PGO and tiered re-optimization, which a statically compiled image cannot
reproduce.

Adjacent measurements from the same session, for the artifact trade-off:

| axis | JIT (framework-dependent) | Native AOT |
| --- | --- | --- |
| cold process start, median of 15 | 184.4 ms | **11.5 ms** |
| resident set, idle process | 33.5 MiB | **7.4 MiB** |
| resident set, 128 MiB resident working set | 163.0 MiB | **136.2 MiB** |
| app's own files | **1.73 MiB** (measured; 1,817,689 B) | ≈5.2 MB (reported by CI, not measured here) |
| runtime prerequisite | .NET 10 runtime (79 MB shared install) | none |

The ~26 MiB and ~173 ms differences are fixed per-process overhead; the marginal cost of the
workload is identical on both.

### Constraint that makes the floor a real decision

AVX2 is **not implied** by the documented platform floor: `README.md:11` admits Windows 10 22H2,
which runs on pre-2013 CPUs, and virtual machines frequently mask AVX2 regardless of the host. An
image compiled for AVX2 executes illegal instructions on such a machine, which is a **startup
crash**, not a graceful fallback — no runtime guard can help, because the ISA check itself is folded
into the image.

## Requirements

- **R1 — the AOT artifact's instruction-set posture must be explicit and deliberate.** The
  vectorized branch in `PacketChecksums.Sum` never executes in the shipped AOT binary today, and
  nothing in the repository records that as a choice.
- **R2 — pin the AOT instruction set instead of relying on ILC's default**, with the consequence of
  raising it written down where the next reader will find it. The default floor is kept: raising it
  to `avx2` recovers only the 128-versus-256-bit gap once R7 is in place, not the 15x it buys against
  today's disabled-vector code (the probe harness puts that gap at 1.8x, the recorded clean harness at
  4.0x; see `benchmarks/results/2026-10-06-aot-instruction-set/`) — but it makes the executable require AVX2 while `README.md:11` promises Windows 10 22H2, which admits pre-2013 CPUs, and
  virtual machines commonly mask AVX2 behind an EVC baseline. The failure mode is a **startup
  crash**, not a graceful fallback, and no runtime guard can help because every ISA check in the
  image is folded to a constant at compile time. `native` and `avx512` are rejected as well: they
  bake the CI runner's capabilities into the artifact and would change silently when the runner
  hardware generation changes.
- **R3 — add a framework-dependent release artifact to CI**, published alongside the AOT one and
  documented as requiring the .NET 10 runtime. This is the artifact that carries the performance
  story: the JIT dispatches on the ISA at run time and applies dynamic PGO, so it is 1.6-2.6x faster
  on the dispatch path and 15x faster on the checksum, in a 1.73 MiB single file rather than a
  5.19 MB one, at the cost of the runtime prerequisite.
- **R4 — the vectorization stays; only its expression changes.** It was proposed that the intrinsics be deleted in favour of
  plain scalar code, on the theory that the JIT auto-vectorizes to AVX-512 and ILC vectorizes
  according to the compile options. Both halves are false for this loop: the plain forms are
  **16-20x slower** than the hand-written vector form under the JIT (794 ns and 964 ns against
  48 ns) and are **unaffected** by the AOT instruction-set floor (798 ns at `avx2` against 771 ns at
  the default), so the floor would not recover what deleting them loses. The loop defeats
  auto-vectorization on two counts — the big-endian byte-swap and the one's-complement fold's
  end-around carry. What does change is **how** the vector path is written: R7 replaces the
  hard-coded `Vector256` with `Vector<T>`, which is measured better in every build.
- **R5 — the AOT artifact keeps its properties**: single file, no managed-runtime dependency, and
  the measured startup and memory advantages (11.5 ms against 184.4 ms cold start; 7.4 MiB against
  33.5 MiB idle resident set).
- **R6 — `README.md` states the truth about both artifacts**: what each is, what it requires, which
  to pick, and that the framework-dependent build needs the runtime installed *before* first run.
- **R7 — the checksum's vector path must survive every build, so it is written against
  `Vector<T>` rather than a hard-coded `Vector256`.** A hard-coded `Vector256` is gated on
  `Vector256.IsHardwareAccelerated`, which the AOT compiler folds to `false` at the `base`
  instruction set — so the portable artifact runs no vector code at all. `Vector<T>` takes its width
  from the build instead of failing the gate: 128-bit under `base`, 256-bit under `avx2`, and the
  JIT's host-chosen width (256-bit on this AVX-512 host — `Vector<T>` is capped at 256 by default
  and does **not** reach 512). Measured on the same 1514-byte input, all four implementations
  producing the identical checksum, with `Vector<byte>.Count` shown for each build:

  | implementation | JIT (32) | AOT `base` (16) | AOT `avx2` (32) |
  | --- | --- | --- | --- |
  | plain, fold every word | 1275.4 | 969.6 | 1278.0 |
  | plain, fold every 4096 words | 795.5 | 760.6 | 793.6 |
  | hard-coded `Vector256` | 45.2 | **974.7** (gate closed, scalar) | 186.9 |
  | **`Vector<T>`** | **48.3** | **85.7** | **47.4** |

  Absolute times differ between harnesses (delegate overhead, driver, code layout), so only within-harness ratios are claimed; `benchmarks/results/2026-10-06-aot-instruction-set/` carries the recorded runs, where the clean harness puts the `base`-versus-`avx2` gap at 4.0x for 1514 B rather than the 1.8x this table's harness shows.

  `Vector<T>` is therefore strictly better in every build: a tie under the JIT, **11.4x** on the
  portable artifact, and **3.9x** under `avx2` (its byte swap is two shifts, expressible in SSE2,
  where `Vector256.Shuffle` needs SSSE3). It also makes the vector path *live* in the shipped
  artifact instead of dead code, which is R1's actual complaint.

  **Plus a `Vector512` tier on top, gated on `Vector512.IsHardwareAccelerated`.** `Vector<T>` is
  capped at 256 bits by default, and the two ways up are not equivalent:

  | route to 512-bit code | `Vector<byte>.Count` | checksum under the JIT |
  | --- | --- | --- |
  | default | 32 | 48.9 ns |
  | `DOTNET_MaxVectorTBitWidth=512` (env var only) | 64 | 33.7 ns |
  | explicit `Vector512<T>` tier | 64 (in that tier) | **34.3 ns** |

  The explicit tier matches the knob without it, and it is *safer*: `Vector512.IsHardwareAccelerated`
  already encodes .NET's own judgement, including reporting `false` on the Intel generations where
  512-bit code costs more in frequency and misaligned accesses than it returns. The tier costs
  nothing in the AOT builds, because the gate is folded `false` there and the `Vector<T>` loop takes
  over. The knob is rejected for a second, practical reason: it is environment-variable-only — both
  `MaxVectorTBitWidth` and `System.Runtime.Intrinsics.MaxVectorTBitWidth` in `runtimeconfig.json`
  were measured to have no effect — so shipping it would mean asking every operator to set an
  environment variable.
- **R8 — the scalar tail folds at the cadence the vector path already proves safe.** After R7 the
  tail serves only the sub-vector remainder, which is still a real path: an IPv4 header checksum is
  20 bytes, below the JIT's 32-byte `Vector<T>` width, so it is all tail. Today the tail folds to 16
  bits on **every 2-byte word** (`PacketChecksums.cs:359-363`), a loop-carried dependency chain
  whose comment justifies the cadence as overflow protection — but the vector path in the same
  method bounds the same accumulation with `foldBlockInterval = 4_096` blocks, so the per-word
  cadence is over-conservative. Measured on the same 1514-byte input (a whole-buffer scalar loop,
  which is the worst case for the tail):

  | implementation | JIT | AOT `base` | AOT `avx2` |
  | --- | --- | --- | --- |
  | fold every word (today's fallback) | 964.2-1275.4 | 969.6-1282.7 | 967.4-1278.0 |
  | fold every 4096 words, no intrinsics | 794.0-795.5 | 760.6-771.1 | 793.6-798.5 |

  **1.66x**, portable, no intrinsics, and orthogonal to R7. Note the spread on the fold-every-word
  row across runs: that implementation is the noisy one, so only the within-run ratio is quoted.
- **R9 — re-measure the decisions that were taken under the JIT.** At minimum the
  `ChecksumBenchmarks` ">20 % win" gate, whose `Scalar` column is in fact the production method and
  therefore the *vector* path under the JIT — a label that invites the reader to conclude the
  opposite of the truth.

## Acceptance Criteria

- [ ] `WinForward.Cli.csproj` sets `IlcInstructionSet` explicitly, and an AOT publish with that
      value produces the same capability profile as leaving the property unset (`Vector256`,
      `Vector512`, `Avx2`, `Avx512F` all false).
- [ ] The property carries a comment naming the failure mode of raising it and pointing at where the
      cost is priced.
- [ ] A framework-dependent publish of the CLI for `win-x64` succeeds, and its `publish/` contains
      one `WinForward.exe` of roughly 1.73 MiB with no `WinForward.dll` and no runtime files.
- [ ] CI uploads both artifacts from one workflow run.
- [ ] `README.md` presents both artifacts with their prerequisites, and says the framework-dependent
      build requires the .NET 10 runtime to be installed first.
- [ ] `PacketChecksums.InternetChecksum` is unchanged in value for every input length from 0 to
      1600 against the independent reference in `tests/WinForward.TestSupport/ChecksumMath.cs`.
- [ ] Every implementation produces the identical checksum to the reference for every input length
      from 0 to 1600, including odd lengths and lengths below one vector.
- [ ] A `Vector512` tier gated on `Vector512.IsHardwareAccelerated` is present, is reached under a
      JIT on AVX-512 hardware, and measures near 34 ns rather than 49 ns on the 1514-byte input.
- [ ] No configuration knob, environment variable or `runtimeconfig.json` entry is required for any
      of the three tiers.
- [ ] In an AOT build at the `base` instruction set, `PacketChecksums` reaches roughly 85 ns on the
      1514-byte benchmark input rather than the ~970 ns today's closed gate produces, i.e. the
      `Vector<T>` tier is what runs there.
- [ ] The scalar tail measures near 771 ns rather than 1282 ns on the whole-buffer worst case, and
      the benchmark results are recorded under `benchmarks/results/`.
- [ ] `ChecksumBenchmarks` distinguishes the vector path from the scalar fallback by name, and its
      stated gate is re-measured against that comparison.
- [ ] `dotnet build WinForward.slnx -c Release` is zero-warning, `dotnet test WinForward.slnx -c
      Release` is green, and both pre-commit gates are clean.

## Decisions taken

- **Q1 resolved** — the AOT instruction-set floor stays at the default; the framework-dependent
  artifact carries the performance story (R2, R3).
- **Q2 resolved** — the fold-cadence change is in scope for this task (R7), because it is the direct
  mitigation for the cost R2 accepts, and it is cheap to verify against an existing independent
  oracle. Its blast radius is the reason it is called out separately in `implement.md` §2.
- **Q3 resolved** — the checksum is rewritten against `Vector<T>` (R7) rather than keeping the
  hard-coded `Vector256`. Measured strictly better in all three builds, and it removes the
  dead-code-in-the-shipped-artifact problem that R1 exists to fix.

## Out of scope

- A baseline-plus-AVX2 pair of AOT artifacts, or a launcher that picks between them.
- The AOT artifact's PGO-shaped throughput gap on the dispatch, flow-table and frame-rewriter paths
  (1.6-2.6x). It is the JIT's dynamic PGO, which a statically compiled image cannot reproduce, and
  none of those files contains an ISA branch, so R2 has no bearing on it. R3's artifact is the
  answer for anyone who needs that throughput; recorded in `design.md` §6 so a later benchmark run
  does not re-open it as a defect.
- Any change to the logging or configuration surface.
- Replacing Native AOT with ReadyToRun or a self-contained trimmed JIT artifact.
