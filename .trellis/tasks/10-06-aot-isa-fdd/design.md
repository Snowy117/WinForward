# Design — AOT instruction-set floor and a framework-dependent release artifact

## 1. Shape of the change

Five files, three independent concerns.

| File | Concern |
| --- | --- |
| `src/WinForward.Cli/WinForward.Cli.csproj` | pin the AOT instruction set explicitly (R2) |
| `src/WinForward.Protocols/PacketChecksums.cs` | fold cadence of the scalar fallback (R7) |
| `tests/WinForward.Protocols.Tests/*` | property coverage for R7 |
| `.github/workflows/aot-build.yml` | second release artifact (R3) |
| `README.md` | what each artifact is, what it needs, what it costs (R6) |

No change to the configuration surface, the CLI surface, the log surface, or any public type.

## 2. The instruction-set posture (R1, R2)

**Decision: pin `<IlcInstructionSet>base</IlcInstructionSet>` explicitly, and record why it must not
be raised.**

Today the value is unset, so ILC's default applies. Measured, `base` and the implicit default
produce an identical capability profile (`Vector256`, `Vector512`, `Avx2`, `Avx512F` all false), so
the pin is behaviour-preserving. It is worth writing down anyway for two reasons: ILC's default is
not a contract this repository controls, and a future reader who notices the 15x checksum penalty
will otherwise "fix" it by reaching for `avx2` without knowing the consequence.

The pin carries a comment stating the consequence: raising it makes the executable require that
instruction set **at process start**, with no runtime guard possible, because the ISA checks
throughout the image are folded to constants at compile time. `README.md:11` promises Windows 10
22H2, which admits pre-2013 CPUs, and virtual machines commonly mask AVX2 behind an EVC baseline.

Valid values, taken from `ilc --help` rather than guessed: for x64 they are
`base, sse4.2, avx, avx2, avx512, avx512v2, avx512v3, avx10v1, avx10v2, …`. `native` and `avx512`
are rejected because they bake the **build machine's** capabilities into the artifact; on GitHub's
runners that would also change silently whenever the runner hardware generation changes.

**Consequence accepted, and made much smaller by §4.** With the hard-coded `Vector256` the gate
closes under `base` and the artifact's checksum runs no vector code at all. §4 rewrites that path
against `Vector<T>`, which takes a 128-bit width at `base` instead of failing the gate, so the
portable artifact keeps a vectorized checksum. What remains is the gap between 128-bit at `base` and
256-bit at `avx2` — **1.8x in the probe harness and 4.0x in the recorded clean harness**, so read it
as the same order, not a single number — and that is the price of portability. Anyone who needs more
takes the framework-dependent artifact.

## 3. The framework-dependent artifact (R3, R5, R6)

**Decision: a second `dotnet publish` in the same workflow, with the runtime prerequisite documented
in `README.md`.**

The command, verified end to end on the real project (cross-published from Linux for `win-x64`,
exit 0):

```
dotnet publish src/WinForward.Cli/WinForward.Cli.csproj -c Release -r win-x64 \
  -p:PublishAot=false --self-contained false
```

`PublishSingleFile=true` comes from the project file and stays, so the artifact is still **one
`WinForward.exe`** — 1,817,689 bytes (1.73 MiB) plus symbol files — dropped next to the same
user-supplied `ndisapi.dll` sidecar the AOT artifact needs. `ServerGarbageCollection=false` and
`InvariantGlobalization=true` also carry over unchanged, so the two artifacts differ only in the
runtime question.

Note the publish needs no `--no-build` workaround, unlike the trim and AOT experiments: the
`NETSDK1207`/`NETSDK1124` failures seen during measurement came from `PublishAot`/`PublishTrimmed`
propagating to the netstandard2.0 analyzer project, and neither property is set on this path.

CI shape: keep one workflow. A second publish step plus a second `actions/upload-artifact` with the
name `WinForward-win-x64-fdd`, uploading the same `publish/` directory. Separate jobs would need the
whole restore/build again for no isolation benefit.

**Why this artifact is the performance story, and the honest limits of that claim.** The JIT
dispatches on the instruction set at run time and applies dynamic PGO, so this artifact is 1.6-2.6x
faster than the AOT one on the dispatch path. On the checksum the margin is smaller than it looks
before §4 lands — 15x against today's disabled-vector code, but only the 128-versus-256-bit gap once
`Vector<T>` keeps the
portable artifact vectorized (measured; see the tables in `prd.md`). It is also the smaller
download. What it needs in exchange is the .NET 10 runtime — a
79 MB shared install that is a prerequisite, not part of the artifact. `README.md` must say so
plainly, because a user who copies `WinForward.exe` to a machine without the runtime gets a host
error rather than a WinForward diagnostic, and an operator who never installs it should not be
steered here.

## 4. The checksum's vector path (R7, R8)

