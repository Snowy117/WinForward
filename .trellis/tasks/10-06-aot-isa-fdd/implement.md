# Implement — AOT instruction-set floor and a framework-dependent release artifact

## Preconditions

- `README.md:11` (platform floor) and `README.md:29` (the artifact description) are the promises this
  task either keeps or changes. Read them before editing either.
- The measurement commands below assume a Linux host with clang available for AOT
  (`nix shell nixpkgs#clang nixpkgs#lld -c …`). They are for *relative* comparison only; see
  `design.md` §5.

## Steps

### 1. Pin the instruction set and record the decision — `src/WinForward.Cli/WinForward.Cli.csproj`

Add, next to `PublishAot`:

```xml
<!--
  Do not raise this. The executable is compiled for one instruction set for its whole lifetime and
  executes illegal instructions at process start on anything older, with no runtime guard possible
  because every ISA check in the image is folded to a constant at compile time. README.md promises
  Windows 10 22H2, which admits pre-2013 CPUs, and VMs commonly mask AVX2 behind an EVC baseline.
  The cost of staying at base is narrower vector code, not absent vector code: once step 2 lands, the
  portable artifact's checksum runs at 128 bits where the JIT uses 256, measured at ~1.8x and priced
  in benchmarks/results/. The framework-dependent artifact is the way around it. Note also that an XML
  comment cannot contain `--`, so `ilc --help` cannot appear inside the comment; phrase it in prose.
-->
<IlcInstructionSet>base</IlcInstructionSet>
```

Valid values come from `ilc --help`, not from memory: `base, sse4.2, avx, avx2, avx512, avx512v2,
avx512v3, avx10v1, avx10v2, …`. `x64` is **not** one of them.

**Validate:** `dotnet publish src/WinForward.Cli/WinForward.Cli.csproj -c Release -p:PublishAot=false --self-contained false -r win-x64`
still succeeds (the property must not disturb the JIT path), and a `linux-x64` AOT build with
`-p:IlcInstructionSet=base` reports `Vector256.IsHardwareAccelerated = False` — the same profile as
leaving it unset, which is what makes this pin behaviour-preserving.

### 2. Rewrite the checksum in three tiers — `src/WinForward.Protocols/PacketChecksums.cs`

Write the three tiers `design.md` §4.1 describes, in this order, then the tail:

1. `Vector512.IsHardwareAccelerated` → a `Vector512<ushort>` loop. `Vector512.Widen(source)` returns a
   `(lower, upper)` tuple (unlike the `Vector<T>` overload, which takes `out` parameters).
2. otherwise → a `Vector<T>` loop over `MemoryMarshal.Cast<byte, ushort>(data)`, byte-swapping with
   `(word >> 8) | (word << 8)` — two shifts, not `Vector256.Shuffle`, whose `pshufb` needs SSSE3 and
   is why the old path could not survive at `base`. Widen with
   `Vector.Widen(swapped, out Vector<uint> lo, out Vector<uint> hi)`.
3. then → the scalar tail, folding on the R8 cadence below.

**All three tiers fold periodically — this is a correctness requirement, not tuning.** `hot-path.md`
P2b: `uint` wraparound is not one's-complement neutral, the function is size-public, and a bare
accumulation already broke once on the `ProtocolAuditTests` shape (131 076 bytes of `0xFF`). Fold
each tier's lanes through the scalar sum and reduce end-around whenever a lane has taken 4 096 words
(2 048 blocks — a lane takes two words per block), bounding any lane at `0x0FFF_F000` and the whole
sixteen-lane reduction at `0xFFFF_0000`; folding per 4 096 *blocks* would exceed that and wrap the
reduction. The scalar tail keeps the literal 4 096-word cadence. A probe implementation that accumulates unbounded exists in
`research/aot-vs-jit-measurement.md` — do **not** copy it verbatim.

Keep:

- the vector path untouched;
- the odd trailing byte handling unchanged;
- `Finish`'s full fold unchanged;
- the existing comment block's overflow reasoning, updated to state the new bound instead of
  claiming a per-word fold is required.

