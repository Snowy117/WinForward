# E4-b1b — `CpRandom`, the verbatim text primitives, and `verdict.json`'s `bootstrap`/`thresholds`

Batch E4-b1b of `.trellis/tasks/10-07-e2e-e4-analyzer`. Authority: `design-decisions.md` **D20.5**
(RNG and text format, the two physical preconditions of verbatim output), D20.2 (slicing and batch
ownership), D20.6 (the read route, and no `InternalsVisibleTo`), D20.7 (the batch table), and
`research/python-oracle-changes.md` (the reference's change list and its rendering quirks).

Its criterion is `python3 benchmarks/WinForward.E2E/scripts/oracle-diff.py --batch 1b` ⇒ **rc=0**:
`verdict.json`'s `bootstrap` and `thresholds`, equal to the frozen reference.

**Result: rc=0, 2 of 2 slices equal**, every other batch still rc=2, and the three-state self-check
still reports 0/1/2 for equal/mutated/missing (§6). The frozen tree, the frozen golden and the
reference `analyze.py` were not touched (§9.8).

## 1. What the batch is, and what it deliberately is not

The two keys this batch owns are the run's **parameters, published**: how many resamples each
interval is drawn with, the base seed, the resampling unit, the interval's definition, the minimum
pass count, and the declared practical-significance thresholds with the reference's own prose about
how a pair is judged. They are unconditional — the reference emits them for every campaign, whatever
the comparisons come out to — and they state what the intervals *below* them were drawn with.

The sampler that consumes them (`bootstrap_pair`, `holm_adjust`, `decide`) is **not** in this batch:
it has no output of its own, and every number it produces only exists inside the comparisons of the
batches that render them (§9.1). What this batch had to get right for it is the part that is
observable now — the generator and the seed derivation — and that part is finished and
golden-vectored. `Stats/CpRandom`'s own contract, the key derivation rule and the boundary cases are
§2; the two keys are §5.

| file | effective lines | what it carries |
|---|---|---|
| `Stats/CpRandom.cs` | 155 | the generator, `getrandbits`, `_randbelow`, `stable_hash` and the seed derivation |
| `Json/VerbatimNumber.cs` | 226 | `fmt_num` and the `%.*f` / `%.*g` / `repr` behind it (moved from `Tables/`, §9.2) |
| `Json/VerbatimJson.cs` | 104 | `json.dumps(..., indent=2, sort_keys=False)` (moved from `Verdict/`, §9.2) |
| `Verdict/VerdictSections.cs` | 45 | the two keys, and `flat_mode` through the writer |
| `Model/CampaignModel.cs` | 43 | `Resamples` / `Seed` / `MinPasses` |
| `Cli/AnalysisRunner.cs` | 65 | three fewer "not yet rendered" lines |
| `Loading/CampaignLoader.cs` | 163 | the three parameters taken from the options |
| `Stats/BootstrapResampler.cs` | 5 | the deferral, written where it is made |
| `tests/…/AnalyzerRandomGoldenTests.cs` | 74 | the RNG vectors and the seed derivation |
| `tests/…/AnalyzerNumberGoldenTests.cs` | 126 | the number vectors, the midpoint table, the fallback |
| `tests/…/AnalyzerJsonGoldenTests.cs` | 93 | the twelve documents and the writer's own rules |
| `tests/…/RepoPaths.cs` | — | `AnalyzerGolden(name)` |
| `verification/synthetic/make_cp_vectors.py` | — | the generator of all three tables (Python, so outside the line rule) |

## 2. `CpRandom`: the generator, value by value

| what the port carries | where the reference does it |
|---|---|
| `Reseed` — the absolute value in little-endian 32-bit words, **at least one word** | `random_seed` in `_randommodule.c` |
| `InitGenRand`, `InitByArray` | `init_genrand`, `init_by_array` |
| `NextUInt32` — the 624-word twist and the tempering | `genrand_uint32` |
| `GetRandBits(width)` — one shifted word up to 32 bits, a word array above | `getrandbits` |
| `RandBelow(bound)` — the rejection loop, `k = bound.bit_length()` | `_randbelow_with_getrandbits` |
| `StableHash`, `DeriveSeed` | `stable_hash`, and the seed expression of `control_drift`/`build_verdict` |

Two details are worth stating because nothing in the frozen tree exercises either of them:

* **A seed of zero is a key of one zero word, not an empty key.** The reference's `keymax` is
  `ceil(bits/32)` and an empty key would be read past; the port mirrors the floor of one word
  (`key[..Math.Max(length, 1)]`). This was settled against CPython itself rather than guessed: a
  Python model of `init_by_array` reproduces `random.Random(n).getstate()[1][:624]` for every seed in
  the table with that rule and for none of the alternatives at `n = 0`.
* **The sign is not part of the seed.** `random.Random(-41)` and `random.Random(41)` are the same
  generator, and `Magnitude` takes the absolute value without overflowing on `long.MinValue`
  (`(ulong)(-(seed + 1)) + 1`).

The port takes a 64-bit seed and draws at most 64 bits; the reference accepts an integer of any size.
That boundary is deliberate and registered: every seed the analysis derives is the `--seed` value
plus a value below 10^6, and `_randbelow` is only ever called with a pass count. The vector table
therefore stops at the two ends of the 64-bit range (`9223372036854775807`, `-9223372036854775808`)
and no longer carries the 10^20 seed it was first generated with — a seed no `long` can hold is a
seed the port cannot be asked for.

### 2.1 The golden vector table

`verification/golden/cp-random-vectors.json` (3121 lines, sha256
`b011711e290a744827edb9ae7433304f29770619885cd7d4b953a0307bab2690`), generated by CPython itself:

```bash
python3 benchmarks/WinForward.E2E.Analysis/verification/synthetic/make_cp_vectors.py
```

| part | what it pins | count |
|---|---|---|
| `seeds[i].words` | `getrandbits(32)`, which is `genrand_uint32` unshifted — the whole of `init_by_array` | 8 × 18 = 144 |
| `seeds[i].bits` | `getrandbits(k)` for k = 1, 2, 5, 8, 16, 31, **32**, 33, 40, 63, **64** | 11 × 18 = 198 |
| `seeds[i].ranges` | `randrange(n)` for n = 1, 2, 3, 4, 5, 7, **8**, 9, 15, **16**, 17, 31, **32**, 33, 63, **64**, 65, 100, 255, **256**, 257, 1000, **1023/1024/1025**, **2^31-1/2^31/2^31+1** | 28 × 18 = 504 |

The seeds are 0, ±1, ±2, 41, 12345, 999999, ±2^31, 2^32±1, 2^32, 2^32+1, ±2^63-1/2^63,
1234567890123456789 and the run's own `20261006`; each seed's draws are taken from one generator in
the order above, so the sequence is pinned as well as the values. **846 values, of which 702 are
`getrandbits`/`randrange` draws** — the batch criterion asked for at least 200.

`tests/WinForward.E2E.Tests/AnalyzerRandomGoldenTests.cs` (74 effective lines) replays them, and
three further facts sit beside the table: the rejection loop's boundary answers for `n = 1, 2, 3, 4,
7, 8, 9, 1000000`, that a seed and its negative agree, and that `StableHash`/`DeriveSeed` reproduce
the reference's own numbers for a real comparison key
(`stable_hash("lat.tcp_rtt.p50|wf-aot-opt|proxifier") = 1973867047`, so the derived seed is
`20261006 + 867047 = 21128053`).

## 3. `VerbatimNumber`: `%.*f`, `%.*g` and `repr`

`Tables/VerbatimNumber.cs` became `Json/VerbatimNumber.cs` (D20.5 puts both text primitives under
`Json/`; §9.2). The rounding is done on the **exact rational value** of the double
(`mantissa * 2^exponent`, as a `BigInteger` fraction), because that is the only way a midpoint is
reachable: `0.0625` at three digits is exactly halfway between `0.062` and `0.063`, and CPython
answers `0.062`.

The `%.3g` form is decided by the exponent **after** the rounding to three significant digits, which
is what C's conversion does and what two of the golden's own values show: `9.9999e-05` rounds up into
`0.0001` (plain form) while `99999.0` rounds up into `1e+05` (exponential form). The trailing zeros
of the significand are stripped in both forms (`1.8e+308`, `0.008`), the exponent is lower case with
an explicit sign and at least two digits (`1e-06`, `4.94e-324`, `1e+100`), and `%.3g` of either zero
is a bare `0` / `-0`.

`repr` — the float text inside `verdict.json` — is the shortest decimal that reads back as the same
double, which .NET's `"R"` also produces; the two differences are re-applied on top of it: an
integral value keeps its point (`100.0`, `1000000000000000.0`) and the exponent starts a decade
later (`1e+16` where .NET would already print an exponent).

`Cell` is the reference's `fmt_num`: the fixed form, falling back to `%.3g` exactly when the fixed
form prints a non-zero value as zero (`0.0004` → `0.0004`, `0.4` at no digits → `0.4`, `1e-06` →
`1e-06`), with the unit appended and a JSON `null` reading rendered as `n/a`.

### 3.1 The midpoint table

| value | `%.3f` (CPython) | halves away from zero |
|---|---|---|
| 0.0625 | **0.062** | 0.063 |
| 0.1875 | 0.188 | 0.188 |
| 0.3125 | **0.312** | 0.313 |
| 0.4375 | 0.438 | 0.438 |
| 0.5625 | **0.562** | 0.563 |
| 0.6875 | 0.688 | 0.688 |
| 0.8125 | **0.812** | 0.813 |
| 0.9375 | 0.938 | 0.938 |

Four of the eight discriminate, which is why the table is a test rather than a restatement: the
theory-driven fact asserts each answer, and `TheMidpointTableTellsHalfToEvenFromHalfAwayFromZero`
asserts that exactly four of them disagree with an away-from-zero formatter (computed in the test
with `Math.Round(..., MidpointRounding.AwayFromZero)`). The vector table adds the negative midpoints
and the same grid at 0, 1, 2 and 4 digits.

### 3.2 The vector table

`verification/golden/py-number-vectors.json` (sha256
`3d256f5994adcd1f7ba44e4f63f06ac7e144b8bbbd56eaa2a9903e812ffcae4e`), from the same generator:

| part | what it pins | count |
|---|---|---|
| `fixed` | `%.*f` for digits 0–4 over 46 values: the midpoints, both zeros, ±1, the `0.0005`/`0.0004999…` pair, `2.675`/`1.005`, `1e15`/`1e16`, `1e-06`, `2^53+1` | 230 |
| `general` | `%.*g` for precision 3, 1 and 6 over 45 values: `1e-06`, `0.008`, `9.99e-05`, `0.000999999`, `99999`, `1234567`, `5e-324`, `1.8e308`, negatives and both zeros | 135 |
| `repr` | `repr` as `json.dumps` writes a float, plus the three named spellings (`Infinity`, `-Infinity`, `NaN`) | 36 + 3 |

`tests/WinForward.E2E.Tests/AnalyzerNumberGoldenTests.cs` (126 effective lines) replays all 404
values and holds the midpoint table and the `%.3g` shapes as separate theories.

**A correction to the quirks list.** `python-oracle-changes.md` §5.5 says the golden carries "exactly
two" `%.3g` renderings, `1e-06` and `8.000e-03`. Measured: both live inside the BASE-floor *reason
string* (`checks.append(("BASE floor (%s)" % source, value < 1e-6, "%.3e < 1e-06" % value))`, line
3387), so `8.000e-03` is `%.3e`'s output and `1e-06` is a literal in the format string. No `fmt_num`
cell in the clean tree reaches the `%.3g` fallback at all — the two numbers are not interchangeable
with `0.000` and the fallback still needs implementing, which is what §3.1/§3.2 do. The document is
left as the batch's author wrote it (§9.5).

## 4. `VerbatimJson`: `json.dumps(..., indent=2, sort_keys=False) + "\n"`

The writer is explicit rather than delegated to `System.Text.Json`, whose default encoder escapes
more than the reference, whose indenting writer puts a space after the colon of a nested object, and
which has no way to say "escape this code point the way Python does".

| rule | how the port holds it |
|---|---|
| insertion order | `Object` writes its members in the order it is given; `VerdictWriter` keeps the reference's declaration order |
| two-space indent, no alignment | every value knows the level it sits at; a container renders its members one level deeper and closes at its own level |
| `<` `>` `&` `'` `+` `/` unescaped | only `"`, `\`, the five short control escapes and everything outside printable ASCII are escaped |
| non-ASCII and astral code points | `\uXXXX`, lower-case, with an astral code point written as its surrogate pair (`\ud83d\ude42`) |
| empty containers | `{}` and `[]`, on one line |
| numbers | `VerbatimNumber.Json` for a float, plain digits for an integer (§3) |
| no BOM, `\n` endings, one trailing newline | `AnalysisRunner.OpenOutput` (UTF-8 without BOM, `NewLine = "\n"`) and `VerdictWriter`'s final `"\n"` |

`verification/golden/py-json-vectors.json` (sha256
`759c08345cb344901d13e799128ab17908ae3d71613b4ff36025f79cdc3bf49d`) holds 12 documents with the
text CPython wrote for each — the five characters above, the two mandatory escapes, the five short
control escapes, `\u0000`/`\u001f`/`\u007f`, non-ASCII, an astral pair, an empty object and array,
arrays of objects, nested objects and arrays, an escaped key, and one document with every scalar kind
(including `null`, `true` and the integral float `100.0`). The test rebuilds each document through
the writer's own entry points, so nesting and order are exercised on values rather than fragments.

The file-level bytes are asserted where the files are: the produced `verdict.json` is 1515 bytes with
no BOM, no `\r` and exactly one trailing `\n` (`od`/`cmp` in §5), rather than by a unit test — the
class that writes the file is internal, and D20.6 is what keeps it that way.

## 5. The two keys, byte for byte

`Verdict/VerdictSections.cs` now builds them from the model, and `Model/CampaignModel.cs` carries the
three parameters the reference's `Context` carries (`Resamples`, `Seed`, `MinPasses`), wired in
`CampaignLoader` from `--resamples`/`--seed` and the reference's `DEFAULT_MIN_PASSES = 3`.

```text
  "bootstrap": {
    "resamples": 10000,
    "seed": 20261006,
    "resampling_unit": "passes (never samples)",
    "method": "percentile bootstrap over per-pass values, 95 % CI",
    "min_passes": 3
  },
  "thresholds": {
    "latency": "5 %", "cpu": "10 %", "memory": "10 %",
    "udp-loss": "0.5 percentage points", "tcp-unexpected": "0.1 percentage points",
    "note": "…Holm\u2013Bonferroni…"
  },
```

| check | result |
|---|---|
| `oracle-diff.py --batch 1b` | **rc=0**, `bootstrap` 9 lines and `thresholds` 10 lines equal |
| the two blocks read out of both files and compared as text | equal except for the comma the reference needs because eleven keys follow `thresholds` (1174 vs 1173 bytes) |
| the `thresholds` note | carries `'` unescaped and `Holm\u2013Bonferroni` as the escape — the en dash is written as a character in the C# source and escaped by the writer, which is the "preserve `\uXXXX`" rule doing its job |
| the keys are not constants | `--batch 1b --cs-out <resamples changed to 10001>` ⇒ rc=1, naming line 3 of `bootstrap` |

### 5.1 `--resamples 0` has no reference behaviour to copy

The reference **dies** on `--resamples 0` and writes *neither* file: `bootstrap_pair` divides by an
empty estimate list (`below = sum(1 for value in estimates if value <= 0.0) / len(estimates)`) inside
`collect_findings`, before `main()` reaches either `write_text`. There is therefore no output to
align with, and by D20.1 this is an input-class divergence to be asserted against the C# product
alone:

```console
$ python3 -B benchmarks/results/2026-10-06-e2e-competitors/analysis/analyze.py \
      --raw /tmp/wf-synth/raw --out /tmp/e4b1b/ref0 --resamples 0
ZeroDivisionError: division by zero            # rc=1, /tmp/e4b1b/ref0/ is empty

$ WinForward.E2E.Analysis --raw /tmp/wf-synth/raw --out /tmp/e4b1b/res0 --resamples 0
analyze.py: verdict -> /tmp/e4b1b/res0/verdict.json; warmup 5.0 s, 0 resamples, seed 20261006, min passes 3
$ rg -n 'resamples|min_passes' /tmp/e4b1b/res0/verdict.json
22:    "resamples": 0,
26:    "min_passes": 3
```

The keys are still there and `resamples` is still the number the run was asked for; the resampling
itself is skipped because there is nothing to draw. Diffed against the frozen reference that run is
rc=1 on exactly one line (`resamples`, 0 against 10000) — which is also the cheapest proof that the
key is read from the option rather than written as a constant. What the *sampler* does at zero is a
decision for the batch that first calls it, and it is written down where it will be made
(`Stats/BootstrapResampler.cs`, §9.1).

## 6. The three states, and the other batches

| what was compared | command | result |
|---|---|---|
| the port against the frozen golden | `oracle-diff.py --batch 1b` | **rc=0**, 2/2 equal |
| one digit changed (`resamples` 10000 → 10001) | `--batch 1b --cs-out …/mutated` | **rc=1**, `bootstrap` line 3 named |
| the `bootstrap` key deleted | `--batch 1b --cs-out …/nokey` | **rc=2**, 0 differ / 1 missing |
| every other batch | `--batch 1a/1c/2/3/4/5` | 1a rc=0; 1c/2/3/4/5 still rc=2 |

Batch 1a stayed green, which is the point of re-running it: the batch moved two files and edited
three, and the preamble/§15 slices are the ones that would move if a shared helper had drifted.

## 7. Negative controls

Each was a one-line edit, rebuilt, run against the new tests only, and reverted; the hashes at the
end are of the **restored** sources (`/tmp/e4b1b/checks/fixed-hashes.txt`, `sha256sum -c` ⇒ all OK).

| # | the edit | what went red |
|---|---|---|
| N1 | `RoundHalfEven`: `comparison >= 0` — halves away from zero | 6 of 23: the four discriminating midpoints, the discriminating-count fact, and the 230-value `%.*f` table |
| N2 | `String`: escape `'` as `\u0027` | 2 of 7: the "leaves alone" fact and the 12-document golden |
| N3 | `Reseed`: key words in big-endian order | 1 of 4: the 144-word/702-draw golden |

N1 is the one that matters for the batch criterion: it is the difference D20.5 names, and it moves
exactly the four midpoints whose even neighbour is below the half — the other four are unchanged by
it, which is why the table has eight rows and not four.

## 8. The gates

Six gates plus the batch criterion, serially, on the final tree (`/tmp/e4b1b/gates/`):

| gate | result |
|---|---|
| `dotnet build WinForward.slnx -c Release` | rc=0, **0 Warning(s) / 0 Error(s)** |
| `dotnet test WinForward.slnx -c Release -m:1` | rc=0, 14 assemblies, **1655 passed, 0 failed, 0 skipped**, no `error ` line |
| `dotnet format WinForward.slnx --severity info --verify-no-changes --no-restore` | rc=0, **0 bytes of output** |
| `jb inspectcode -f=Xml -e=HINT -o=… WinForward.slnx` | rc=0, XML parsed: **`<Issue>` 0, `<IssueType>` 0**, no `CSharpErrors` |
| `effective-lines.py` over the four paths | rc=0, **0 bytes of output** |
| the batch's own criterion | `--batch 1b` rc=0; `--batch 1a` rc=0; `--batch 1c/2/3/4/5` rc=2 each |

`dotnet test` grew by exactly the new facts: 1621 → **1655**, +34, and `WinForward.E2E.Tests` alone
went 322 → 356. The largest file this batch touched is `Json/VerbatimNumber.cs` at 226 effective
lines, under the 400-line rule.

The two gates found six diagnostics on the way, and all six were fixed rather than suppressed:

| gate | finding | the fix |
|---|---|---|
| format | `IDE1006` — a local constant in Pascal case | `metricKey` |
| format | `CA1870` — `IndexOfAny(char[])` suggests `SearchValues` | the probe became `text.AsSpan().Trim("0.-").IsEmpty`, which is the same set the reference's parse-back answers |
| format | `RCS1267` — `string.Concat` of spans | the interpolated form (one allocation, same text) |
| inspectcode | `RedundantUsingDirective` — `System.Text` in `VerbatimNumber` | removed |
| inspectcode | `MergeIntoLogicalPattern` ×2 — two `double.Is…Infinity` guards, and `exponent <= -5 \|\| exponent >= 16` | `double.IsInfinity` + the sign bit, and the relational pattern `exponent is <= -5 or >= 16` |
| inspectcode | `SwitchExpressionHandlesSomeKnownEnumValuesWithExceptionInDefault` — `JsonValueKind.Undefined` | an explicit arm for it |

## 9. Deviations and deferrals

1. **The sampler is deferred to batch 3/4.** `bootstrap_pair`, `holm_adjust` and `decide` produce no
   output of their own — every number they compute is rendered by the batches that own the metric
   tables — so they land with those tables rather than here, and `Stats/BootstrapResampler.cs` says
   so on every run (`not yet rendered: 3/4 bootstrap intervals, Holm adjustment and verdict
   wording`). What the batch does own is the part that is observable now: the generator, the seed
   derivation and the parameters. The batch is judged by its own criterion (the two keys, rc=0).
2. **The two text primitives moved to `Json/`.** D20.5 names `Json/VerbatimNumber.cs` and
   `Json/VerbatimJson.cs`; E4-a's skeleton had left them under `Tables/` and `Verdict/`, where
   nothing consumed them yet. Moving them also removes a dependency the split would otherwise have
   had (`Verdict` reaching into `Tables` for a number formatter). `E4b1a-oracle.md` §1's file table
   names the old paths, which is what that batch wrote; it is left as the record of b1a.
3. **Three types are public.** `CpRandom`, `VerbatimNumber` and `VerbatimJson` are `public` so
   `tests/WinForward.E2E.Tests` can drive them value by value; the alternative was an
   `InternalsVisibleTo` on the analyzer, which D20.6 rules out and which the b1a check round recorded
   as "0 hits in this project". The test project gained a `ProjectReference` to the analyzer for the
   same reason. Everything else in the analyzer stays `internal`.
4. **`--resamples 0` is a C#-only assertion** (§5.1): the reference crashes and writes nothing.
5. **`python-oracle-changes.md` §5.5 misattributes two numbers** (§3.2). The quirk list is the
   batch's own evidence document and is left untouched; the correction lives here, and the fallback
   it describes is covered by the vector table instead of by the frozen tree.
6. **The port takes a 64-bit seed and draws at most 64 bits.** The reference is unbounded; the
   analysis's seeds are not (base seed + hash below 10^6), and `_randbelow` is only ever called with
   a pass count. `RandBelow` is `long` rather than `int` so the vector table can pin the 32-bit draw
   widths (2^31-1/2^31/2^31+1) that an `int` bound cannot express.
7. **`verification/` is not byte-unchanged this batch.** Three vector files and one generator were
   added under it and `FROZEN.md` gained §1.1; the tarball, both golden files, `make_tree.py`,
   `check-fixture-drift.py` and `plots-SKIPPED.md` are untouched, so **A was not re-frozen** and the
   earlier batches' diffs stand.
8. **The reference was only ever run with `python3 -B`/`PYTHONDONTWRITEBYTECODE=1`.** The tracked
   `benchmarks/results/2026-10-06-e2e-competitors/analysis/__pycache__/analyze.cpython-314.pyc` is
   unmodified (`git status --porcelain` for the analysis directory is empty apart from nothing).

## 10. For check

1. **The key padding rule** (§2). `Reseed` keeps the key at one word when the seed is zero; the
   golden's first seed is 0 and its eight words are the ones CPython produces with that padding.
   Confirm the alternative (`key[..length]`, an empty key) is unreachable rather than merely
   untested — and that the rule is stated where the code makes it.
2. **`%.3g` chooses its form from the rounded exponent** (§3). `9.9999e-05 → 0.0001` and
   `99999.0 → 1e+05` are the two cells that tell the rule from "use the value's own exponent"; every
   other value in the table passes either way. Confirm both are in `py-number-vectors.json` and
   asserted, and that the `%.3g` fallback (`Cell`) is reachable from a real caller and not only from
   a test.
3. **The two keys are compared value-wise by the differ** (§5). `oracle-diff.py` re-serializes the
   parsed subtree, so a wrong *order* inside `bootstrap`/`thresholds` would still pass rc=0; the
   byte-level comparison in §5 is what pins the text, and the only difference it leaves is the
   separator comma that `VerdictWriter` adds because more keys follow. Confirm the insertion order is
   the reference's, and that the note's en dash is the escape and not the character.
4. **The `BootstrapResampler` deferral** (§9.1) — that the batch's criterion is met without it, that
   the deferral is visible on every run rather than hidden, and that `--resamples 0`'s C#-only
   behaviour is the right call rather than a divergence to fix.
5. **The public surface and the missing IVT** (§9.3) — that only the three primitives are public,
   that `tests/WinForward.E2E.Tests` gained a `ProjectReference` rather than the analyzer gaining an
   `InternalsVisibleTo`, and that the golden vectors are read through `RepoPaths.AnalyzerGolden`
   rather than copied into the test tree. `rg -n InternalsVisibleTo benchmarks/WinForward.E2E.Analysis`
   now returns three hits, all of them the prose in those three classes that says *why* they are
   public instead; there is no attribute, and the "0 hits" the b1a check round recorded should be read
   as "no attribute" for the same reason.