**Decision: write the vector loop against `Vector<T>` instead of a hard-coded `Vector256`, and fold
the scalar tail every 4 096 words instead of every word.**

### 4.1 `Vector<T>` replaces `Vector256` (R7)

A hard-coded `Vector256` is gated on `Vector256.IsHardwareAccelerated`, and the AOT compiler folds
that to `false` at the `base` instruction set, so the loop is skipped and the artifact runs scalar
code. `Vector<T>` has no such gate to fail: its width comes from the build — 128-bit at `base`,
256-bit at `avx2`, and the JIT's host-chosen width (256-bit on this AVX-512 host; `Vector<T>` is
capped at 256 by default and does not reach 512).

Measured on the same 1514-byte input, all four implementations producing the identical checksum,
with `Vector<byte>.Count` for each build:

| implementation | JIT (32) | AOT `base` (16) | AOT `avx2` (32) |
| --- | --- | --- | --- |
| hard-coded `Vector256` | 45.2 ns | 974.7 ns | 186.9 ns |
| **`Vector<T>`** | **48.3 ns** | **85.7 ns** | **47.4 ns** |

Absolute times differ between harnesses (delegate overhead, driver, code layout), so only within-harness ratios are claimed; `benchmarks/results/2026-10-06-aot-instruction-set/` carries the recorded runs, where the clean harness puts the `base`-versus-`avx2` gap at 4.0x for 1514 B rather than the 1.8x this table's harness shows.

**11.4x** on the portable artifact and **3.9x** under `avx2`, for a tie under the JIT. The `avx2`
win has a mechanical reason worth keeping in mind while writing the loop: the byte swap must be
expressed as `(word >> 8) | (word << 8)` on a `Vector<ushort>`, which SSE2 can do, rather than with
`Vector256.Shuffle`, whose `pshufb` needs SSSE3. Widening the swapped words to `Vector<uint>` before
accumulating uses `Vector.Widen(source, out low, out high)` — the `Vector<T>` overload takes `out`
parameters and cannot be deconstructed.

**A third tier above it, gated on `Vector512.IsHardwareAccelerated`.** `Vector<T>` stops at 256
bits by default — deliberately, because existing code takes implicit dependencies on the width and
because 512-bit code is not automatically faster (it costs frequency on some Intel generations and
makes misaligned 64-byte accesses more expensive; C compilers default to `-mprefer-vector-width=256`
for the same reason). Raising the cap needs `DOTNET_MaxVectorTBitWidth`, which is
environment-variable-only — `MaxVectorTBitWidth` and `System.Runtime.Intrinsics.MaxVectorTBitWidth`
in `runtimeconfig.json` were both measured to do nothing — so it cannot ship inside an artifact.
An explicit `Vector512<T>` tier reaches the same 34.3 ns against the knob's 33.7 ns, needs no
configuration, and inherits .NET's own gate, which reports `false` exactly on the generations where
512-bit code is a net loss. Under `base` and `avx2` that gate is folded `false` and the `Vector<T>`
loop runs, so the tier costs the AOT artifact nothing.

The resulting shape is three tiers and a tail:

```
if (Vector512.IsHardwareAccelerated)  -> 512-bit loop   (JIT on AVX-512 that .NET blesses)
else                                  -> Vector<T> loop (128-bit at base, 256-bit at avx2/JIT)
then                                  -> scalar tail with the R8 fold cadence
```

**Every tier folds periodically, and this is not optional.** `hot-path.md`'s P2b contract states it:
`uint` wraparound is not one's-complement neutral, the function is **size-public** (packet paths stop
at 64 KiB, callers need not), and a previous bare-accumulation attempt broke on the
`ProtocolAuditTests` shape at 131 076 bytes of `0xFF`. An accumulator that is only reduced at the
end overflows at a few megabytes of input. The measured probe implementations in
`research/aot-vs-jit-measurement.md` accumulate unbounded, which is sound for their 1 514-byte input
and **not** sound to ship: each tier folds its lanes through the scalar sum and reduces it
end-around whenever a lane has taken `4 096` words — `2 048` blocks, since a lane takes two words
per block — which bounds any lane at `0x0FFF_F000` and the whole sixteen-lane reduction at
`0xFFFF_0000`. The
periodic fold costs one reduction per 4 096 vectors, so it does not move the measured numbers.

The width arithmetic matters for the tail: `Vector<byte>.Count` is 32 under the JIT, so a 20-byte
IPv4 header checksum takes no vector iteration at all and is entirely tail; at `base` (16) it takes
one iteration plus a 4-byte tail. Both paths therefore matter, which is why §4.2 is not dead work.

### 4.2 The tail's fold cadence (R8)