Do not add a configuration knob, environment variable or `runtimeconfig.json` entry for any tier —
the gates are the configuration, and `DOTNET_MaxVectorTBitWidth` is environment-variable-only, which
was measured and rejected.

**Risky file.** This is the per-packet checksum on both TCP legs and every rebuilt UDP response.
Change nothing else in the file in the same commit.

**Validate:** `dotnet test tests/WinForward.Protocols.Tests -c Release` and
`dotnet test tests/WinForward.Runtime.UdpProxy.Tests -c Release`.

### 3. Property coverage for the new tiers — `tests/WinForward.Protocols.Tests/`

Add a test that walks lengths 0..1 600 (crossing 0, odd, one 512-bit block, one `Vector<T>` block, and
several fold intervals) and asserts `PacketChecksums.InternetChecksum` equals the independent reference in
`tests/WinForward.TestSupport/ChecksumMath.cs`, with byte patterns that include 0x00 and 0xff so the
end-around carry is actually exercised. Prefer extending the existing checksum test file over adding
a new one.

**Validate:** the new test fails against the old cadence only if the cadence was wrong — check that
it passes on the *unchanged* code first, so it is proving equivalence rather than encoding the new
behaviour.

### 4. Second release artifact — `.github/workflows/aot-build.yml`

After the existing AOT publish, add:

```yaml
      - name: Publish framework-dependent
        run: dotnet publish src/WinForward.Cli/WinForward.Cli.csproj -c Release --no-restore -r win-x64 -p:PublishAot=false --self-contained false

      - name: Upload framework-dependent artifact
        uses: actions/upload-artifact@v4
        with:
          name: WinForward-win-x64-fdd
          path: src/WinForward.Cli/bin/Release/net10.0/win-x64/publish/
          if-no-files-found: error
```

The two publishes share the output directory, so the second overwrites the first's files; that is
fine for the uploads because each upload runs immediately after its own publish. If that ordering
ever changes, give the FDD publish its own output path via `-p:PublishDir=`.

**Validate:** on a Windows host, both publishes succeed and the FDD `publish/` holds
`WinForward.exe` at ~1.73 MiB, no `WinForward.dll`, and no runtime files.

### 5. Document both artifacts — `README.md`

Replace the single-artifact paragraph at `:29` with a short table: what each artifact is, its
approximate size, whether it needs the .NET 10 runtime, and which one to pick. State plainly that the
framework-dependent build needs the runtime **installed first** and fails with a host error without
it. Keep the "single native AOT executable, no managed runtime dependency" sentence for the AOT row —
it is accurate there, and the FDD row is the one that needs the new caveat.

### 6. Re-measure the JIT-taken decisions (R9) — `benchmarks/`

- Rename `ChecksumBenchmarks`' `Scalar` method so the baseline is the **scalar fallback** rather than
  the production method, whose identity depends on the JIT's ISA. A reader must be able to tell
  which side is vectorized.
- Re-state the `>20 %` gate in the file's comment against that comparison.
- Run `ChecksumBenchmarks` under the JIT and record the result, with the AOT-default and AOT-`avx2`
  columns, under `benchmarks/results/2026-10-06-aot-instruction-set/`.

**Validate:** the production entry point lands near 34 ns under the JIT and near 85 ns in an AOT build at
`base`; the vector paths still clear the gate by a wide margin against the 771-800 ns scalar forms.

## Final checks

```bash
dotnet build WinForward.slnx -c Release
dotnet test WinForward.slnx -c Release
dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore
jb inspectcode -f=Xml -e=HINT -o=/tmp/jb-aot.xml WinForward.slnx
```

The format gate must exit 0 with empty output; the inspectcode XML must contain zero `<Issue>`
elements. Then dispatch `trellis-check` before committing.

## Follow-ups this task does not own

- The AOT artifact's PGO-shaped throughput gap (1.6-2.6x on dispatch, flow table and frame rewrite)
  is deliberately accepted; `design.md` §6 records it so a later benchmark run does not re-open it
  as a defect.
- If the checksum ever becomes the measured bottleneck in the AOT artifact, the next lever is a
  runtime-dispatched layout that keeps the image at `base` — out of scope here.
