# 2026-10-06 AOT instruction set — what the checksum costs in each build

Task `10-06-aot-isa-fdd` (R7, R8, R9). This directory decides two things and records one correction:

1. whether the shipped Native AOT artifact, compiled at `IlcInstructionSet=base`, still has a
   vectorized checksum (before this task it did not — the `Vector256.IsHardwareAccelerated` gate is
   folded `false` at `base`, so the artifact ran scalar code);
2. whether the scalar fallback's fold cadence was costing more than it had to.

**The correction, first, because it changes how to read every number below.** The obvious way to
measure this is BenchmarkDotNet's `NativeAotRuntime` job, which is what
`bdn-checksum-jit-vs-nativeaot.md` is. That column is **not the shipped configuration**: BDN sets
`IlcInstructionSet` itself (its assembly carries `IlcInstructionSet`/`GetCurrentInstructionSet` and
its generated project is passed the value; the CLI exposes `--ilCompilerVersion` and `--ilcPackages`
but no way to override the instruction set), so it compiles for the host's capabilities. On this
AVX-512 host that means the BDN `NativeAOT` column ran the **512-bit** tier, which is why it lands
within 9 % of the JIT column instead of the several-fold gap a `base` build shows. Read it as "AOT
when allowed the host's ISA", never as the artifact the release job uploads.

## The production entry point in each configuration

Direct call from a loop, no delegate in the loop, 20 M warm iterations then 20 M measured, 1514-byte
input matching the packet-rebuild shape. (An earlier probe wrapped the call in a
`Func<ReadOnlySpan<byte>, ushort>`, which measured the delegate and inflated everything — that is why
these numbers are lower than the probe's and why only same-harness ratios are quoted anywhere.)

| input | JIT (the framework-dependent artifact) | **AOT `base` (the shipped artifact)** | AOT `avx2` |
| --- | --- | --- | --- |
| 20 B (IPv4 header) | 13.40 ns | **8.44 ns** | 13.05 ns |
| 512 B | 17.34 ns | **71.72 ns** | 20.06 ns |
| 1514 B | 61.53 ns | **215.67 ns** | 54.45 ns |

**The defect and its fix, same harness, same input, code the only difference:** the previous
implementation at `base` measured **960.69 ns** at 1514 B; the three-tier implementation measures
**215.67 ns**. That is the number this task exists for — **4.5x on a per-packet path** in the artifact
that ships, with no change to the instruction-set floor and therefore no change to the CPUs the
executable runs on.

The 20-byte row is the opposite trade and worth keeping: at `base` the 128-bit `Vector<T>` tier fits
inside an IPv4 header, so it is *faster* than both the 256-bit and the JIT builds, where 20 bytes is
below the vector width and falls entirely to the scalar tail.

## What the vector path is worth, statistically

From `bdn-checksum-jit-vs-nativeaot.md` (BDN 0.15.8, `--job medium`, 15 iterations x 2 launches).
The baseline is now the scalar form on the same 4 096-word fold cadence, so the ratio measures
vectorization rather than fold frequency, and the vectorized side is the production entry point
rather than a copy of it:

| FrameBytes | JIT scalar | JIT production | ratio | AOT scalar | AOT production | ratio |
| --- | --- | --- | --- | --- | --- | --- |
| 20 | 10.26 ns | 11.33 ns | 1.10 (slower) | 10.70 ns | 13.19 ns | 1.29 (slower) |
| 64 | 32.30 ns | 4.64 ns | **0.14** | 32.45 ns | 6.48 ns | **0.20** |
| 512 | 272.61 ns | 11.08 ns | **0.04** | 273.02 ns | 13.07 ns | **0.05** |
| 1514 | 781.62 ns | 45.52 ns | **0.06** | 776.85 ns | 49.42 ns | **0.06** |

Allocation is `-` (zero) in every cell, which is the standing `hot-path.md` contract for this path.

Two things to take from this table. The gate the vector path has to clear — "lands in production
only if it clears a >20 % win" — is cleared by 86-96 % at every size where a vector fits, in both
runtimes. Allocation is zero in every cell, which is the standing `hot-path.md` contract.

The 20-byte row carries no vector signal and should not be read as one. In **both** BDN columns
20 bytes is below the vector width — the JIT's 512-bit tier needs 64 bytes and the default 256-bit
`Vector<T>` needs 32 — so neither side runs a tier and both cells are the scalar tail, which is what
the paragraph above the table already says. The 1.10 and 1.29 are differences between two
near-identical scalar loops on a ~10 ns floor, and the 1.29 in particular is BDN's ratio against the
**JIT** baseline rather than the AOT's own scalar (13.192 / 10.698 = 1.23). The build that ships is
the one in the sweep above, where a `base` image's 128-bit tier does fit inside an IPv4 header and
20 bytes measures **8.44 ns** — faster than either BDN cell.

## Why the harness-to-harness spread exists, and what is claimed

Absolute times for the same code differ by 1.5-4x between the three harnesses used here (this
directory's sweeps, the BDN run, and the throwaway probes in
`.trellis/tasks/10-06-aot-isa-fdd/research/aot-vs-jit-measurement.md`). The causes are identified:
delegate invocation in the loop, whether the benchmark runner or a hand loop drives the call, and
code layout. **Only ratios measured inside one harness are claimed**; the earlier design note quoting
"1.8x" as the `base`-versus-`avx2` gap came from the delegate-wrapped probe and should be read as
"the same order" — the clean harness puts it at 4.0x for 1514 B, and the direction is what the
`IlcInstructionSet=base` decision was made on.

## Provenance of the numbers in this directory

| figure | source |
| --- | --- |
| the two BDN tables | `bdn-checksum-jit-vs-nativeaot.{md,csv}` in this directory, byte-identical to the runner's own artifacts |
| the per-configuration sweep above | `production-entry-point-sweep.txt` in this directory, raw stderr of the harness |
| the "≈5.2 MB" AOT artifact size quoted in `README.md` and the task's PRD | **not measured here.** It is the size CI reports for `WinForward-win-x64`; cross-OS AOT is unsupported, and a linux-x64 AOT build of this Windows-only CLI is vacuously trimmed. Treat it as reported, not verified |
| the framework-dependent artifact's size | measured repeatedly here and by the checker: one `WinForward.exe` of 1,817,689 bytes (1.73 MiB) plus symbol files, no `WinForward.dll`, no runtime files |

## Reproducing

```
# BDN, both runtimes. Note the AOT column is host-ISA, not base (see above).
dotnet run -c Release --project benchmarks/WinForward.Benchmarks --no-build -- \
  --filter '*ChecksumBenchmarks*' --runtimes net10.0 nativeaot10.0 --job medium

# The shipped configuration, per ISA floor (needs clang; cross-OS AOT is unsupported, so this is
# linux-x64 and the input/output shapes are platform-neutral on purpose).
dotnet publish <harness> -c Release -r linux-x64 -p:PublishAot=true -p:StripSymbols=true -p:IlcInstructionSet=base
```

The CLI's own `win-x64` artifact cannot be measured here at all: cross-OS AOT is unsupported, and a
`linux-x64` AOT build of it is vacuously trimmed — ILLink folds `OperatingSystem.IsWindows()` and
removes the product code, verified once by checking that the published `WinForward.dll` referenced
neither `LoggerFactory` nor `NdisApiDriver`. CI is where the real artifact is built.
