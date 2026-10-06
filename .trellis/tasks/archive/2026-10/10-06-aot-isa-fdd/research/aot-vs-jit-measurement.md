# Research — how the AOT/JIT numbers were produced

Every number quoted in `prd.md` and `design.md` came from one of the five procedures below. They are
recorded so that the measurement can be repeated — on this host, or on the Windows CI host where the
shipped artifact actually exists.

Host for all of them: Linux x64, AMD Ryzen 9 9955HX (AVX-512 capable), .NET SDK 10.0.401, runtime
10.0.12. A Native AOT toolchain is not installed system-wide; `clang` 21.1.8 and `lld` came from
`nix build --no-link --print-out-paths nixpkgs#clang nixpkgs#lld`, prepended to `PATH`.

## 1. BenchmarkDotNet, JIT against Native AOT, on the repository's own benchmarks

```
dotnet run -c Release --project benchmarks/WinForward.Benchmarks --no-build -- \
  --filter '*FlowTableProductionShapeBenchmarks*' '*ParserBenchmarks*' '*FrameRewriterBenchmarks*' \
           '*QuiescenceScopeBenchmarks*' '*DispatcherBenchmarks*' \
  --runtimes net10.0 nativeaot10.0 --job medium
```

Findings that made this work, none of them obvious:

- The runtime classes live in `BenchmarkDotNet.Environments`, not `BenchmarkDotNet.Jobs`
  (`NativeAotRuntime.Net10_0`, `CoreRuntime.Core10_0`). There is no `RuntimeMoniker` type in 0.15.8.
- `--filter` may be given **once**, with several globs after it. Repeating the flag fails with
  `Option 'f, filter' is defined multiple times` and BDN prints usage instead of running anything.
- BDN adds `Microsoft.DotNet.ILCompiler` to the project it generates, so **no repository file needs
  to change** to get an AOT job.
- Reports land in `BenchmarkDotNet.Artifacts/results/` in the current directory. That directory is
  gitignored and holds reports from earlier sessions, so a report is only trustworthy if it contains
  a `NativeAOT 10.0` row — check before quoting one.
- `--job short` gives error bars of the same order as the means (one 1514-byte checksum reading was
  49.6 ± 34.3 ns). Use `medium` for anything that will be quoted.
- The final class of the run hit a 50-minute timeout. Four classes completed with usable tables.

## 2. What Native AOT actually compiles for

A four-line probe printing `Vector256.IsHardwareAccelerated`, `Vector512.IsHardwareAccelerated`,
`Avx2.IsSupported`, `Avx512F.IsSupported`, published twice:

```
dotnet publish -c Release -r linux-x64 -p:PublishAot=true -p:StripSymbols=true
dotnet publish -c Release -r linux-x64 -p:PublishAot=true -p:StripSymbols=true -p:IlcInstructionSet=avx2
```

| capability | JIT | AOT default | AOT `avx2` | AOT `native` / `avx512` | AOT `base` |
| --- | --- | --- | --- | --- | --- |
| `Vector256.IsHardwareAccelerated` | true | **false** | true | true | false |
| `Vector512.IsHardwareAccelerated` | true | **false** | false | true | false |
| `Avx2.IsSupported` | true | **false** | true | true | false |
| `Avx512F.IsSupported` | true | **false** | false | true | false |

`IlcInstructionSet=base` reproduces the implicit default exactly, which is what makes pinning it a
behaviour-preserving change.

Valid values come from `ilc --help`, not from guessing — the targets file only forwards
`--instruction-set:$(IlcInstructionSet)`. For x64 they are
`base, sse4.2, avx, avx2, avx512, avx512v2, avx512v3, avx10v1, avx10v2, apx, aes, …`. **`x64` is not
one of them**; using it fails the build with `Unrecognized instruction set x64`.

Environment variables reach ILC (`IlcInstructionSet=avx2 dotnet publish …` works), because MSBuild
imports them as properties. That did **not** help inside a BenchmarkDotNet run — the AOT column's
`VectorCandidate` stayed equal to `Scalar`, i.e. the generated project did not pick it up.

## 3. The four-implementation checksum matrix

One project, no package references, 1514-byte input, 20M warm iterations then 20M measured, all four
implementations printed so an equivalence break cannot be missed. Measured under `dotnet` (JIT), and
as an AOT single file at the default and at `avx2`:

