#!/usr/bin/env python3
"""Regenerate the CPython golden vectors the analysis's arithmetic primitives are judged against.

The port has two physical preconditions it cannot test against the frozen campaign tree, because the
tree only exercises the draws and the number formats it happens to contain (D20.5):

* ``Stats/CpRandom.cs`` reproduces ``random.Random(int)``, and a bootstrap that draws one number per
  resample diverges visibly from the reference when a single draw is off. The tree's golden pins the
  draws it uses, which is a handful of ``randrange`` calls; the vector table pins the generator
  itself, including the ranges the tree never reaches (``n`` a power of two, ``n`` one below and one
  above a power of two, and ``getrandbits`` above 32 bits, where the reference masks the top word).
* ``Tables/VerbatimNumber.cs`` reproduces ``%.*f`` and ``%.*g``, whose rounding is half-to-even on
  the *exact* binary value: the midpoint table below is the set of doubles that tell a half-to-even
  formatter from one that rounds halves away from zero.

Both tables are produced by CPython itself, so the expectations are the reference's behaviour rather
than a transcription of it. This script imports nothing from ``analyze.py``: it is the standard
library's ``random`` and ``%`` operator only, so the vectors stay valid after the reference retires
(E4-d).

Usage (writes both files, in place):

    python3 benchmarks/WinForward.E2E.Analysis/verification/synthetic/make_cp_vectors.py
"""

from __future__ import annotations

import json
import platform
import random
import sys
from pathlib import Path

GOLDEN = Path(__file__).resolve().parents[1] / "golden"
COMMAND = "python3 benchmarks/WinForward.E2E.Analysis/verification/synthetic/make_cp_vectors.py"

# The seeds the generator is pinned on. `random.Random(int)` takes the integer's absolute value in
# little-endian 32-bit words; the list covers zero and one (where the key is shorter than one word),
# the word boundaries, and both ends of the 64-bit range the port accepts.
SEEDS = [
    0,
    1,
    2,
    -1,
    -2,
    41,
    12345,
    999999,
    2147483647,
    -2147483648,
    2147483648,
    4294967295,
    4294967296,
    4294967297,
    9223372036854775807,
    -9223372036854775808,
    20261006,
    1234567890123456789,
]

# `getrandbits(k)`: 32 is where the reference stops shifting a single word and starts filling a word
# array; 64 is the widest draw the port makes, and the analysis reaches that width only through a
# `_randbelow` over a range no pass count comes near.
BIT_WIDTHS = [1, 2, 5, 8, 16, 31, 32, 33, 40, 63, 64]

# `randrange(n)` stops: 1 and 2 reject draws, and 2**k - 1 / 2**k / 2**k + 1 are the three shapes
# `_randbelow`'s rejection loop treats differently.
RANGE_STOPS = [
    1,
    2,
    3,
    4,
    5,
    7,
    8,
    9,
    15,
    16,
    17,
    31,
    32,
    33,
    63,
    64,
    65,
    100,
    255,
    256,
    257,
    1000,
    1023,
    1024,
    1025,
    2147483647,
    2147483648,
    2147483649,
]

# The values `%.*f` is asked about. 0.0625 ... 0.9375 at three digits are the exact binary midpoints
# of that grid: a formatter that rounds halves away from zero prints 0.063 where the reference
# prints 0.062, and 0.188 where the reference prints 0.187.
FIXED_VALUES = [
    0.0625,
    0.1875,
    0.3125,
    0.4375,
    0.5625,
    0.6875,
    0.8125,
    0.9375,
    -0.0625,
    -0.1875,
    -0.9375,
    0.0,
    -0.0,
    1.0,
    -1.0,
    0.5,
    2.5,
    0.125,
    0.375,
    0.0005,
    0.0004999999999999999,
    -0.0005,
    0.9995,
    1.0005,
    -2.675,
    2.675,
    0.1,
    0.2,
    0.3,
    1.005,
    1.015,
    1.025,
    3.141592653589793,
    2.718281828459045,
    100.0,
    -100.0,
    1234567.8901,
    -1234567.8901,
    409600.0,
    7.0 / 3.0,
    1e-06,
    1.0000000000000002,
    9007199254740993.0,
    1e15,
    1e16,
    0.008,
]

FIXED_DIGITS = [0, 1, 2, 3, 4]

# `%.3g` cases: the shapes C's general format has to choose between — fixed form, exponential form,
# the boundary at the fourth significant digit, and the trailing-zero stripping in both forms.
GENERAL_VALUES = [
    1e-06,
    0.008,
    0.0001,
    0.00001,
    9.99e-05,
    0.000999999,
    0.001,
    0.01,
    0.1,
    0.5,
    1.0,
    1.5,
    9.99,
    10.0,
    99.9,
    100.0,
    999.0,
    1000.0,
    1234.0,
    9999.0,
    12345.0,
    99999.0,
    100000.0,
    123456.0,
    1e6,
    1e7,
    1234567.0,
    1e-07,
    1.2345e-07,
    1e-10,
    2.5e-08,
    0.0,
    -0.0,
    -1e-06,
    -0.008,
    -1234.0,
    -0.0 + 0.0,
    5e-324,
    1.7976931348623157e308,
    3.141592653589793,
    1e100,
    6.02214076e23,
    0.000123456,
    0.99995,
    9.9999e-05,
]