`PacketChecksums.Sum` folds to 16 bits inside the per-word loop
(`sum = (sum & 0xffff) + (sum >> 16)` every two bytes). That creates a loop-carried dependency chain
and is why a whole-buffer scalar loop of that shape measures 1 282 ns on a 1 514-byte buffer under
AOT where the same loop folding every 4 096 words measures 771 ns.

The change keeps the shape and only moves the fold out of the inner cadence:

```csharp
const int foldWordInterval = 4_096;
var sinceFold = 0;
for (; index + 1 < data.Length; index += 2)
{
    sum += BinaryPrimitives.ReadUInt16BigEndian(data.Slice(index, 2));
    if (++sinceFold == foldWordInterval)
    {
        sum = (sum & 0xffff) + (sum >> 16);
        sinceFold = 0;
    }
}
```

**Overflow argument, which is the only thing that makes this legal.** The existing code folds per
word to keep `uint` from wrapping, because a wrap is not one's-complement-neutral (2^32 ≡ 1 mod
65 535). The bound after a fold is `sum ≤ 0xffff + 0xffff = 0x1fffe`; 4 096 more words add at most
`4 096 × 0xffff = 0x0ffff000`, so the accumulator never exceeds `0x1001effe < 2^32`. The vector path
in the same method already relies on the same order of magnitude (`foldBlockInterval = 4 096`
blocks, eight lanes, two words per lane per block), so this is not a new class of assumption — it is
the same assumption the hardware path has shipped with.

Equivalence is not a matter of taste: one's-complement addition with end-around carry is associative
modulo 65 535, and `Finish` performs the final full fold regardless, so deferring the intermediate
folds cannot change the result. That is a proof obligation, and it is discharged by measurement and
by tests (§5), not by the argument alone.

**Why not simply delete the vector code.** Measured on the same input, all four implementations
producing the identical checksum: the plain forms are 16-20x slower than the vectorized form under
the JIT (794 ns and 964 ns against ~48 ns) and are *unaffected* by the AOT instruction-set floor
(798 ns at `avx2` against 771 ns at the default). The loop defeats auto-vectorization on two
counts — the big-endian byte swap and the fold's end-around carry — so the vector path is
load-bearing, and the artifact chosen in §3 depends on it. That is an argument for keeping
vectorization, not for keeping `Vector256`: §4's rewrite is measured faster in all three builds.

## 5. Verification

| What | How |
| --- | --- |
| Existing gates | `dotnet build WinForward.slnx -c Release` (zero-warning), `dotnet test WinForward.slnx -c Release`, the two pre-commit gates |
| R7 equivalence | A property test over lengths 0..1 600 comparing `PacketChecksums.InternetChecksum` against the independent reference in `tests/WinForward.TestSupport/ChecksumMath.cs`, plus the seven existing test files that exercise these paths |
| R7 (the `Vector<T>` rewrite) | The AOT-at-`base` checksum must move from ~970 ns to ~85 ns on the 1514-byte input, and the JIT number must not regress against the hand-written `Vector256` (~45-48 ns). Property-tested against the reference for lengths 0..1600 |
| R8 (the tail cadence) | The whole-buffer scalar worst case must move from ~1 282 ns to ~771 ns, recorded under `benchmarks/results/` |
| R2 | An `IlcInstructionSet=base` publish reproduces the default capability profile; no CI change needed to prove it, but the pin is what makes it stable |
| R3 | The workflow run itself: the FDD publish succeeds and the artifact uploads |
| R4 | `ChecksumBenchmarks`' `Scalar` column is the production method, so under the JIT it *is* the vector path — the label invites the reader to conclude the opposite of the truth. Rename so the comparison a reader sees is "vector vs the scalar fallback", and re-state the `>20 %` gate against that comparison; measured, the vector form now wins by ~94 % under the JIT and ~77 % under an `avx2` AOT build |

**Not verifiable on this machine:** the AOT artifact's absolute numbers, because cross-OS AOT is
unsupported and a `linux-x64` AOT build of a Windows-only CLI is vacuously trimmed (ILLink folds
`OperatingSystem.IsWindows()` and deletes the product code). The `linux-x64` proxy numbers quoted
throughout come from builds of the same source with the same compiler settings and are used for
*relative* comparisons only. The CI workflow is the first place the real artifact is measured.

## 6. What this task deliberately does not fix

The AOT artifact stays 1.6-2.6x slower than JIT on the dispatch, flow-table and frame-rewriter paths.
That gap is the JIT's dynamic PGO, which a statically compiled image cannot reproduce; the
instruction-set floor has no bearing on it, since none of those files contains an ISA branch. The
framework-dependent artifact is the answer for anyone who needs that throughput. Recording this here
so that the next benchmark run does not treat it as an open defect.

## 7. Rollback

Every change is independently revertible. The instruction-set pin is one line; the fold cadence is
confined to one method and guarded by tests; the CI artifact and the README section are additive, and
deleting the second publish step leaves the AOT release exactly as it is today.