| implementation | JIT | AOT default | AOT `avx2` |
| --- | --- | --- | --- |
| fold every word (today's fallback) | 964.2 | 1282.7 | 967.4 |
| fold every 4096 words, no intrinsics | 794.0 | 771.1 | 798.5 |
| hand-written `Vector256` | **48.0** | 1294.3 | **184.8** |

All three checksum values and all four sink accumulators agreed, so the comparison is code quality
only. Two caveats: the same code path reads 960.7 ns in the harness of §4, a ~30 % harness
sensitivity, so only within-harness comparisons are used; and AOT publishes of a project that
`ProjectReference`s this repository need `--no-build` after a normal `dotnet build`, because
`PublishAot` propagates to the netstandard2.0 analyzer project and fails it with `NETSDK1207`.

## 4. The production checksum entry point

A three-line harness calling `PacketChecksums.InternetChecksum` directly through a `ProjectReference`
to `src/WinForward.Protocols`, 20M warm iterations, printing ns/op and the checksum:

| build | ns/op |
| --- | --- |
| JIT | 64.3 |
| AOT default | 960.7 |
| AOT `avx2` | 186.7 |
| AOT `native` | 181.9 |

## 5. Memory, startup and artifact size

A probe with the same dependency shape as the CLI (MEL + console + `Configuration.Json`), two modes —
`idle`, and `work` holding 512 × 256 KiB with **every fourth kilobyte touched** so the working set is
genuinely resident. The first version touched only one byte per buffer and read 37 MiB of RSS against
a 128 MiB GC heap, which is not a defensible pair of numbers; that is why the page touch is there.

| axis | JIT | AOT |
| --- | --- | --- |
| idle peak RSS | 33.5 MiB | 7.4 MiB |
| work peak RSS (128.2 against 128.4 MiB GC heap) | 163.0 MiB | 136.2 MiB |
| cold start, median of 15 | 184.4 ms | 11.5 ms |

Sizes: the framework-dependent publish of the real CLI is a single `WinForward.exe` of 1,817,689
bytes (1.73 MiB), plus symbol files, and contains no `WinForward.dll` and no runtime files. The
shared runtime it needs is a 79 MB install that belongs to no artifact. For comparison, the same
probe was 20 KB framework-dependent against 2370 KB AOT with no MEL at all, 668 KB against 3144 KB
with MEL, and 848 KB against 3618 KB with the configuration-file layer added.

## What could not be measured here

The shipped artifact. Cross-OS AOT is unsupported, so a `win-x64` AOT publish cannot be produced on
Linux; and a `linux-x64` AOT publish of this CLI is worthless as a size or speed measurement because
ILLink folds `OperatingSystem.IsWindows()` and deletes the Windows-only product code — verified once
by checking that the published `WinForward.dll` referenced neither `LoggerFactory` nor
`NdisApiDriver`. Every AOT number above therefore comes from platform-neutral probes compiled with
the same compiler and settings, and is used for relative comparison. CI is where the real artifact
gets measured for the first time.

## 6. The vector width question: `Vector<T>` and the 512-bit tiers

Prompted by a proposal to replace the hand-written `Vector256` with `Vector<T>`. Same harness as §3,
extended with `Vector<T>` and, later, a `Vector512` tier; every implementation printed so an
equivalence break cannot be missed. `Vector<byte>.Count` is reported because it is the whole point:

| implementation | JIT (Count 32) | AOT `base` (16) | AOT `avx2` (32) |
| --- | --- | --- | --- |
| plain, fold every word | 959.96 | 969.63 | 1277.96 |
| plain, fold every 4096 words | 799.64 | 760.60 | 793.63 |
| hard-coded `Vector256` | 46.57 | 974.68 | 186.94 |
| `Vector<T>` | 48.93 | **85.70** | **47.35** |
| `Vector512` tier | **34.32** | gate false → `Vector<T>` | gate false → `Vector<T>` |

The mechanism is a one-line difference: a hard-coded `Vector256` is gated on
`Vector256.IsHardwareAccelerated`, which the AOT compiler folds to `false` at `base`, so the loop is
skipped and scalar code runs. `Vector<T>` has no gate to fail — its width comes from the build — so
the portable artifact keeps a 128-bit SSE2 path. Two implementation notes that cost time to find:
the byte swap must be `(word >> 8) | (word << 8)` on a `Vector<ushort>`, because `Vector256.Shuffle`
needs SSSE3 which `base` does not have; and `Vector.Widen` for `Vector<T>` takes `out` parameters and
cannot be deconstructed, while the `Vector512` overload returns a tuple. `Vector<T>` lives in
`System.Numerics`, not `System.Runtime.Intrinsics`.

### Raising the cap

`Vector<T>` stops at 256 bits by design. `DOTNET_PreferredVectorBitWidth` is **not** the knob that
raises it — it only ever lowers the width:

| setting | `Vector<byte>.Count` | `Vector<T>` checksum |
| --- | --- | --- |
| default | 32 | 46.75 |
| `DOTNET_PreferredVectorBitWidth=512` | 32 (no effect) | 46.40 |
| `DOTNET_PreferredVectorBitWidth=128` | 16 | 93.75 |
| `DOTNET_MaxVectorTBitWidth=512` | 64 | **33.66** |

The cap is an upper bound, not a requirement: masking the ISA lowers the width below the cap
(`DOTNET_EnableAVX2=0` → 16, `DOTNET_EnableAVX512=0` → 32). One masking result is worth not
over-reading: `DOTNET_EnableAVX512F=0` alone left the width at 64, because that single flag is not
the umbrella for the whole AVX-512 family — it is an artifact of the diagnostic switch, not evidence
that the cap overrides the CPU.

`MaxVectorTBitWidth` is **environment-variable-only**. Both `"MaxVectorTBitWidth": "512"` and
`"System.Runtime.Intrinsics.MaxVectorTBitWidth": "512"` in `runtimeconfig.json` were measured to have
no effect on `Vector<byte>.Count`. That is why the design ships an explicit `Vector512<T>` tier
instead: it reaches the same 34.32 ns against the knob's 33.66 ns without asking an operator to set
an environment variable, and it inherits `Vector512.IsHardwareAccelerated`, which .NET already
reports as `false` on the Intel generations where 512-bit code costs more than it returns.