# `json.dumps` renders a float with `repr`, which is the shortest decimal that round-trips — the
# number text `verdict.json` carries. The cases pin the two places .NET's shortest form differs:
# an integral value keeps a `.0`, and the switch to an exponent happens at a different magnitude.
REPR_VALUES = [
    0.0,
    -0.0,
    1.0,
    -1.0,
    0.5,
    2.5,
    100.0,
    0.1,
    0.2,
    0.3,
    1.0 / 3.0,
    3.141592653589793,
    2.718281828459045,
    1e-05,
    0.0001,
    1e-06,
    1.5e-05,
    1e15,
    1e16,
    -1e16,
    9.999999999999998e15,
    123456789012345.6,
    1e17,
    1e100,
    1e-100,
    5e-324,
    1.7976931348623157e308,
    0.008,
    1.05,
    1.005,
    1234.5678,
    -1234.5678,
    2.0**52,
    2.0**53,
    1e300,
    0.30000000000000004,
]

# `PyOS_double_to_string` spells these three with a name rather than digits.
NONFINITE = ["Infinity", "-Infinity", "NaN"]

# `json.dumps(indent=2)` cases, as `(name, value)`: the escaping (the five characters the encoder
# leaves alone, the seven it does not, and the code points it writes as `\uXXXX`), the container forms
# (empty, nested, arrays of objects), and one document holding every scalar kind.
JSON_CASES = [
    ("the characters json leaves alone", {"text": "'+<>&/"}),
    ("the two mandatory escapes", {"quote": '"', "backslash": "\\"}),
    ("the five short control escapes", {"text": "\b\f\n\r\t"}),
    ("the control characters with no short form", {"text": "\x00\x01\x1f\x7f"}),
    ("non-ASCII text", {"text": "–—中文é\ufffd"}),
    ("an astral code point", {"text": "🙂🚀"}),
    ("an empty object and an empty array", {"object": {}, "array": []}),
    ("an array of objects", {"pairs": [{"a": 1}, {"b": 2}]}),
    ("a nested object", {"outer": {"middle": {"inner": "leaf"}}}),
    ("a nested array", {"grid": [[1, 2], [3]]}),
    ("a key that needs escaping", {'quote"d': "back\\slash"}),
    (
        "one document with every scalar kind",
        {"string": "x", "integer": 7, "plain float": 0.5, "integral float": 100.0, "exponent": 1e2,
         "true": True, "false": False, "null": None},
    ),
]


def rng_vectors() -> dict:
    """One entry per seed: the raw 32-bit words, then `getrandbits` and `randrange` draws."""
    entries = []
    for seed in SEEDS:
        generator = random.Random(seed)
        entry = {
            "seed": seed,
            # `getrandbits(32)` is `genrand_uint32` without a shift: it pins `init_by_array` itself.
            "words": [generator.getrandbits(32) for _ in range(8)],
            "bits": [{"width": width, "value": generator.getrandbits(width)} for width in BIT_WIDTHS],
            "ranges": [{"stop": stop, "value": generator.randrange(stop)} for stop in RANGE_STOPS],
        }
        entries.append(entry)
    return entries


def fixed_vectors() -> list[dict]:
    return [
        {"value": value, "digits": digits, "text": "%.*f" % (digits, value)}
        for digits in FIXED_DIGITS
        for value in FIXED_VALUES
    ]


def general_vectors() -> list[dict]:
    return [
        {"value": value, "precision": precision, "text": "%.*g" % (precision, value)}
        for precision in (3, 1, 6)
        for value in GENERAL_VALUES
    ]


def repr_vectors() -> dict:
    return {
        "finite": [{"value": value, "text": repr(value)} for value in REPR_VALUES],
        "named": [{"value": name, "text": json.dumps(float(name))} for name in NONFINITE],
    }


def json_vectors() -> list[dict]:
    return [
        {"name": name, "value": value, "text": json.dumps(value, indent=2)}
        for name, value in JSON_CASES
    ]


def write(path: Path, document: dict) -> None:
    document["generated_by"] = f"CPython {platform.python_version()} ({platform.python_compiler()})"
    document["command"] = COMMAND
    path.write_text(json.dumps(document, indent=2) + "\n", encoding="utf-8")
    print(f"wrote {path}")


def main() -> int:
    write(
        GOLDEN / "cp-random-vectors.json",
        {
            "what": "random.Random(int) draws: genrand_uint32 words, getrandbits(k) and randrange(n)",
            "seeds": rng_vectors(),
        },
    )
    write(
        GOLDEN / "py-json-vectors.json",
        {
            "what": "json.dumps(value, indent=2) with the reference's own encoder settings",
            "cases": json_vectors(),
        },
    )
    write(
        GOLDEN / "py-number-vectors.json",
        {
            "what": "C-style float formatting and Python float repr, as the reference writes numbers",
            "fixed": fixed_vectors(),
            "general": general_vectors(),
            "repr": repr_vectors(),
        },
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
