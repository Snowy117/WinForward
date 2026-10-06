#!/usr/bin/env python3
"""Campaign analysis for the 2026-10-06 Windows transparent-proxy end-to-end benchmark.

Input layout
============

    <raw>/pass<N>/<row>/run.json              one object describing that client run
    <raw>/pass<N>/<row>/<ARM>.jsonl           JSON Lines: sample / samplerError / result /
                                              armSummary / error records
    <raw>/pass<N>/<row>/proxy-truth.json      {"tcp": n, "udp": n, "utcp": n, "total": n}
    <raw>/pass<N>/<row>/dual/proxied/...      a second client run (same layout as a row)
    <raw>/pass<N>/<row>/dual/direct/...       a second client run, the same workload shape
    <raw>/pass<N>/<row>/dual/proxy-truth.json the row-level shape plus {"directLeak": n}
    <raw>/pass<N>/<row>/config*               the effective product configuration (opaque)
    <raw>/pass<N>/target-ledger.jsonl         the target's own ledger for that pass
    <raw>/pass<N>/order.txt                   optional within-pass run order
    <raw>/environment.json                    optional orchestrator environment block

The ledger is also looked for as ``ledger.jsonl`` or ``*ledger*.jsonl`` beside the pass
directory, in ``<raw>`` itself and in ``<raw>``'s parent (a one-off run writes it next to
its output directory). ``--ledger PATH`` overrides the search.

``--flat`` accepts a tree whose own directory is the only row, or whose immediate
subdirectories are rows, of a single implicit pass (that is what a one-off smoke run
looks like).

Rows and plans
==============

``ROW_PROFILES`` below is the single place that records what each row's plan runs, what
it does with destination port 53 over UDP and what it does with general UDP. Every
label, exclusion and comparability rule in this script is derived from it. The important
consequences, all generated from that one table:

* a row that did not run an arm is reported as ``not measured in this row`` and is never
  treated as a missing-at-random value or as a zero;
* every ProxiFyre/Proxifier UDP cell whose traffic the product does not carry reads
  ``not carried (UDP bypassed)`` instead of a number, and those rows are excluded from
  the UDP-accuracy and DNS-latency comparisons;
* the port-53 ``DNS`` arm is a *direct-path* measurement on the rows whose DNS local
  target forwards it verbatim and on ProxiFyre (hardcoded port-53 pass-through), so the
  cross-product DNS comparison runs on ``DNSALT`` and the ``DNS`` arm carries an explicit
  carriage column.

Aggregation policy
==================

Every aggregated number in ``tables.md`` is computed **over passes**, never over raw
samples and never over arm iterations:

  1. one value is computed per pass (one arm result record, or one statistic of that
     pass's sample stream for that row);
  2. the reported number is the median of those per-pass values, with the interquartile
     range ``[p25-p75]`` printed next to it and the pass count printed as ``(n=K)``;
  3. a cell backed by a single pass is flagged ``(n=1)`` so a single run is never
     mistaken for a consensus.

Percentiles always come from the harness's own histograms (``p50Us``, ``p90Us``,
``p99Us``, ``p999Us``); they are never recomputed from samples. A cell whose input is
missing is printed as ``n/a (<reason>)`` and is never silently dropped, and no table ever
averages over a pass count it does not print. A rate the harness wrote as JSON ``null``
means its denominator was zero (nothing was sent), so the cell is rendered **empty** and
never as a zero.

Outputs
=======

``tables.md``, ``verdict.json`` and ``plots/*.png`` (or ``plots/SKIPPED.md`` when
matplotlib is not importable). The analysis itself is Python 3 standard library only;
matplotlib is optional and imported lazily inside the plotting section. No numpy.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import random
import re
import sys
from dataclasses import dataclass, field
from datetime import datetime, timedelta, timezone
from pathlib import Path

SEP = "/"  # path separator for dig(); '/' because metric keys themselves contain '.'
DEFAULT_RAW = "../raw"
DEFAULT_OUT = ".."
DEFAULT_SEED = 20261006
DEFAULT_RESAMPLES = 10000
DEFAULT_MIN_PASSES = 3
DEFAULT_WARMUP_SECONDS = 5.0

ROW_ORDER = (
    "control-pre",
    "wf-aot-opt",
    "wf-fdd-opt",
    "wf-aot-nativeudp",
    "wf-aot-dnsrelay",
    "proxifyre",
    "proxifier",
    "proxybridge",
    "control-post",
)
CONTROL_PRE = "control-pre"
CONTROL_POST = "control-post"
CONTROL_ROWS = (CONTROL_PRE, CONTROL_POST)
COMPETITOR_ROWS = {"proxifyre", "proxifier", "proxybridge"}
TCP_GATE_MIN = 0.95
UDP_GATE_MIN = 1.0

# What a row's plan runs. `full` is the whole measured arm set; the partial rows exist to
# isolate one feature of the reference configuration, so they run only the arms that
# feature can move, and "not measured in this row" is a design statement, never a gap.
PLAN_ARMS = {
    "full": ["IDLE", "LAT", "LATLOAD", "DNS", "DNSALT", "LOSS", "REL", "THRU", "MIX", "PERSIST"],
    "udp only": ["LAT", "LATLOAD", "LOSS"],
    "dns only": ["DNS", "DNSALT"],
    "BASE only": ["BASE"],
}

UDP53_RELAYED = "relayed"
UDP53_LOCAL_TARGET = "direct-local-target"
UDP53_HARDCODED = "direct-hardcoded-bypass"
UDP53_NOT_CARRIED = "direct-not-carried"
UDP53_NA = "n/a"

UDP_PROXIED_UTCP = "proxied-utcp"
UDP_PROXIED_NATIVE = "proxied-native"
UDP_NOT_CARRIED = "not-carried"
UDP_NA = "n/a"


@dataclass(frozen=True)
class RowProfile:
    """One measured row: plan, dual phase, and what it does with UDP."""

    product: str
    config: str
    plan: str
    dual: bool
    udp53: str
    udp: str


ROW_PROFILES = {
    "wf-aot-opt": RowProfile(
        product="WinForward AOT",
        config="UDP-over-TCP v2 on, DNS local target on (reference configuration)",
        plan="full",
        dual=True,
        udp53=UDP53_LOCAL_TARGET,
        udp=UDP_PROXIED_UTCP,
    ),
    "wf-fdd-opt": RowProfile(
        product="WinForward framework-dependent",
        config="same configuration as wf-aot-opt",
        plan="full",
        dual=True,
        udp53=UDP53_LOCAL_TARGET,
        udp=UDP_PROXIED_UTCP,
    ),
    "wf-aot-nativeudp": RowProfile(
        product="WinForward AOT",
        config="UDP-over-TCP off, everything else identical to wf-aot-opt",
        plan="udp only",
        dual=False,
        udp53=UDP53_LOCAL_TARGET,
        udp=UDP_PROXIED_NATIVE,
    ),
    "wf-aot-dnsrelay": RowProfile(
        product="WinForward AOT",
        config="DNS local target off, everything else identical to wf-aot-opt",
        plan="dns only",
        dual=False,
        udp53=UDP53_RELAYED,
        udp=UDP_PROXIED_UTCP,
    ),
    "proxifyre": RowProfile(
        product="ProxiFyre 2.6.1",
        config="SOCKS5 rule for the client, supportedProtocols TCP+UDP",
        plan="full",
        dual=True,
        # netlib/src/proxy/socks_local_router.h: destination port 53 is allowed to pass
        # through without redirection, in both the IPv4 and the IPv6 site.
        udp53=UDP53_HARDCODED,
        udp=UDP_PROXIED_NATIVE,
    ),
    "proxifier": RowProfile(
        product="Proxifier 4.14",
        config="SOCKS5 profile, process rules (no UDP support in the product)",
        plan="full",
        dual=True,
        udp53=UDP53_NOT_CARRIED,
        udp=UDP_NOT_CARRIED,
    ),
    "proxybridge": RowProfile(
        product="ProxyBridge 4.0.0",
        config="PROXY rule for the client, DIRECT rule for sing-box",
        plan="full",
        dual=True,
        udp53=UDP53_RELAYED,
        udp=UDP_PROXIED_NATIVE,
    ),
    CONTROL_PRE: RowProfile(
        product="none (control block before the product block)",
        config="no product loaded",
        plan="BASE only",
        dual=False,
        udp53=UDP53_NA,
        udp=UDP_NA,
    ),
    CONTROL_POST: RowProfile(
        product="none (control block after the product block)",
        config="no product loaded",
        plan="BASE only",
        dual=False,
        udp53=UDP53_NA,
        udp=UDP_NA,
    ),
}

# Derived, never hand-maintained: a product that leaves UDP on the direct path.
UDP_INCAPABLE_ROWS = {row for row, profile in ROW_PROFILES.items() if profile.udp == UDP_NOT_CARRIED}

UDP53_LABEL = {
    UDP53_RELAYED: "relayed through the proxy",
    UDP53_LOCAL_TARGET: "direct via the local DNS target (udp/53 forwarded verbatim, bypasses the proxy)",
    UDP53_HARDCODED: "direct via the product's hardcoded port-53 pass-through",
    UDP53_NOT_CARRIED: "direct (the product cannot carry UDP at all)",
    UDP53_NA: "n/a (no product loaded: the direct path is the only path)",
}

UDP_LABEL = {
    UDP_PROXIED_UTCP: "proxied (UDP-over-TCP v2)",
    UDP_PROXIED_NATIVE: "proxied (native UDP relay)",
    UDP_NOT_CARRIED: "not carried (UDP bypassed)",
    UDP_NA: "n/a (no product loaded)",
}

NOT_CARRIED_CELL = "not carried (UDP bypassed)"
NULL_RATE_REASON = "null rate: the harness wrote null because the denominator was zero"

ARM_ORDER = ["IDLE", "LAT", "LATLOAD", "DNS", "DNSALT", "LOSS", "REL", "THRU", "MIX", "PERSIST", "BASE"]
LATENCY_CLASSES = ["tcp-connect", "tcp-rtt", "udp-rtt", "dns-rtt"]
PERCENTILES = ["minUs", "meanUs", "p50Us", "p90Us", "p99Us", "p999Us", "maxUs"]
OUTCOME_KEYS = [
    "clean",
    "reset",
    "unexpectedEof",
    "timeout",
    "connectFail",
    "halfCloseViolation",
    "otherError",
]
MIB = 1024.0 * 1024.0

USAGE = (
    "analyze.py [--raw <dir>] [--out <dir>] [--ledger <path>] [--flat] "
    "[--warmup-seconds S] [--resamples N] [--seed N]"
)

# Pre-declared practical-significance thresholds. 'ratio' families compare
# ratios of medians against 1 +- value; 'diff' families compare percentage-point
# differences against +- value.
THRESHOLDS = {
    "latency": {"kind": "ratio", "value": 0.05, "text": "5 % latency"},
    "cpu": {"kind": "ratio", "value": 0.10, "text": "10 % CPU"},
    "memory": {"kind": "ratio", "value": 0.10, "text": "10 % memory"},
    "udp-loss": {"kind": "diff", "value": 0.5, "text": "0.5 pp UDP loss"},
    "tcp-unexpected": {"kind": "diff", "value": 0.1, "text": "0.1 pp TCP unexpected rate"},
    "none": {"kind": "ratio", "value": None, "text": "no pre-declared threshold"},
}

# Tolerances used by the independent cross-checks. Declared here, printed beside every
# number they judge, never tuned after seeing a result.
LEDGER_CONNECTION_TOLERANCE = 0.01  # 1 % of the client's own count ...
LEDGER_CONNECTION_SLACK = 2.0  # ... or two connections, whichever is larger
LEDGER_DATAGRAM_TOLERANCE = 0.01
LEDGER_DATAGRAM_SLACK = 5.0
LEDGER_SUMMARY_INTERVAL_SECONDS = 1.0

CORRECTNESS_FAILURE = "correctness-failure"
HARNESS_ERROR = "harness-error"
MEASUREMENT_CAVEAT = "measurement-caveat"
INFORMATIONAL = "informational"
PATH_INTERFERENCE = "path-interference"
SEVERITY_ORDER = [CORRECTNESS_FAILURE, PATH_INTERFERENCE, HARNESS_ERROR, MEASUREMENT_CAVEAT, INFORMATIONAL]


def dig(obj, path, default=None):
    """Walk ``path`` ('metrics/classes/udp/lossRate') into nested dicts."""
    cur = obj
    for part in path.split(SEP):
        if not isinstance(cur, dict) or part not in cur:
            return default
        cur = cur[part]
    return cur


def dig_present(obj, path):
    """(present, value): unlike dig(), distinguishes a missing key from a JSON null."""
    cur = obj
    for part in path.split(SEP):
        if not isinstance(cur, dict) or part not in cur:
            return False, None
        cur = cur[part]
    return True, cur


def is_number(value) -> bool:
    return isinstance(value, (int, float)) and not isinstance(value, bool)


def as_number(value):
    return float(value) if is_number(value) else None


def quantile(values, q):
    """Linear-interpolation percentile; q in [0, 1]. The single percentile helper."""
    if not values:
        return None
    ordered = sorted(values)
    if len(ordered) == 1:
        return ordered[0]
    pos = q * (len(ordered) - 1)
    lo = int(math.floor(pos))
    hi = int(math.ceil(pos))
    if lo == hi:
        return ordered[lo]
    return ordered[lo] + (ordered[hi] - ordered[lo]) * (pos - lo)


def median(values):
    return quantile(values, 0.5)


def median_iqr(values):
    """(median, p25, p75) of the per-pass values, or (None, None, None)."""
    if not values:
        return None, None, None
    return quantile(values, 0.5), quantile(values, 0.25), quantile(values, 0.75)


def fmt_num(value, digits=3, unit=""):
    if value is None:
        return "n/a"
    text = "%.*f" % (digits, value)
    if float(value) != 0.0 and float(text.replace("-", "").replace(".", "")) == 0:
        text = "%.3g" % value
    return text + unit


def fmt_stat(values, digits=3, unit="", zero_bound_n=None, bound_scale=None, null_passes=0, missing_passes=0):
    """Render ``median [p25-p75] (n=K)`` for one metric's per-pass values.

    ``zero_bound_n`` triggers the rule of three: when *every* pass value is
    exactly zero, the cell prints ``< 3/n`` instead of a misleading ``0``.
    ``bound_scale`` additionally prints the bound's value (100.0 turns a fraction
    bound into a percentage). A cell whose median is zero but which has a
    non-zero pass keeps the ordinary rendering so the spread stays visible.

    ``null_passes`` counts passes whose value the harness wrote as JSON null (a rate
    with a zero denominator). When every pass is null the cell is rendered **empty**
    rather than as a zero, and when only some passes are null the count is printed
    beside the pass count so nothing is silently dropped.
    """
    if not values:
        return "" if null_passes else "n/a"
    unavailable = null_passes + missing_passes
    if zero_bound_n is not None and zero_bound_n > 0 and all(v == 0 for v in values):
        suffix = "" if not unavailable else " (%d pass(es) without a value)" % unavailable
        if bound_scale is None:
            return "< 3/%d%s" % (zero_bound_n, suffix)
        return "< 3/%d = %s%s" % (zero_bound_n, fmt_num(3.0 * bound_scale / zero_bound_n, 4, unit), suffix)
    med, p25, p75 = median_iqr(values)
    total = len(values) + null_passes + missing_passes
    parts = []
    if null_passes:
        parts.append("%d null" % null_passes)
    if missing_passes:
        parts.append("%d unavailable" % missing_passes)
    if not parts:
        tail = "(n=1)" if len(values) == 1 else "(n=%d)" % len(values)
    else:
        tail = "(n=%d of %d; %s)" % (len(values), total, ", ".join(parts))
    if len(values) == 1:
        return "%s %s" % (fmt_num(med, digits, unit), tail)
    return "%s [%s–%s]%s %s" % (
        fmt_num(med, digits),
        fmt_num(p25, digits),
        fmt_num(p75, digits),
        unit,
        tail,
    )


def natural_key(text):
    return [int(part) if part.isdigit() else part for part in re.split(r"(\d+)", text)]


def md_table(headers, rows):
    """Render one markdown table."""
    out = ["| " + " | ".join(headers) + " |", "|" + "|".join(["---"] * len(headers)) + "|"]
    for row in rows:
        out.append("| " + " | ".join(str(cell) for cell in row) + " |")
    return "\n".join(out)


def sha256_prefix(path, length=12):
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(65536), b""):
            digest.update(chunk)
    return digest.hexdigest()[:length]


def stable_hash(text):
    """Deterministic across processes (unlike hash()); seeds the bootstrap."""
    return int.from_bytes(hashlib.sha256(text.encode("utf-8")).digest()[:4], "big")


def parse_utc(text):
    """Parse the harness's ISO-8601 UTC stamps ('...T14:07:18.3361631+00:00')."""
    if not isinstance(text, str) or not text:
        return None
    try:
        stamp = datetime.fromisoformat(text)
    except ValueError:
        return None
    if stamp.tzinfo is None:
        stamp = stamp.replace(tzinfo=timezone.utc)
    return stamp.astimezone(timezone.utc)


# Student's t at 95 % two-sided for df 1..30; the normal 1.96 is used beyond.
_T95 = {
    1: 12.706,
    2: 4.303,
    3: 3.182,
    4: 2.776,
    5: 2.571,
    6: 2.447,
    7: 2.365,
    8: 2.306,
    9: 2.262,
    10: 2.228,
    11: 2.201,
    12: 2.179,
    13: 2.160,
    14: 2.145,
    15: 2.131,
    16: 2.120,
    17: 2.110,
    18: 2.101,
    19: 2.093,
    20: 2.086,
    21: 2.080,
    22: 2.074,
    23: 2.069,
    24: 2.064,
    25: 2.060,
    26: 2.056,
    27: 2.052,
    28: 2.048,
    29: 2.045,
    30: 2.042,
}


def t_critical_95(dof):
    if dof <= 0:
        return 0.0
    return _T95.get(dof, 1.96)


def ols_slope(xs, ys):
    """Least-squares slope with its 95 % CI and a leak verdict.

    Returns (slope, lo, hi, n, verdict), with None for the numbers when there is
    too little data; the verdict is 'leaks' only when the CI excludes zero from
    above.
    """
    n = len(xs)
    if n < 3:
        return None, None, None, n, "n/a (fewer than three samples)"
    mean_x = sum(xs) / n
    mean_y = sum(ys) / n
    sxx = sum((x - mean_x) ** 2 for x in xs)
    if sxx == 0:
        return None, None, None, n, "n/a (all samples share one timestamp)"
    sxy = sum((x - mean_x) * (y - mean_y) for x, y in zip(xs, ys))
    slope = sxy / sxx
    intercept = mean_y - slope * mean_x
    sse = sum((y - (intercept + slope * x)) ** 2 for x, y in zip(xs, ys))
    se = math.sqrt(sse / (n - 2) / sxx) if sse > 0 else 0.0
    tcrit = t_critical_95(n - 2)
    lo, hi = slope - tcrit * se, slope + tcrit * se
    if lo > 0:
        verdict = "leaks (CI above zero)"
    elif hi < 0:
        verdict = "does not leak (CI below zero)"
    else:
        verdict = "does not leak (CI includes zero)"
    return slope, lo, hi, n, verdict




@dataclass
class ArmData:
    name: str
    file: str
    result: dict | None = None
    arm_summary: dict | None = None
    samples: list = field(default_factory=list)
    sampler_errors: list = field(default_factory=list)
    errors: list = field(default_factory=list)
    bad_lines: int = 0

    @property
    def kind(self):
        if self.result is not None:
            return self.result.get("kind")
        if self.arm_summary is not None:
            return self.arm_summary.get("kind")
        return None


@dataclass
class RunData:
    """One client run: a row of a pass, a dual lane, or the flat tree itself."""

    pass_id: str
    run_id: str
    path: Path
    run: dict = field(default_factory=dict)
    arms: dict = field(default_factory=dict)
    configs: list = field(default_factory=list)  # (filename, sha256 prefix, bytes)
    proxy_truth: dict | None = None
    dual_proxied: "RunData | None" = None
    dual_direct: "RunData | None" = None
    dual_truth: dict | None = None
    load_errors: list = field(default_factory=list)

    @property
    def row_id(self):
        return self.run_id

    @property
    def label(self):
        return self.run.get("label")

    @property
    def profile(self) -> RowProfile | None:
        return ROW_PROFILES.get(self.run_id)

    @property
    def tick_frequency(self):
        """(endedTicks - startedTicks) / wallSeconds, derived per run; never hardcoded."""
        started = as_number(self.run.get("startedTicks"))
        ended = as_number(self.run.get("endedTicks"))
        wall = as_number(self.run.get("wallSeconds"))
        if started is None or ended is None or not wall:
            return None
        ticks = ended - started
        if ticks <= 0:
            return None
        return ticks / wall

    def ticks_to_seconds(self, ticks):
        frequency = self.tick_frequency
        if frequency is None or ticks is None:
            return None
        return ticks / frequency

    def arm_entry(self, arm_name):
        for entry in self.run.get("arms", []) or []:
            if entry.get("name") == arm_name:
                return entry
        return None

    def arm_window_seconds(self, arm_name):
        entry = self.arm_entry(arm_name)
        if entry is None:
            return None
        started = as_number(entry.get("startedTicks"))
        ended = as_number(entry.get("endedTicks"))
        if started is None or ended is None:
            return None
        return self.ticks_to_seconds(ended - started)

    def arm_start_ticks(self, arm_name):
        entry = self.arm_entry(arm_name)
        return None if entry is None else as_number(entry.get("startedTicks"))

    def arm_utc_window(self, arm_name):
        """(start, end) UTC datetimes for one arm, from the client's own ticks.

        The run's startedUtc and startedTicks are written next to each other, so an
        arm's tick window converts to UTC by that offset. It is the only bridge
        between the client's stopwatch and the ledger's wall clock.
        """
        entry = self.arm_entry(arm_name)
        frequency = self.tick_frequency
        base_utc = parse_utc(self.run.get("startedUtc"))
        base_ticks = as_number(self.run.get("startedTicks"))
        if entry is None or frequency is None or base_utc is None or base_ticks is None:
            return None
        started = as_number(entry.get("startedTicks"))
        ended = as_number(entry.get("endedTicks"))
        if started is None or ended is None:
            return None
        return (
            base_utc + timedelta(seconds=(started - base_ticks) / frequency),
            base_utc + timedelta(seconds=(ended - base_ticks) / frequency),
        )


@dataclass
class MetricValue:
    """One cell's worth of per-pass data plus the reasons for the passes without one."""

    row_id: str
    values: dict = field(default_factory=dict)  # pass_id -> float
    reasons: dict = field(default_factory=dict)  # pass_id -> reason
    status: str = "ok"  # ok | not-in-plan | declared-absent | not-carried | dns-carriage
    status_reason: str | None = None

    @property
    def null_passes(self):
        return sum(1 for why in self.reasons.values() if why == NULL_RATE_REASON)

    def sorted_values(self):
        return [self.values[key] for key in sorted(self.values, key=natural_key)]

    def reason_summary(self, limit=2):
        unique = sorted(set(self.reasons.values()))
        return "; ".join(unique[:limit]) if unique else "no data"


@dataclass
class LedgerData:
    path: Path
    records: list = field(default_factory=list)
    bad_lines: int = 0
    load_errors: list = field(default_factory=list)

    @property
    def labels(self):
        counts = {}
        for record in self.records:
            label = record.get("label")
            counts[label] = counts.get(label, 0) + 1
        return counts

    @property
    def types(self):
        counts = {}
        for record in self.records:
            kind = record.get("type")
            counts[kind] = counts.get(kind, 0) + 1
        return counts


@dataclass
class Finding:
    severity: str
    kind: str
    scope: str
    detail: str

    def as_dict(self):
        return {"severity": self.severity, "kind": self.kind, "scope": self.scope, "detail": self.detail}


@dataclass
class Context:
    raw: Path
    out: Path
    flat: bool
    passes: dict  # pass_id -> list of RunData
    warmup_seconds: float
    resamples: int
    seed: int
    min_passes: int
    environment: dict | None = None
    order: dict = field(default_factory=dict)  # pass_id -> row ids as the orchestrator ran them
    ledger_paths: dict = field(default_factory=dict)  # pass_id -> Path
    ledgers: dict = field(default_factory=dict)  # pass_id -> LedgerData
    findings: list = field(default_factory=list)

    @property
    def rows(self):
        out = []
        for pass_id in self.pass_ids:
            out.extend(self.passes[pass_id])
        return out

    @property
    def pass_ids(self):
        return sorted(self.passes, key=natural_key)

    @property
    def row_ids(self):
        seen = []
        for row in self.rows:
            if row.row_id not in seen:
                seen.append(row.row_id)
        rank = {row_id: index for index, row_id in enumerate(ROW_ORDER)}
        return sorted(seen, key=lambda row_id: (rank.get(row_id, len(rank)), natural_key(row_id)))

    @property
    def measured_row_ids(self):
        return [row_id for row_id in self.row_ids if row_id not in CONTROL_ROWS]

    def row_in(self, pass_id, row_id):
        for row in self.passes.get(pass_id, []):
            if row.row_id == row_id:
                return row
        return None

    def control_in(self, pass_id, row_id):
        return self.row_in(pass_id, row_id)


def is_control(row_id) -> bool:
    return row_id in CONTROL_ROWS


def plan_arms(row_id):
    profile = ROW_PROFILES.get(row_id)
    if profile is None:
        return None
    return PLAN_ARMS.get(profile.plan)




def load_arm(name, path):
    arm = ArmData(name=name, file=path.name)
    if not path.is_file():
        return arm
    with path.open("r", encoding="utf-8-sig", errors="replace") as handle:
        for line in handle:
            line = line.strip()
            if not line:
                continue
            try:
                record = json.loads(line)
            except json.JSONDecodeError:
                arm.bad_lines += 1
                continue
            if not isinstance(record, dict):
                arm.bad_lines += 1
                continue
            kind = record.get("type")
            if kind == "result":
                arm.result = record
            elif kind == "armSummary":
                arm.arm_summary = record
            elif kind == "sample":
                arm.samples.append(record)
            elif kind == "samplerError":
                arm.sampler_errors.append(record)
            elif kind == "error":
                arm.errors.append(record)
    return arm


def _load_json_file(path, row, label):
    if not path.is_file():
        return None
    try:
        return json.loads(path.read_text(encoding="utf-8-sig"))
    except (OSError, json.JSONDecodeError) as exc:
        row.load_errors.append("%s unreadable (%s)" % (label, exc.__class__.__name__))
        return None


def load_run(pass_id, run_id, path, require_truth=True, is_lane=False):
    run = RunData(pass_id=pass_id, run_id=run_id, path=path)
    run.run = _load_json_file(path / "run.json", run, "run.json") or {}
    if not run.run:
        run.load_errors.append("no run.json")

    files = {}
    for entry in run.run.get("arms", []) or []:
        name = entry.get("name")
        if name:
            files[name] = entry.get("file") or ("%s.jsonl" % name)
    for candidate in sorted(path.glob("*.jsonl")):
        files.setdefault(candidate.stem, candidate.name)
    for name in ARM_ORDER:
        if name in files:
            run.arms[name] = load_arm(name, path / files[name])
    for name in sorted(files):
        if name not in run.arms:
            run.arms[name] = load_arm(name, path / files[name])

    truth_path = path / "proxy-truth.json"
    if truth_path.is_file():
        run.proxy_truth = _load_json_file(truth_path, run, "proxy-truth.json")
    elif require_truth and not is_lane:
        run.load_errors.append("no proxy-truth.json")

    if not is_lane:
        for config in sorted(path.glob("config*")):
            if config.is_file():
                try:
                    run.configs.append((config.name, sha256_prefix(config), config.stat().st_size))
                except OSError:
                    run.configs.append((config.name, "unreadable", 0))
    return run


def announce_dual_owner(row: RunData, dual_dir: Path):
    """Attach a dual directory to the row its lanes name, or to this row when they name none.

    The shipped orchestrator writes one ``<pass>/dual`` for the whole pass and rebuilds it for
    every dual row, so the lanes inside it belong to the row named by their labels rather than to
    whichever row is being loaded.
    """
    lanes = {}
    for lane in ("proxied", "direct"):
        lane_dir = dual_dir / lane
        if lane_dir.is_dir():
            lanes[lane] = load_run(row.pass_id, "%s/%s" % (row.run_id, lane), lane_dir, require_truth=False, is_lane=True)
    owner = None
    for run in lanes.values():
        label = run.label
        if isinstance(label, str) and "-dual-" in label:
            owner = label.split("-dual-", 1)[0]
            break
    for lane, run in lanes.items():
        if owner is not None and owner != row.run_id:
            run.run_id = "%s/%s" % (owner, lane)
        setattr(row, "dual_" + lane, run)
    if lanes:
        row.dual_truth = _load_json_file(dual_dir / "proxy-truth.json", row, "dual/proxy-truth.json")
        if row.dual_truth is None:
            row.load_errors.append("no dual/proxy-truth.json")
    return owner


def load_dual(row: RunData):
    dual_dir = row.path / "dual"
    if dual_dir.is_dir():
        announce_dual_owner(row, dual_dir)


def looks_like_row(path: Path) -> bool:
    """A row directory holds run.json or at least one arm file."""
    return path.is_dir() and ((path / "run.json").is_file() or any(path.glob("*.jsonl")))


LEDGER_NAMES = ("target-ledger.jsonl", "ledger.jsonl", "ledger-main.jsonl", "ledger-direct.jsonl")


def find_ledger_paths(raw: Path, pass_dir: Path | None, overrides):
    """Every ledger that could belong to this pass, in a stable order.

    A campaign can run more than one target instance — the shipped launcher starts a proxied
    target and a separate direct-lane target, each with its own ledger — so a single "the
    ledger" is not enough. ``--ledger`` may be repeated and is used verbatim; otherwise the
    pass directory, the raw directory and the raw directory's parent are searched.
    """
    if overrides:
        return [Path(path) for path in overrides if Path(path).is_file()]
    seen = set()
    found = []
    bases = [pass_dir] if pass_dir is not None else []
    bases += [raw, raw.parent]
    for base in bases:
        if base is None:
            continue
        try:
            candidates = [base / name for name in LEDGER_NAMES] + sorted(base.glob("*ledger*.jsonl"))
        except OSError:
            continue
        for candidate in candidates:
            key = str(candidate)
            if key in seen or not candidate.is_file():
                continue
            seen.add(key)
            found.append(candidate)
    return found


def load_ledger(path: Path) -> LedgerData:
    ledger = LedgerData(path=path)
    try:
        handle = path.open("r", encoding="utf-8-sig", errors="replace")
    except OSError as exc:
        ledger.load_errors.append("ledger unreadable (%s)" % exc.__class__.__name__)
        return ledger
    with handle:
        for line in handle:
            line = line.strip()
            if not line:
                continue
            try:
                record = json.loads(line)
            except json.JSONDecodeError:
                ledger.bad_lines += 1
                continue
            if not isinstance(record, dict):
                ledger.bad_lines += 1
                continue
            record["_utc"] = parse_utc(record.get("utc"))
            ledger.records.append(record)
    return ledger


def discover(raw: Path, flat: bool, ledger_override: Path | None):
    """Return ({pass_id: [RunData]}, order, {pass_id: LedgerData}, {pass_id: Path})."""
    passes = {}
    order = {}
    ledgers = {}
    ledger_paths = {}

    override_list = ledger_override if isinstance(ledger_override, (list, tuple)) else ([ledger_override] if ledger_override else [])

    def attach_ledger(pass_id, pass_dir):
        paths = find_ledger_paths(raw, pass_dir, override_list)
        if paths:
            ledger_paths[pass_id] = paths
            ledgers[pass_id] = [load_ledger(path) for path in paths]

    if flat:
        if looks_like_row(raw):
            row = load_run("flat", raw.name, raw)
            load_dual(row)
            passes["flat"] = [row]
            attach_ledger("flat", raw)
            return passes, order, ledgers, ledger_paths
        rows = []
        for child in sorted(raw.iterdir(), key=lambda p: natural_key(p.name)):
            if child.is_dir() and looks_like_row(child):
                row = load_run("flat", child.name, child)
                load_dual(row)
                rows.append(row)
        if rows:
            passes["flat"] = rows
        attach_ledger("flat", raw)
        return passes, order, ledgers, ledger_paths

    for child in sorted(raw.iterdir(), key=lambda p: natural_key(p.name)):
        if not child.is_dir() or not child.name.startswith("pass"):
            continue
        rows = []
        pass_dual = None
        for row_dir in sorted(child.iterdir(), key=lambda p: natural_key(p.name)):
            if row_dir.name == "dual":
                pass_dual = row_dir
                continue
            if looks_like_row(row_dir):
                row = load_run(child.name, row_dir.name, row_dir)
                load_dual(row)
                rows.append(row)
        if pass_dual is not None and rows:
            owners = {row.run_id for row in rows if row.dual_proxied is not None or row.dual_direct is not None}
            unclaimed = [row for row in rows if row.run_id not in owners]
            for row in unclaimed:
                if announce_dual_owner(row, pass_dual) == row.run_id:
                    break
        if rows:
            passes[child.name] = rows
        attach_ledger(child.name, child)
        order_file = child / "order.txt"
        if order_file.is_file():
            try:
                text = order_file.read_text(encoding="utf-8").strip()
                order[child.name] = [part for part in re.split(r"[,\s]+", text) if part]
            except OSError:
                pass
    return passes, order, ledgers, ledger_paths




def arm_result(row, arm_name):
    """(result record, reason) for one arm of one row."""
    arm = row.arms.get(arm_name)
    if arm is None:
        return None, "no %s arm" % arm_name
    if arm.result is None:
        return None, "%s arm has no result record" % arm_name
    return arm.result, None


IDENTITY_BLOCKED_PREFIXES = {
    "LOSS": ("metrics/",),
    "MIX": ("metrics/classes/udp/", "metrics/udpSent", "metrics/udpLossRate", "metrics/clientSendLoss"),
    "BASE": ("metrics/loss/",),
}


def identity_blocked_prefix(row, arm_name):
    """(prefix, detail) when a failed UDP identity makes this arm's UDP fields unreadable.

    The classification identity is asserted per record; a violation is a harness error, and
    the affected fields are excluded from every aggregate rather than averaged over.
    """
    if arm_name not in IDENTITY_BLOCKED_PREFIXES:
        return None, None
    for check_arm, kind, ok, detail in identity_checks(row):
        if check_arm == arm_name and kind == "udp-identity" and ok is False:
            for prefix in IDENTITY_BLOCKED_PREFIXES[arm_name]:
                return prefix, detail
    return None, None


def arm_number(row, arm_name, path):
    """(value, reason) for a numeric metric at a SEP path inside the arm result."""
    result, why = arm_result(row, arm_name)
    if result is None:
        return None, why
    blocked, detail = identity_blocked_prefix(row, arm_name)
    if blocked is not None and path.startswith(blocked):
        return None, "harness error: UDP identity violated (%s); excluded rather than averaged over" % detail
    present, value = dig_present(result, path)
    dotted = path.replace(SEP, ".")
    if not present:
        return None, "%s %s missing" % (arm_name, dotted)
    if value is None:
        return None, NULL_RATE_REASON
    number = as_number(value)
    if number is None:
        return None, "%s %s is not a number" % (arm_name, dotted)
    return number, None


def arm_flag(row, arm_name, path):
    """(bool, reason) for a boolean metric (survivedIdle)."""
    result, why = arm_result(row, arm_name)
    if result is None:
        return None, why
    present, value = dig_present(result, path)
    if not present:
        return None, "%s %s missing" % (arm_name, path.replace(SEP, "."))
    if isinstance(value, bool):
        return value, None
    return None, "%s %s is not a boolean" % (arm_name, path.replace(SEP, "."))


def arm_text(row, arm_name, path):
    result, why = arm_result(row, arm_name)
    if result is None:
        return None, why
    present, value = dig_present(result, path)
    if not present or value is None:
        return None, "%s %s missing" % (arm_name, path.replace(SEP, "."))
    return value, None


def arm_ratio(row, arm_name, numerator_path, denominator_path):
    """(numerator / denominator, reason) with an explicit zero-denominator reason."""
    numerator, why = arm_number(row, arm_name, numerator_path)
    if numerator is None:
        return None, why
    denominator, why = arm_number(row, arm_name, denominator_path)
    if denominator is None:
        return None, why
    if denominator == 0:
        return None, "%s %s is zero" % (arm_name, denominator_path.replace(SEP, "."))
    return numerator / denominator, None


def arm_latency(row, arm_name, latency_class, stat):
    """One histogram statistic straight from the harness's own histogram."""
    result, why = arm_result(row, arm_name)
    if result is None:
        return None, why
    histogram = dig(result, "latency" + SEP + latency_class)
    if not isinstance(histogram, dict):
        return None, "%s has no %s histogram" % (arm_name, latency_class)
    value = as_number(histogram.get(stat))
    if value is None:
        return None, "%s %s.%s missing" % (arm_name, latency_class, stat)
    return value, None


def arm_samples(row, arm_name, self_only=False):
    arm = row.arms.get(arm_name)
    if arm is None:
        return []
    return [s for s in arm.samples if (s.get("self") is True) == self_only]


def product_samples(row):
    """Non-self samples; 'absent' records carry no counters and are kept for presence."""
    return [
        sample
        for arm in row.arms.values()
        for sample in arm.samples
        if sample.get("self") is not True
    ]


def self_samples(row):
    return [sample for arm in row.arms.values() for sample in arm.samples if sample.get("self") is True]


def sampler_errors(row):
    return [record for arm in row.arms.values() for record in arm.sampler_errors]


def primary_product_process(row):
    """The sampled process name with the most records (the campaign samples one)."""
    counts = {}
    for sample in product_samples(row):
        name = sample.get("process") or ""
        counts[name] = counts.get(name, 0) + 1
    if not counts:
        return None, []
    ranked = sorted(counts.items(), key=lambda kv: (-kv[1], kv[0]))
    return ranked[0][0], ranked


def sample_is_readable(sample) -> bool:
    """A sample whose readError is true is a partially read sample and is rejected.

    The harness writes null (not 0) for a process whose counters could not be read and
    marks the whole sample with readError; averaging it in would report a measurement
    that did not happen.
    """
    return not sample.get("readError")


def process_private_bytes(sample):
    """The sample's summed private bytes, preferring the per-process block."""
    processes = sample.get("processes")
    if isinstance(processes, list) and processes:
        total = 0.0
        found = False
        for proc in processes:
            value = as_number(proc.get("privateBytes")) if isinstance(proc, dict) else None
            if value is not None:
                total += value
                found = True
        if found:
            return total
    return as_number(sample.get("privateBytes"))


def present_product_samples(row, arm_name, primary, require_counters=True):
    """Readable samples of the primary product process that actually carry counters."""
    out = []
    for sample in arm_samples(row, arm_name):
        if primary is not None and sample.get("process") != primary:
            continue
        if not sample_is_readable(sample):
            continue
        matched = as_number(sample.get("matched"))
        if require_counters and not (matched and matched > 0):
            continue
        if require_counters and not (
            is_number(sample.get("privateBytes")) or is_number(sample.get("cpuSeconds"))
        ):
            continue
        out.append(sample)
    return out


def steady_samples(row, arm_name, primary, warmup_seconds):
    """Readable product samples after the arm's own warmup window (falls back to all)."""
    samples = present_product_samples(row, arm_name, primary)
    started = row.arm_start_ticks(arm_name)
    frequency = row.tick_frequency
    if started is None or frequency is None:
        return samples
    cut = started + warmup_seconds * frequency
    kept = [s for s in samples if as_number(s.get("ticks")) is not None and s["ticks"] >= cut]
    return kept or samples


def sample_identities(sample):
    """[(identity, cpuSeconds, countersRead)] for one sample's processes[] block.

    The identity of a summed counter is (pid, startUtc): Windows recycles process ids, so
    the id alone cannot tell a restart from the process that was already there.
    """
    out = []
    for proc in sample.get("processes") or []:
        if not isinstance(proc, dict):
            continue
        key = (proc.get("pid"), proc.get("startUtc"))
        out.append((key, as_number(proc.get("cpuSeconds")), bool(proc.get("countersRead"))))
    return out


def cpu_detail(samples, tick_frequency, fallback_field="cpuSeconds"):
    """(cpu percent of one vCPU, reason, diagnostics) summed per process identity.

    Each process identity contributes (last - first) of its own cumulative CPU counter,
    so a process that restarted mid-run cannot corrupt the sum the way a first/last
    difference over a process *name* does. Unreadable counters are null in the record and
    those samples are rejected by the caller, so they can never be averaged as a zero.
    """
    diagnostics = {
        "identities": 0,
        "identities_used": 0,
        "skipped_single_point": 0,
        "negative_delta": 0,
        "restarts": 0,
        "samples": len(samples),
    }
    if tick_frequency is None:
        return None, "no tick frequency in run.json", diagnostics
    usable = [s for s in samples if sample_is_readable(s) and as_number(s.get("ticks")) is not None]
    diagnostics["samples"] = len(usable)
    if len(usable) < 2:
        return None, "fewer than two readable samples", diagnostics

    per_identity = {}
    fallback_series = []
    for sample in usable:
        ticks = as_number(sample.get("ticks"))
        identities = sample_identities(sample)
        if not identities:
            value = as_number(sample.get(fallback_field))
            if value is not None:
                fallback_series.append((ticks, value))
            continue
        for key, cpu, read in identities:
            if read and cpu is not None:
                per_identity.setdefault(key, []).append((ticks, cpu))

    if not per_identity and fallback_series:
        # Records written before the per-process block existed carry only the sum.
        per_identity[("sum", None)] = fallback_series

    diagnostics["identities"] = len(per_identity)
    starts = {key[1] for key in per_identity}
    diagnostics["restarts"] = max(0, len(starts) - 1)
    ticks_all = [as_number(s.get("ticks")) for s in usable]
    t0, t1 = min(ticks_all), max(ticks_all)
    if t1 <= t0:
        return None, "non-increasing sample ticks", diagnostics

    total = 0.0
    for points in per_identity.values():
        points.sort()
        if len(points) < 2:
            diagnostics["skipped_single_point"] += 1
            continue
        delta = points[-1][1] - points[0][1]
        if delta < 0:
            diagnostics["negative_delta"] += 1
            continue
        total += delta
        diagnostics["identities_used"] += 1
    if diagnostics["identities_used"] == 0:
        return None, "no process identity with two readable samples", diagnostics
    return 100.0 * total * tick_frequency / (t1 - t0), None, diagnostics


def cpu_percent(samples, field_name, tick_frequency):
    """Back-compatible wrapper: (percent, reason) only."""
    value, why, _ = cpu_detail(samples, tick_frequency, fallback_field=field_name)
    return value, why




def udp_identity(record, prefix):
    """(ok, detail) for the UDP classification identity the arms document.

    Arrived + late + never + abandonedAtTeardown + corruptDatagrams == sent, by
    construction, for every arm that publishes the full classification (LOSS, the MIX
    UDP class and the BASE loss phase). A violation is a harness error and the record is
    not averaged over.
    """
    fields = {}
    for name in ("sent", "arrived", "late", "never", "abandonedAtTeardown", "corruptDatagrams"):
        present, value = dig_present(record, prefix + SEP + name if prefix else name)
        if not present or not is_number(value):
            return None, "%s missing from the record" % name
        fields[name] = float(value)
    total = sum(fields[name] for name in ("arrived", "late", "never", "abandonedAtTeardown", "corruptDatagrams"))
    if total != fields["sent"]:
        return False, (
            "arrived(%d) + late(%d) + never(%d) + abandonedAtTeardown(%d) + corruptDatagrams(%d) = %d != sent(%d)"
            % (
                fields["arrived"],
                fields["late"],
                fields["never"],
                fields["abandonedAtTeardown"],
                fields["corruptDatagrams"],
                total,
                fields["sent"],
            )
        )
    return True, None


def dns_partition(record):
    """(ok, detail) for answered + servfail + timeout + other == sent."""
    values = {}
    for name in ("sent", "answered", "servfail", "timeout", "other"):
        present, value = dig_present(record, "metrics" + SEP + name)
        if not present or not is_number(value):
            return None, "%s missing from the record" % name
        values[name] = float(value)
    total = values["answered"] + values["servfail"] + values["timeout"] + values["other"]
    if total != values["sent"]:
        return False, (
            "answered(%d) + servfail(%d) + timeout(%d) + other(%d) = %d != sent(%d)"
            % (values["answered"], values["servfail"], values["timeout"], values["other"], total, values["sent"])
        )
    return True, None


def reliability_invariants(record):
    """(ok, detail) for connectAttempts == scheduledAttempts and the outcome partition."""
    attempts, why = None, None
    present, value = dig_present(record, "metrics" + SEP + "connectAttempts")
    if not present or not is_number(value):
        return None, "metrics.connectAttempts missing from the record"
    attempts = float(value)
    present, scheduled = dig_present(record, "metrics" + SEP + "scheduledAttempts")
    if not present or not is_number(scheduled):
        return None, "metrics.scheduledAttempts not published (older record)"
    if float(scheduled) != attempts:
        return False, "connectAttempts(%d) != scheduledAttempts(%d): the arm ended with work in flight" % (
            attempts,
            float(scheduled),
        )
    outcomes = dig(record, "metrics" + SEP + "outcomes")
    if not isinstance(outcomes, dict):
        return None, "metrics.outcomes missing from the record"
    total = sum(float(v) for v in outcomes.values() if is_number(v))
    if total != attempts:
        return False, "outcomes sum to %d != connectAttempts(%d)" % (total, attempts)
    return True, None


def lane_witnesses(row, arm_name):
    """[(label, witness value, ok)] for the lane witnesses of one arm.

    A zero witness means a lane never ran: the record must not read as a smaller
    aggregate of a complete run. Returns None when the arm publishes no witnesses.
    """
    result, _ = arm_result(row, arm_name)
    if result is None:
        return None
    kind = dig(result, "kind")
    witnesses = []
    if kind == "latency":
        planned = as_number(dig(result, "gates" + SEP + "lanesPlanned"))
        started = as_number(dig(result, "gates" + SEP + "lanesStarted"))
        if planned is not None:
            witnesses.append(("gates.lanesStarted == gates.lanesPlanned (%s of %s)" % (fmt_num(started, 0), fmt_num(planned, 0)), started == planned))
        supplied = dig(result, "metrics" + SEP + "tcp.laneSupplied")
        if isinstance(supplied, list):
            for index, value in enumerate(supplied):
                witnesses.append(("tcp.laneSupplied[%d] > 0 (=%s)" % (index, fmt_num(as_number(value), 0)), (as_number(value) or 0) > 0))
        udp_started = as_number(dig(result, "metrics" + SEP + "udp.laneStarted"))
        protocol = dig(result, "parameters" + SEP + "protocol")
        if udp_started is not None and isinstance(protocol, str) and "udp" in protocol:
            witnesses.append(("udp.laneStarted > 0 (=%s)" % fmt_num(udp_started, 0), udp_started > 0))
    elif kind == "mix":
        desktops = dig(result, "metrics" + SEP + "desktops")
        if isinstance(desktops, list):
            for entry in desktops:
                if not isinstance(entry, dict):
                    continue
                desktop = entry.get("desktop")
                for field in ("udpSent", "pageConnections", "bulkFrames", "dnsSent"):
                    value = as_number(entry.get(field))
                    witnesses.append(("desktop %s %s > 0 (=%s)" % (desktop, field, fmt_num(value, 0)), (value or 0) > 0))
    return witnesses or None


def identity_checks(row):
    """[(arm, label, ok, detail)] for every per-record identity this script asserts."""
    checks = []
    for arm_name, prefix in (("LOSS", "metrics"), ("MIX", "metrics/classes/udp"), ("BASE", "metrics/loss")):
        result, _ = arm_result(row, arm_name)
        if result is None:
            continue
        ok, detail = udp_identity(result, prefix)
        if ok is not None:
            checks.append((arm_name, "udp-identity", ok, detail))
    for arm_name in ("DNS", "DNSALT"):
        result, _ = arm_result(row, arm_name)
        if result is None:
            continue
        ok, detail = dns_partition(result)
        if ok is not None:
            checks.append((arm_name, "dns-partition", ok, detail))
    result, _ = arm_result(row, "REL")
    if result is not None:
        ok, detail = reliability_invariants(result)
        if ok is not None:
            checks.append(("REL", "scheduled-attempts", ok, detail))
    return checks


def foreign_connection_sources(row):
    """[(arm, label, count)] for every foreignConnection counter in the record."""
    out = []
    for arm_name, path, label in (
        ("LOSS", "metrics/foreignConnection", "LOSS.foreignConnection"),
        ("MIX", "metrics/classes/udp/foreignConnection", "MIX.udp.foreignConnection"),
        ("LAT", "metrics/udp.foreignConnection", "LAT.udp.foreignConnection"),
        ("LATLOAD", "metrics/udp.foreignConnection", "LATLOAD.udp.foreignConnection"),
        ("BASE", "metrics/loss/foreignConnection", "BASE.loss.foreignConnection"),
    ):
        value, _ = arm_number(row, arm_name, path)
        if value:
            out.append((arm_name, label, value))
    result, _ = arm_result(row, "MIX")
    mix_total, _ = arm_number(row, "MIX", "metrics/classes/udp/foreignConnection")
    if result is not None and not mix_total:
        desktops = dig(result, "metrics" + SEP + "desktops")
        if isinstance(desktops, list):
            for entry in desktops:
                if isinstance(entry, dict) and (as_number(entry.get("udpForeignConnection")) or 0) > 0:
                    out.append(
                        (
                            "MIX",
                            "MIX.desktop %s udpForeignConnection" % entry.get("desktop"),
                            as_number(entry.get("udpForeignConnection")),
                        )
                    )
    return out




def metric_lat_p50(row):
    return arm_latency(row, "LAT", "tcp-rtt", "p50Us")


def metric_lat_p99(row):
    return arm_latency(row, "LAT", "tcp-rtt", "p99Us")


def metric_latload_p50(row):
    return arm_latency(row, "LATLOAD", "tcp-rtt", "p50Us")


def metric_latload_p99(row):
    return arm_latency(row, "LATLOAD", "tcp-rtt", "p99Us")


def metric_rate(row, arm_name, path):
    value, why = arm_number(row, arm_name, path)
    return (None if value is None else value * 100.0), why


def metric_ratio_rate(row, arm_name, numerator_path, denominator_path):
    value, why = arm_ratio(row, arm_name, numerator_path, denominator_path)
    return (None if value is None else value * 100.0), why


def metric_mix_udp_loss_rate(row):
    value, why = arm_number(row, "MIX", "metrics/classes/udp/lossRate")
    if value is None and why != NULL_RATE_REASON:
        value, why = arm_number(row, "MIX", "metrics/udpLossRate")
    return (None if value is None else value * 100.0), why


def metric_lat_udp_loss_rate(row):
    value, why = arm_number(row, "LAT", "metrics/udp.lossRate")
    return (None if value is None else value * 100.0), why


def metric_lat_udp_rtt_p50(row):
    return arm_latency(row, "LAT", "udp-rtt", "p50Us")


def metric_persist_reconnects(row):
    return arm_number(row, "PERSIST", "metrics/reconnects")


def metric_persist_response_rate(row):
    return metric_rate(row, "PERSIST", "metrics/responseRate")


def metric_loss_foreign(row):
    return arm_number(row, "LOSS", "metrics/foreignConnection")


def metric_memory_private_p50(ctx, row):
    """Per-pass p50 of the product's steady-state private bytes."""
    primary, _ = primary_product_process(row)
    if primary is None:
        return None, "no product process was sampled (run.json samplerProcesses is empty)"
    values = []
    for arm_name in row.arms:
        for sample in steady_samples(row, arm_name, primary, ctx.warmup_seconds):
            value = process_private_bytes(sample)
            if value is not None:
                values.append(value / MIB)
    if not values:
        return None, "no steady-state product samples"
    return quantile(values, 0.50), None


def metric_cpu_proxy_vcpu(ctx, row):
    """Row-level proxy CPU over the loaded arms (IDLE excluded, see caption)."""
    primary, _ = primary_product_process(row)
    if primary is None:
        return None, "no product process was sampled (run.json samplerProcesses is empty)"
    series = [
        sample
        for arm_name in row.arms
        if arm_name != "IDLE"
        for sample in present_product_samples(row, arm_name, primary)
    ]
    series = [s for s in series if as_number(s.get("ticks")) is not None]
    series.sort(key=lambda s: s["ticks"])
    value, why, _ = cpu_detail(series, row.tick_frequency)
    return value, why


# Unit legend for the metric registry: 'pp' percentage points (0.5 pp == 0.005 as
# a fraction), 'us' microseconds, 'Mbps' megabits per second, 'MiB' mebibytes,
# '%vcpu' percent of one logical processor, 'count' an event count.
#
# 'arm' is the arm the metric is read from (None for the sampling metrics), 'udp_path'
# marks a metric whose value rides on a UDP path ('udp' = the UDP echo arms, 'dns' = a
# DNS arm), and 'dns53' marks the port-53 DNS arm, whose carriage differs per row.
METRIC_SPECS = [
    {"key": "lat.tcp_rtt.p50", "label": "LAT tcp-rtt p50", "unit": "us", "digits": 1, "family": "latency",
     "arm": "LAT", "extract": lambda ctx, row: metric_lat_p50(row)},
    {"key": "lat.tcp_rtt.p99", "label": "LAT tcp-rtt p99", "unit": "us", "digits": 1, "family": "latency",
     "arm": "LAT", "extract": lambda ctx, row: metric_lat_p99(row)},
    {"key": "lat.udp_rtt.p50", "label": "LAT udp-rtt p50", "unit": "us", "digits": 1, "family": "latency",
     "arm": "LAT", "udp_path": "udp", "extract": lambda ctx, row: metric_lat_udp_rtt_p50(row)},
    {"key": "lat.udp_lossRate", "label": "LAT udp lossRate", "unit": "pp", "digits": 4, "family": "udp-loss",
     "arm": "LAT", "udp_path": "udp", "extract": lambda ctx, row: metric_lat_udp_loss_rate(row)},
    {"key": "latload.tcp_rtt.p50", "label": "LATLOAD tcp-rtt p50", "unit": "us", "digits": 1, "family": "latency",
     "arm": "LATLOAD", "extract": lambda ctx, row: metric_latload_p50(row)},
    {"key": "latload.tcp_rtt.p99", "label": "LATLOAD tcp-rtt p99", "unit": "us", "digits": 1, "family": "latency",
     "arm": "LATLOAD", "extract": lambda ctx, row: metric_latload_p99(row)},
    {"key": "loss.lossRate", "label": "LOSS lossRate", "unit": "pp", "digits": 4, "family": "udp-loss",
     "arm": "LOSS", "udp_path": "udp", "extract": lambda ctx, row: metric_rate(row, "LOSS", "metrics/lossRate")},
    {"key": "loss.corruptRate", "label": "LOSS corruptRate", "unit": "pp", "digits": 4, "family": "udp-loss",
     "arm": "LOSS", "udp_path": "udp",
     "extract": lambda ctx, row: metric_rate(row, "LOSS", "metrics/corruptRate")},
    {"key": "loss.foreignConnection", "label": "LOSS foreignConnection", "unit": "count", "digits": 0,
     "family": "none", "arm": "LOSS", "udp_path": "udp", "extract": lambda ctx, row: metric_loss_foreign(row)},
    {"key": "rel.unexpectedEofRate", "label": "REL unexpectedEofRate", "unit": "pp", "digits": 4,
     "family": "tcp-unexpected", "arm": "REL",
     "extract": lambda ctx, row: metric_ratio_rate(row, "REL", "metrics/unexpectedEof", "metrics/connectAttempts")},
    {"key": "rel.fidelityRate", "label": "REL fidelityRate", "unit": "pp", "digits": 4, "family": "tcp-unexpected",
     "arm": "REL",
     "extract": lambda ctx, row: metric_ratio_rate(row, "REL", "metrics/fidelityMismatch", "metrics/connectAttempts")},
    {"key": "dns.answerRate", "label": "DNS(53) answerRate", "unit": "pp", "digits": 4, "family": "udp-loss",
     "arm": "DNS", "udp_path": "dns", "dns53": True,
     "extract": lambda ctx, row: metric_rate(row, "DNS", "metrics/answerRate")},
    {"key": "dns.rtt.p50", "label": "DNS(53) dns-rtt p50", "unit": "us", "digits": 1, "family": "latency",
     "arm": "DNS", "udp_path": "dns", "dns53": True,
     "extract": lambda ctx, row: arm_latency(row, "DNS", "dns-rtt", "p50Us")},
    {"key": "dnsalt.answerRate", "label": "DNSALT answerRate", "unit": "pp", "digits": 4, "family": "udp-loss",
     "arm": "DNSALT", "udp_path": "dns",
     "extract": lambda ctx, row: metric_rate(row, "DNSALT", "metrics/answerRate")},
    {"key": "dnsalt.rtt.p50", "label": "DNSALT dns-rtt p50", "unit": "us", "digits": 1, "family": "latency",
     "arm": "DNSALT", "udp_path": "dns",
     "extract": lambda ctx, row: arm_latency(row, "DNSALT", "dns-rtt", "p50Us")},
    {"key": "thru.goodputMbps", "label": "THRU goodputMbps", "unit": "Mbps", "digits": 3, "family": "none",
     "arm": "THRU", "extract": lambda ctx, row: arm_number(row, "THRU", "metrics/goodputMbps")},
    {"key": "mix.udpLossRate", "label": "MIX udpLossRate", "unit": "pp", "digits": 4, "family": "udp-loss",
     "arm": "MIX", "udp_path": "udp", "extract": lambda ctx, row: metric_mix_udp_loss_rate(row)},
    {"key": "persist.responseRate", "label": "PERSIST responseRate", "unit": "pp", "digits": 4,
     "family": "tcp-unexpected", "arm": "PERSIST",
     "extract": lambda ctx, row: metric_persist_response_rate(row)},
    {"key": "persist.reconnects", "label": "PERSIST reconnects", "unit": "count", "digits": 1, "family": "none",
     "arm": "PERSIST", "extract": lambda ctx, row: metric_persist_reconnects(row)},
    {"key": "mem.privateBytes.p50", "label": "steady-state private bytes", "unit": "MiB", "digits": 2,
     "family": "memory", "arm": None, "extract": metric_memory_private_p50},
    {"key": "cpu.proxy.vcpuPct", "label": "proxy CPU", "unit": "%vcpu", "digits": 2, "family": "cpu",
     "arm": None, "extract": metric_cpu_proxy_vcpu},
]

METRIC_DEFINITIONS = {
    "lat.tcp_rtt.p50": "LAT arm, latency.tcp-rtt.p50Us",
    "lat.tcp_rtt.p99": "LAT arm, latency.tcp-rtt.p99Us",
    "lat.udp_rtt.p50": "LAT arm UDP lane, latency.udp-rtt.p50Us (proxied only where the row carries UDP)",
    "lat.udp_lossRate": "LAT arm UDP lane, metrics.udp.lossRate (percentage points of udp.sentOk)",
    "latload.tcp_rtt.p50": "LATLOAD arm, latency.tcp-rtt.p50Us",
    "latload.tcp_rtt.p99": "LATLOAD arm, latency.tcp-rtt.p99Us",
    "loss.lossRate": "LOSS arm, metrics.lossRate = (late + never) / sent (percentage points)",
    "loss.corruptRate": "LOSS arm, metrics.corruptRate (percentage points)",
    "loss.foreignConnection": "LOSS arm, metrics.foreignConnection: datagrams delivered into a different flow",
    "rel.unexpectedEofRate": "REL metrics.unexpectedEof / metrics.connectAttempts (percentage points)",
    "rel.fidelityRate": "REL metrics.fidelityMismatch / metrics.connectAttempts (percentage points)",
    "dns.answerRate": "port-53 DNS arm, metrics.answerRate (percentage points); carriage differs per row",
    "dns.rtt.p50": "port-53 DNS arm, latency.dns-rtt.p50Us; carriage differs per row",
    "dnsalt.answerRate": "DNSALT arm (a port no product special-cases), metrics.answerRate (percentage points)",
    "dnsalt.rtt.p50": "DNSALT arm (a port no product special-cases), latency.dns-rtt.p50Us",
    "thru.goodputMbps": "THRU metrics.goodputMbps",
    "mix.udpLossRate": "MIX metrics.classes.udp.lossRate (percentage points)",
    "persist.responseRate": "PERSIST metrics.responseRate = responses / requests (percentage points)",
    "persist.reconnects": "PERSIST metrics.reconnects: a long-lived connection that had to be replaced",
    "mem.privateBytes.p50": "per-pass p50 of the product's steady-state privateBytes, then median across passes",
    "cpu.proxy.vcpuPct": "row proxy CPU over the loaded arms (IDLE excluded), percent of one vCPU",
}


def metric_row_status(ctx, spec, row_id):
    """(status, reason) for one metric on one row, before any value is read.

    The status separates a *design* absence ("this row never ran that arm") from a gap in
    data that should be there, and from a metric this row's traffic cannot support (a
    product that does not carry UDP has no UDP accuracy to report).
    """
    profile = ROW_PROFILES.get(row_id)
    arm_name = spec.get("arm")
    if arm_name is not None:
        declared = plan_arms(row_id)
        observed = any(arm_name in row.arms for row in ctx.rows if row.row_id == row_id)
        if not observed:
            if declared is not None and arm_name not in declared:
                return "not-in-plan", "not measured in this row: %s is not in the %s plan" % (arm_name, profile.plan)
            if declared is not None:
                return "declared-absent", "%s is declared in the %s plan but no arm file or result was found" % (
                    arm_name,
                    profile.plan,
                )
            return "declared-absent", "%s was not measured" % arm_name
    if profile is not None and spec.get("udp_path") and profile.udp == UDP_NOT_CARRIED:
        return "not-carried", NOT_CARRIED_CELL
    if profile is not None and spec.get("dns53") and profile.udp53 != UDP53_RELAYED:
        return "dns-carriage", "port-53 arm is a direct-path measurement here: %s" % UDP53_LABEL[profile.udp53]
    return "ok", None


def per_pass_values(ctx, spec, row_id):
    """MetricValue for one metric and one row id: per-pass values, reasons and status."""
    cell = MetricValue(row_id=row_id)
    status, reason = metric_row_status(ctx, spec, row_id)
    cell.status, cell.status_reason = status, reason
    if status in ("not-in-plan", "declared-absent", "not-carried"):
        return cell
    for pass_id in ctx.pass_ids:
        row = ctx.row_in(pass_id, row_id)
        if row is None:
            cell.reasons[pass_id] = "row not measured in this pass"
            continue
        value, why = spec["extract"](ctx, row)
        if value is None:
            cell.reasons[pass_id] = why or "unavailable"
        else:
            cell.values[pass_id] = value
    return cell


def cell_text(cell: MetricValue, spec, zero_bound_n=None, bound_scale=None):
    """Render one metric cell, honouring the plan/not-carried/empty-cell conventions."""
    if cell.status == "not-carried":
        return NOT_CARRIED_CELL
    if cell.status in ("not-in-plan", "declared-absent"):
        return "n/a (%s)" % cell.status_reason
    values = cell.sorted_values()
    if values:
        return fmt_stat(
            values,
            digits=spec["digits"],
            unit="" if spec["unit"] == "count" else " " + spec["unit"],
            zero_bound_n=zero_bound_n,
            bound_scale=bound_scale,
            null_passes=cell.null_passes,
            missing_passes=len(cell.reasons) - cell.null_passes,
        )
    if not values and cell.null_passes:
        return ""
    if cell.status == "dns-carriage":
        return "n/a (%s)" % cell.reason_summary()
    return "n/a (%s)" % cell.reason_summary()




def collect_findings(ctx) -> list:
    """Every correctness failure, harness error and measurement caveat in one list."""
    findings = []

    def add(severity, kind, scope, detail):
        findings.append(Finding(severity, kind, scope, detail))

    for pass_id in ctx.pass_ids:
        for row in ctx.passes[pass_id]:
            profile = ROW_PROFILES.get(row.row_id)
            if row.dual_truth is None:
                if profile is not None and profile.dual:
                    add(
                        MEASUREMENT_CAVEAT,
                        "dual-truth-missing",
                        "%s/%s" % (pass_id, row.row_id),
                        "the row's plan declares a dual phase but dual/proxy-truth.json is missing, so directLeak cannot be checked",
                    )
                continue
            leak = as_number(row.dual_truth.get("directLeak"))
            if leak is None:
                add(
                    MEASUREMENT_CAVEAT,
                    "direct-leak-unreadable",
                    "%s/%s" % (pass_id, row.row_id),
                    "dual/proxy-truth.json has no numeric directLeak field",
                )
            elif leak > 0:
                add(
                    CORRECTNESS_FAILURE,
                    "direct-leak",
                    "%s/%s (dual/direct)" % (pass_id, row.row_id),
                    "directLeak=%d: the product intercepted %d application connection(s) it was configured to send direct"
                    % (leak, leak),
                )
            for lane in ("dual_proxied", "dual_direct"):
                lane_run = getattr(row, lane)
                if lane_run is None:
                    add(
                        MEASUREMENT_CAVEAT,
                        "dual-lane-missing",
                        "%s/%s" % (pass_id, row.row_id),
                        "dual/%s run directory is missing" % lane.split("_")[1],
                    )

    for pass_id in ctx.pass_ids:
        for row in ctx.passes[pass_id]:
            for arm_name, label, count in foreign_connection_sources(row):
                add(
                    CORRECTNESS_FAILURE,
                    "foreign-connection",
                    "%s/%s %s" % (pass_id, row.row_id, arm_name),
                    "%s=%s: the product delivered %s datagram(s) belonging to one flow into a different flow"
                    % (label, fmt_num(count, 0), fmt_num(count, 0)),
                )

    for pass_id in ctx.pass_ids:
        for row in ctx.passes[pass_id]:
            for arm_name, kind, ok, detail in identity_checks(row):
                if ok is False:
                    if kind == "scheduled-attempts":
                        add(
                            MEASUREMENT_CAVEAT,
                            kind,
                            "%s/%s %s" % (pass_id, row.row_id, arm_name),
                            "%s; the values are kept but every cell that carries them says so" % detail,
                        )
                    else:
                        add(
                            HARNESS_ERROR,
                            kind,
                            "%s/%s %s" % (pass_id, row.row_id, arm_name),
                            "%s; the record is excluded from the affected aggregates rather than averaged over" % detail,
                        )
                elif ok is None:
                    add(
                        MEASUREMENT_CAVEAT,
                        kind + "-uncheckable",
                        "%s/%s %s" % (pass_id, row.row_id, arm_name),
                        detail,
                    )

    for pass_id in ctx.pass_ids:
        for row in ctx.passes[pass_id]:
            for arm_name in ARM_ORDER:
                witnesses = lane_witnesses(row, arm_name)
                if witnesses is None:
                    continue
                for label, ok in witnesses:
                    if not ok:
                        add(
                            HARNESS_ERROR,
                            "lane-witness-zero",
                            "%s/%s %s" % (pass_id, row.row_id, arm_name),
                            "%s: the witness is zero, so that lane never ran and the arm measured less than its plan"
                            % label,
                        )
            idle, _ = arm_number(row, "MIX", "gates/idleLanes")
            if idle:
                add(
                    HARNESS_ERROR,
                    "idle-lanes",
                    "%s/%s MIX" % (pass_id, row.row_id),
                    "gates.idleLanes=%s: %s per-desktop flow-class witness(es) stayed at zero"
                    % (fmt_num(idle, 0), fmt_num(idle, 0)),
                )

    for pass_id in ctx.pass_ids:
        for row in ctx.passes[pass_id]:
            for arm_name in ("LAT", "LATLOAD", "BASE"):
                result, _ = arm_result(row, arm_name)
                if result is None:
                    continue
                overflow, _ = arm_number(row, arm_name, "gates/windowOverflow")
                backlog, _ = arm_number(row, arm_name, "gates/backlogDrops")
                truncated, _ = arm_number(row, arm_name, "gates/scheduleTruncated")
                ceiling, _ = arm_number(row, arm_name, "gates/inFlightCeilingMs")
                if backlog:
                    add(
                        HARNESS_ERROR,
                        "latency-backlog-drops",
                        "%s/%s %s" % (pass_id, row.row_id, arm_name),
                        "gates.backlogDrops=%s: the arm's own deferred queue discarded %s request(s), so its tail is censored"
                        % (fmt_num(backlog, 0), fmt_num(backlog, 0)),
                    )
                if truncated:
                    add(
                        HARNESS_ERROR,
                        "latency-schedule-truncated",
                        "%s/%s %s" % (pass_id, row.row_id, arm_name),
                        "gates.scheduleTruncated=%s: part of the offered schedule was never offered"
                        % fmt_num(truncated, 0),
                    )
                if overflow:
                    add(
                        MEASUREMENT_CAVEAT,
                        "latency-ceiling-reached",
                        "%s/%s %s" % (pass_id, row.row_id, arm_name),
                        "gates.windowOverflow=%s with gates.inFlightCeilingMs=%s: requests past the direct-latency ceiling %s ms were measured through the deferred queue"
                        % (fmt_num(overflow, 0), fmt_num(ceiling, 1), fmt_num(ceiling, 1)),
                    )

    for pass_id in ctx.pass_ids:
        for row in ctx.passes[pass_id]:
            errors = sampler_errors(row)
            if errors:
                processes = sorted({str(record.get("process")) for record in errors})
                add(
                    HARNESS_ERROR,
                    "sampler-error",
                    "%s/%s" % (pass_id, row.row_id),
                    "%d samplerError record(s) for %s: the 1 Hz trace has a sampling gap that is disclosed here, not averaged over"
                    % (len(errors), ", ".join(processes)),
                )
            unreadable = [s for s in row.arms.values() for s in s.samples if s.get("readError")]
            if unreadable:
                read_errors = sum(int(as_number(s.get("readErrors")) or 0) for s in unreadable)
                add(
                    HARNESS_ERROR,
                    "sample-read-error",
                    "%s/%s" % (pass_id, row.row_id),
                    "%d sample(s) carried readError with %d unreadable process counter(s); those samples are rejected from every CPU and memory cell"
                    % (len(unreadable), read_errors),
                )

    for pass_id in ctx.pass_ids:
        for row in ctx.passes[pass_id]:
            declared = plan_arms(row.row_id)
            if declared is None:
                continue
            observed = set(row.arms)
            missing = [name for name in declared if name not in observed]
            extra = [name for name in sorted(observed) if name not in declared]
            if missing:
                add(
                    MEASUREMENT_CAVEAT,
                    "declared-arm-missing",
                    "%s/%s" % (pass_id, row.row_id),
                    "the %s plan declares %s but no file was found for: %s" % (row.profile.plan, ", ".join(declared), ", ".join(missing)),
                )
            if extra:
                add(
                    MEASUREMENT_CAVEAT,
                    "undeclared-arm-present",
                    "%s/%s" % (pass_id, row.row_id),
                    "the %s plan does not declare %s; the data is still reported, and the plan table is what the comparability rule trusts"
                    % (row.profile.plan, ", ".join(extra)),
                )

    for pass_id in ctx.pass_ids:
        for row in ctx.passes[pass_id]:
            alt_port, why = arm_text(row, "DNSALT", "parameters/dnsPort")
            if alt_port is None:
                continue
            if as_number(alt_port) == 53:
                add(
                    HARNESS_ERROR,
                    "dnsalt-on-53",
                    "%s/%s DNSALT" % (pass_id, row.row_id),
                    "the DNSALT arm ran on port 53, which is the port products special-case, so it is not the comparable arm (%s)"
                    % (why or "port 53"),
                )
            dns_port, _ = arm_text(row, "DNS", "parameters/dnsPort")
            if dns_port is not None and alt_port is not None and dns_port == alt_port:
                add(
                    HARNESS_ERROR,
                    "dns-port-collision",
                    "%s/%s" % (pass_id, row.row_id),
                    "the DNS and DNSALT arms both ran on port %s, so the two arms measure the same port" % dns_port,
                )
            if as_number(dns_port) not in (None, 53):
                add(
                    INFORMATIONAL,
                    "dns-arm-port",
                    "%s/%s DNS" % (pass_id, row.row_id),
                    "the port-53 arm ran on port %s, not 53: the products' hardcoded port-53 and local-target special cases are declared for port 53, so the carriage label for this arm is reported as declared but the port is stated"
                    % dns_port,
                )

    for pass_id in ctx.pass_ids:
        rows = ctx.passes[pass_id]
        pre = next((row for row in rows if row.row_id == CONTROL_PRE), None)
        post = next((row for row in rows if row.row_id == CONTROL_POST), None)
        if pre is None or post is None:
            add(
                MEASUREMENT_CAVEAT,
                "control-block-missing",
                pass_id,
                "the pass has %s; both control blocks are needed to bracket the product block"
                % ("only %s" % CONTROL_PRE if pre else "only %s" % CONTROL_POST if post else "neither control block"),
            )
            continue
        ordering = control_ordering(ctx, pass_id)
        if ordering:
            add(CORRECTNESS_FAILURE, "control-bracketing", pass_id, ordering)
    drift = control_drift(ctx)
    for entry in drift["comparisons"]:
        if entry["comparison"].get("error"):
            continue
        scope = "%s vs %s across %d pass(es): %s" % (CONTROL_POST, CONTROL_PRE, entry["passes_used"], entry["metric"])
        if entry["verdict"] == "different":
            add(
                CORRECTNESS_FAILURE,
                "control-drift",
                scope,
                "%s; the post block is the only thing in the campaign that can detect a product that left a driver filtering after it exited"
                % entry["statement"],
            )
        elif entry["verdict"] == "inconclusive":
            add(
                MEASUREMENT_CAVEAT,
                "control-drift-undecided",
                scope,
                "%s: the two control blocks are not shown to agree" % entry["statement"],
            )

    findings.extend(ledger_findings(ctx))

    for row_id in ctx.row_ids:
        profile = ROW_PROFILES.get(row_id)
        if profile is None or profile.udp53 in (UDP53_NA,):
            continue
        if profile.udp53 != UDP53_RELAYED:
            add(
                INFORMATIONAL,
                "udp53-direct",
                row_id,
                "the port-53 DNS arm is a direct-path measurement on this row (%s); the cross-product DNS comparison runs on DNSALT"
                % UDP53_LABEL[profile.udp53],
            )
        if profile.udp == UDP_NOT_CARRIED:
            add(
                INFORMATIONAL,
                "udp-not-carried",
                row_id,
                "every UDP cell for this row reads '%s' and the row is excluded from the UDP-accuracy and DNS-latency comparisons; its TCP results are unaffected"
                % NOT_CARRIED_CELL,
            )
    return findings


def findings_by_severity(findings):
    out = {severity: [] for severity in SEVERITY_ORDER}
    for finding in findings:
        out.setdefault(finding.severity, []).append(finding)
    return out


CONTROL_METRICS = [
    {
        "key": "loss.lossRate",
        "label": "BASE loss lossRate",
        "kind": "diff",
        "unit": "pp",
        "scale": 100.0,
        "extract": lambda row: arm_number(row, "BASE", "metrics/loss/lossRate"),
    },
    {
        "key": "lat.tcp_rtt.p50",
        "label": "BASE latency tcp-rtt p50",
        "kind": "ratio",
        "unit": "us",
        "scale": 1.0,
        "extract": lambda row: arm_latency(row, "BASE", "tcp-rtt", "p50Us"),
    },
    {
        "key": "lat.tcp_rtt.p99",
        "label": "BASE latency tcp-rtt p99",
        "kind": "ratio",
        "unit": "us",
        "scale": 1.0,
        "extract": lambda row: arm_latency(row, "BASE", "tcp-rtt", "p99Us"),
    },
    {
        "key": "lat.udp_rtt.p50",
        "label": "BASE latency udp-rtt p50",
        "kind": "ratio",
        "unit": "us",
        "scale": 1.0,
        "extract": lambda row: arm_latency(row, "BASE", "udp-rtt", "p50Us"),
    },
    {
        "key": "lat.udp_lossRate",
        "label": "BASE latency udp lossRate",
        "kind": "diff",
        "unit": "pp",
        "scale": 100.0,
        "extract": lambda row: arm_number(row, "BASE", "metrics/latency/udp.lossRate"),
    },
]


def control_ordering(ctx, pass_id):
    rows = ctx.passes.get(pass_id, [])
    pre = next((row for row in rows if row.row_id == CONTROL_PRE), None)
    post = next((row for row in rows if row.row_id == CONTROL_POST), None)
    if pre is None or post is None:
        return None
    pre_start = parse_utc(pre.run.get("startedUtc"))
    post_start = parse_utc(post.run.get("startedUtc"))
    if pre_start is None or post_start is None:
        return None
    problems = []
    for row in rows:
        if is_control(row.row_id):
            continue
        start = parse_utc(row.run.get("startedUtc"))
        if start is None:
            problems.append("%s has no startedUtc" % row.row_id)
            continue
        if start < pre_start:
            problems.append("%s started before %s" % (row.row_id, CONTROL_PRE))
        if start > post_start:
            problems.append("%s started after %s" % (row.row_id, CONTROL_POST))
    if problems:
        return "the control blocks do not bracket the product block: " + "; ".join(problems)
    return None


def control_drift(ctx):
    """Compare the two control blocks across passes, per path-quality metric.

    The post block is the only thing in the campaign that can detect a product that left
    a driver filtering after it exited, so a difference between the blocks is reported as
    a finding rather than as noise to average away.
    """
    available = any(
        ctx.control_in(pass_id, CONTROL_PRE) and ctx.control_in(pass_id, CONTROL_POST) for pass_id in ctx.pass_ids
    )
    result = {"available": available, "comparisons": [], "per_pass": []}
    for pass_id in ctx.pass_ids:
        result["per_pass"].append(
            {
                "pass": pass_id,
                "pre_present": ctx.control_in(pass_id, CONTROL_PRE) is not None,
                "post_present": ctx.control_in(pass_id, CONTROL_POST) is not None,
                "ordering": control_ordering(ctx, pass_id),
            }
        )
    if not available:
        return result
    for metric in CONTROL_METRICS:
        family = THRESHOLDS["udp-loss"] if metric["kind"] == "diff" else THRESHOLDS["latency"]
        threshold = family["value"]
        pre_values, post_values, reasons = {}, {}, {}
        for pass_id in ctx.pass_ids:
            pre = ctx.control_in(pass_id, CONTROL_PRE)
            post = ctx.control_in(pass_id, CONTROL_POST)
            if pre is None or post is None:
                reasons[pass_id] = "a control block is missing in this pass"
                continue
            value, why = metric["extract"](pre)
            if value is None:
                reasons[pass_id] = "pre: %s" % why
            else:
                pre_values[pass_id] = value * metric["scale"]
            value, why = metric["extract"](post)
            if value is None:
                reasons[pass_id] = "post: %s" % why
            else:
                post_values[pass_id] = value * metric["scale"]
        seed = ctx.seed + stable_hash("control|%s" % metric["key"]) % 1000000
        comparison = bootstrap_pair(post_values, pre_values, metric["kind"], threshold, ctx.resamples, seed)
        passes_used = len(set(pre_values) & set(post_values))
        verdict, reason = decide(metric["kind"], threshold, comparison, passes_used, ctx.min_passes)
        result["comparisons"].append(
            {
                "metric": metric["label"],
                "key": metric["key"],
                "kind": metric["kind"],
                "unit": metric["unit"],
                "threshold": threshold,
                "pre": {key: pre_values[key] for key in sorted(pre_values, key=natural_key)},
                "post": {key: post_values[key] for key in sorted(post_values, key=natural_key)},
                "unavailable": reasons,
                "comparison": comparison,
                "verdict": verdict,
                "reason": reason,
                "passes_used": passes_used,
                "statement": control_statement(metric, comparison, verdict, reason),
            }
        )
    return result


def control_statement(metric, comparison, verdict, reason):
    if comparison.get("error"):
        return "n/a (%s)" % comparison["error"]
    point = comparison.get("estimate")
    if metric["kind"] == "ratio":
        text = "post/pre = %s" % fmt_num(point, 4) if point is not None else "n/a"
        if comparison.get("ci_low") is not None:
            text += " (95 %% CI %s–%s)" % (fmt_num(comparison["ci_low"], 4), fmt_num(comparison["ci_high"], 4))
    else:
        text = "post-pre = %s pp" % fmt_num(point, 4) if point is not None else "n/a"
        if comparison.get("ci_low") is not None:
            text += " (95 %% CI %s–%s pp)" % (fmt_num(comparison["ci_low"], 4), fmt_num(comparison["ci_high"], 4))
    return "%s -> %s" % (text, verdict if verdict != "inconclusive" else "inconclusive (%s)" % reason)


def ledger_views(ctx):
    """Everything the target ledger contributes, computed once per analysis run."""
    cached = getattr(ctx, "_ledger_views", None)
    if cached is not None:
        return cached
    views = {"available": False, "passes": {}, "attribution": {}, "summary": {}}
    for pass_id in ctx.pass_ids:
        ledgers = ctx.ledgers.get(pass_id) or []
        if not ledgers:
            continue
        views["available"] = True
        records = []
        for ledger in ledgers:
            previous_received = 0.0
            for record in ledger.records:
                if record.get("type") == "udpSummary":
                    received = as_number(record.get("received")) or 0.0
                    record["_received_delta"] = max(0.0, received - previous_received)
                    previous_received = received
                record["_ledger"] = str(ledger.path)
                records.append(record)
        labels = {}
        for record in records:
            label = record.get("label")
            labels[str(label)] = labels.get(str(label), 0) + 1
        runs = []
        for row in ctx.passes.get(pass_id, []):
            runs.append(("row", row, row))
            if row.dual_proxied is not None:
                runs.append(("lane", row.dual_proxied, row))
            if row.dual_direct is not None:
                runs.append(("lane", row.dual_direct, row))
        row_labels = {str(run.label) for _, run, _ in runs if run.label}
        discriminating = len(labels) > 1 and bool(set(labels) & row_labels)
        windows = {}
        for _, run, _ in runs:
            for arm_name in ARM_ORDER:
                window = run.arm_utc_window(arm_name)
                if window is not None:
                    windows.setdefault(run.run_id, []).append((arm_name, window))
        per_arm = []
        for scope, run, owner in runs:
            for arm_name in ARM_ORDER:
                if arm_name not in run.arms:
                    continue
                window = run.arm_utc_window(arm_name)
                if window is None:
                    continue
                selected = [
                    record
                    for record in records
                    if record.get("_utc") is not None and window[0] <= record["_utc"] <= window[1]
                ]
                if discriminating and run.label is not None:
                    selected = [record for record in selected if record.get("label") == run.label]
                overlap = [
                    other
                    for other, spans in windows.items()
                    if other != run.run_id
                    and any(start <= window[1] and window[0] <= end for _, (start, end) in spans)
                ]
                unattributable = bool(overlap) and not discriminating
                udp_records = [record for record in selected if record.get("type") == "udpSummary"]
                tcp_records = [record for record in selected if record.get("type") == "tcp"]
                endpoints = {}
                overflow = 0
                for record in udp_records:
                    overflow += int(as_number(record.get("sourceOverflow")) or 0)
                    for source in record.get("sources") or []:
                        if not isinstance(source, dict):
                            continue
                        key = "%s:%s" % (source.get("address"), source.get("port"))
                        endpoints[key] = endpoints.get(key, 0) + (as_number(source.get("datagrams")) or 0)
                verdicts = {}
                for record in tcp_records:
                    verdict = str(record.get("verdict"))
                    verdicts[verdict] = verdicts.get(verdict, 0) + 1
                per_arm.append(
                    {
                        "pass": pass_id,
                        "scope": scope,
                        "row": run.run_id,
                        "owner": owner.run_id,
                        "arm": arm_name,
                        "window": window,
                        "label": run.label,
                        "records": len(selected),
                        "ledgers": sorted({record["_ledger"] for record in selected}),
                        "overlapping_runs": sorted(overlap),
                        "unattributable": unattributable,
                        "tcp_connections": len(tcp_records),
                        "tcp_verdicts": verdicts,
                        "udp_datagrams": sum(endpoints.values()),
                        "udp_received_delta": sum(
                            (as_number(record.get("_received_delta")) or 0.0) for record in udp_records
                        ),
                        "udp_endpoints": sorted(endpoints),
                        "udp_endpoint_counts": endpoints,
                        "udp_overflow": overflow,
                        "client_connections": None,
                        "client_connections_source": None,
                        "client_datagrams": None,
                        "client_datagrams_source": None,
                    }
                )
        views["passes"][pass_id] = {
            "paths": [str(ledger.path) for ledger in ledgers],
            "records": len(records),
            "bad_lines": sum(ledger.bad_lines for ledger in ledgers),
            "types": {kind: sum(1 for record in records if record.get("type") == kind) for kind in {record.get("type") for record in records}},
            "labels": labels,
            "discriminating_labels": discriminating,
            "per_arm": per_arm,
            "load_errors": [error for ledger in ledgers for error in ledger.load_errors],
        }
        if discriminating:
            views["attribution"][pass_id] = (
                "%d ledger(s); the ledger's own label (%s) selects the run, then the arm's UTC window bounds it"
                % (len(ledgers), ", ".join(sorted(labels)))
            )
        else:
            views["attribution"][pass_id] = (
                "%d ledger(s); the arm's UTC window only, because the ledger carries %s, so a record's own label "
                "cannot select a row" % (len(ledgers), "one label" if len(labels) == 1 else "no label")
            )
    for entry in views["passes"].values():
        for arm in entry["per_arm"]:
            run = ctx_run_by_id(ctx, arm["pass"], arm["row"])
            if run is None:
                continue
            connections, why = client_connection_count(run, arm["arm"])
            datagrams, why_datagrams = client_datagram_count(run, arm["arm"])
            arm["client_connections"] = connections
            arm["client_connections_source"] = why
            arm["client_datagrams"] = datagrams
            arm["client_datagrams_source"] = why_datagrams
            arm["duration_seconds"] = run.arm_window_seconds(arm["arm"])
    ctx._ledger_views = views
    return views


def client_connection_count(row, arm_name):
    result, why = arm_result(row, arm_name)
    if result is None:
        return None, why
    kind = result.get("kind")
    if kind == "latency":
        return arm_number(row, arm_name, "metrics/tcp.connectAttempts")[0], (
            "metrics.tcp.connectAttempts (every connect the arm attempted: its lane connects + the 1 Hz connect probe)"
        )
    if kind == "reliability":
        return arm_number(row, arm_name, "metrics/connectAttempts")[0], "metrics.connectAttempts"
    if kind == "throughput":
        return arm_number(row, arm_name, "parameters/streams")[0], "parameters.streams (one connection per stream)"
    if kind == "persistent":
        return arm_number(row, arm_name, "metrics/connectAttempts")[0], (
            "metrics.connectAttempts (a reconnect opens a second connection)"
        )
    if kind == "mix":
        page, _ = arm_number(row, arm_name, "metrics/classes/page/connections")
        desktops, _ = arm_number(row, arm_name, "parameters/desktops")
        if page is None and desktops is None:
            return None, "no page-connection or desktop counter"
        return (page or 0.0) + (desktops or 0.0), "classes.page.connections + parameters.desktops (bulk streams)"
    if kind == "base":
        return arm_number(row, arm_name, "metrics/latency/tcp.connectAttempts")[0], (
            "metrics.latency.tcp.connectAttempts (the latency phase's lane connects + its 1 Hz connect probe)"
        )
    if kind == "dns":
        return None, "the DNS arm opens its TCP connection on the DNS port, which the ledger reports as dnsSummary"
    return None, "%s opens no connection on the TCP echo port" % arm_name


def client_datagram_count(row, arm_name):
    result, why = arm_result(row, arm_name)
    if result is None:
        return None, why
    kind = result.get("kind")
    if kind == "latency":
        return arm_number(row, arm_name, "metrics/udp.sentOk")[0], "metrics.udp.sentOk"
    if kind == "loss":
        return arm_number(row, arm_name, "metrics/sent")[0], "metrics.sent"
    if kind == "mix":
        return arm_number(row, arm_name, "metrics/udpSent")[0], "metrics.udpSent"
    if kind == "base":
        latency_lane, _ = arm_number(row, arm_name, "metrics/latency/udp.sentOk")
        loss_phase, _ = arm_number(row, arm_name, "metrics/loss/sent")
        if latency_lane is None and loss_phase is None:
            return None, "no UDP phase counter"
        return (latency_lane or 0.0) + (loss_phase or 0.0), (
            "metrics.latency.udp.sentOk + metrics.loss.sent (both phases send datagrams)"
        )
    if kind == "dns":
        return None, "the DNS arm sends to the DNS port, which the ledger reports as dnsSummary"
    return None, "%s sends no datagram to the UDP echo port" % arm_name


def declared_udp_path(row):
    if "/" in row.run_id:
        return "direct" if row.run_id.endswith("/direct") else "proxied"
    profile = ROW_PROFILES.get(row.run_id)
    if profile is None:
        return None
    if profile.udp == UDP_NOT_CARRIED:
        return "direct"
    if profile.udp in (UDP_PROXIED_UTCP, UDP_PROXIED_NATIVE):
        return "proxied"
    return None


def dns_tolerance(entry):
    """One summary interval of the aggregate client rate per shutdown summary, or 1 %."""
    band = max(LEDGER_DATAGRAM_SLACK, LEDGER_DATAGRAM_TOLERANCE * abs(entry["client_udp"]))
    if entry["client_udp"] and entry["duration_seconds"]:
        rate = entry["client_udp"] / entry["duration_seconds"]
        band = max(band, rate * LEDGER_SUMMARY_INTERVAL_SECONDS * max(1, entry.get("summaries", 1)))
    return band


def within_tolerance(observed, expected, relative, slack):
    if observed is None or expected is None:
        return None
    return abs(observed - expected) <= max(slack, relative * abs(expected))


def datagram_tolerance(client_datagrams, duration_seconds):
    """The widest defensible agreement band for a datagram count.

    The target summarises its UDP census once a second and writes one final summary at
    shutdown, so a window's own tail can be reported up to one summary interval late:
    one second of the arm's own rate is the granularity floor, and it is printed beside
    every number it judges.
    """
    band = max(LEDGER_DATAGRAM_SLACK, LEDGER_DATAGRAM_TOLERANCE * abs(client_datagrams or 0.0))
    if client_datagrams and duration_seconds:
        band = max(band, client_datagrams / duration_seconds * LEDGER_SUMMARY_INTERVAL_SECONDS)
    return band


def ledger_findings(ctx):
    out = []
    views = ledger_views(ctx)
    if not views["available"]:
        out.append(
            Finding(
                MEASUREMENT_CAVEAT,
                "no-target-ledger",
                "campaign",
                "no target-ledger.jsonl (or ledger.jsonl) was found beside the pass directories, in the raw "
                "directory or in its parent, so the independent second opinion the campaign is designed around "
                "is unavailable",
            )
        )
        return out
    for pass_id, entry in views["passes"].items():
        if entry["records"] == 0:
            out.append(Finding(MEASUREMENT_CAVEAT, "empty-target-ledger", pass_id, "the ledger holds no records"))
            continue
        if entry["bad_lines"]:
            out.append(
                Finding(HARNESS_ERROR, "ledger-bad-lines", pass_id, "%d ledger line(s) are not JSON" % entry["bad_lines"])
            )
        for arm in entry["per_arm"]:
            scope = "%s/%s %s" % (pass_id, arm["row"], arm["arm"])
            connections = arm["client_connections"]
            if connections is not None and connections > 0:
                if not within_tolerance(
                    arm["tcp_connections"], connections, LEDGER_CONNECTION_TOLERANCE, LEDGER_CONNECTION_SLACK
                ):
                    out.append(
                        Finding(
                            MEASUREMENT_CAVEAT,
                            "ledger-connection-mismatch",
                            scope,
                            "the ledger saw %d connection(s) in the arm's window against the client's own %s (%s)"
                            % (arm["tcp_connections"], fmt_num(connections, 1), arm["client_connections_source"]),
                        )
                    )
            datagrams = arm["client_datagrams"]
            if datagrams is not None and datagrams > 0:
                band = datagram_tolerance(datagrams, arm["duration_seconds"])
                if abs(arm["udp_datagrams"] - datagrams) > band:
                    out.append(
                        Finding(
                            MEASUREMENT_CAVEAT,
                            "ledger-datagram-mismatch",
                            scope,
                            "the ledger counted %d datagram(s) from its source census against the client's own %s "
                            "(%s), outside the ±%s band the one-second summary granularity allows"
                            % (arm["udp_datagrams"], fmt_num(datagrams, 1), arm["client_datagrams_source"], fmt_num(band, 1)),
                        )
                    )
            if arm["unattributable"]:
                out.append(
                    Finding(
                        MEASUREMENT_CAVEAT,
                        "ledger-window-ambiguous",
                        scope,
                        "this window overlaps %s and the ledger carries no per-run label, so its records cannot be "
                        "attributed to this run rather than to the overlapping one; the counts below are the whole "
                        "overlapping window" % ", ".join(arm["overlapping_runs"]),
                    )
                )
            if arm["udp_overflow"]:
                out.append(
                    Finding(
                        MEASUREMENT_CAVEAT,
                        "ledger-source-overflow",
                        scope,
                        "udpSummary.sourceOverflow=%d: the endpoint census table was full, so the endpoint list "
                        "for this window is incomplete" % arm["udp_overflow"],
                    )
                )
        for row_id, slot in endpoint_partition(ctx, pass_id, entry).items():
            overlap = sorted(slot["proxied"] & slot["direct"])
            if not overlap:
                continue
            out.append(
                Finding(
                    CORRECTNESS_FAILURE,
                    "ledger-endpoint-overlap",
                    "%s/%s" % (pass_id, row_id),
                    "datagram(s) arrived from endpoint(s) used on both a proxied and a direct-path window of this "
                    "run: %s; a direct-path arm arriving from a proxied endpoint (or the reverse) means the product "
                    "did not route that traffic where it was configured to" % ", ".join(overlap),
                )
            )
    for port, totals in ledger_dns_totals(ctx, ctx.pass_ids[0] if ctx.pass_ids else "").items():
        if totals["client_udp"] == 0 and totals["client_tcp"] == 0:
            continue
        band = dns_tolerance(totals)
        if abs(totals["ledger_udp"] - totals["client_udp"]) > band:
            out.append(
                Finding(
                    MEASUREMENT_CAVEAT,
                    "ledger-dns-udp-mismatch",
                    "campaign dns port %s" % port,
                    "%d dnsSummary record(s) report %s UDP quer%s against the client's own %s, outside the "
                    "\u00b1%s band the %d shutdown summar%s allow"
                    % (
                        totals["summaries"],
                        fmt_num(totals["ledger_udp"], 0),
                        "y" if totals["ledger_udp"] == 1 else "ies",
                        fmt_num(totals["client_udp"], 0),
                        fmt_num(band, 0),
                        totals["summaries"],
                        "y" if totals["summaries"] == 1 else "ies",
                    ),
                )
            )
        if not within_tolerance(totals["ledger_tcp"], totals["client_tcp"], LEDGER_CONNECTION_TOLERANCE, LEDGER_CONNECTION_SLACK):
            out.append(
                Finding(
                    MEASUREMENT_CAVEAT,
                    "ledger-dns-tcp-mismatch",
                    "campaign dns port %s" % port,
                    "%d dnsSummary record(s) report %s TCP quer%s against the client's own %s"
                    % (totals["summaries"], fmt_num(totals["ledger_tcp"], 0), "y" if totals["ledger_tcp"] == 1 else "ies", fmt_num(totals["client_tcp"], 0)),
                )
            )
        for ledger in ctx.ledgers.get(pass_id) or []:
            for record in ledger.records:
                if record.get("type") != "targetSummary":
                    continue
                errors = as_number(record.get("ledgerWriteErrors")) or 0
                if errors:
                    out.append(
                        Finding(
                            HARNESS_ERROR,
                            "ledger-write-errors",
                            pass_id,
                            "targetSummary.ledgerWriteErrors=%s: the ledger itself lost records" % fmt_num(errors, 0),
                        )
                    )
    return out


def endpoint_partition(ctx, pass_id, entry):
    """{row: {proxied: set, direct: set, proxied_arms: [...], direct_arms: [...]}} for one pass.

    The partition is a property of one row — the product invocation — which owns its own run
    and its dual lanes: the same port number seen on two different rows is not necessarily
    the same endpoint, so only the endpoints within one row's own windows may be compared.
    """
    per_row = {}
    for arm in entry["per_arm"]:
        if arm["arm"] not in UDP_ECHO_ARMS:
            continue
        run = ctx_run_by_id(ctx, arm["pass"], arm["row"])
        if run is None:
            continue
        path = declared_udp_path(run)
        if path is None:
            continue
        slot = per_row.setdefault(arm["owner"], {"proxied": set(), "direct": set(), "proxied_arms": [], "direct_arms": []})
        slot[path].update(arm["udp_endpoints"])
        label = arm["arm"] if arm["row"] == arm["owner"] else "%s:%s" % (arm["row"].split("/")[-1], arm["arm"])
        slot[path + "_arms"].append(label)
    return per_row


def ctx_run_by_id(ctx, pass_id, run_id):
    for row in ctx.passes.get(pass_id, []):
        if row.run_id == run_id:
            return row
        for lane in (row.dual_proxied, row.dual_direct):
            if lane is not None and lane.run_id == run_id:
                return lane
    return None


def ledger_dns_totals(ctx, pass_id):
    """{port: {ledger_udp, ledger_tcp, client_udp, client_tcp}} from the pass's dnsSummary."""
    ledgers = ctx.ledgers.get(pass_id) or []
    if not ledgers:
        return {}
    totals = {}
    for ledger in ledgers:
        for record in ledger.records:
            if record.get("type") != "dnsSummary":
                continue
            entry = totals.setdefault(
                str(record.get("port")),
                {"ledger_udp": 0.0, "ledger_tcp": 0.0, "client_udp": 0.0, "client_tcp": 0.0,
                 "duration_seconds": 0.0, "summaries": 0, "ledger_paths": set()},
            )
            entry["ledger_udp"] += as_number(record.get("udpQueries")) or 0.0
            entry["ledger_tcp"] += as_number(record.get("tcpQueries")) or 0.0
            entry["summaries"] += 1
            entry["ledger_paths"].add(str(ledger.path))
    all_runs = []
    for other_pass in ctx.pass_ids:
        for row in ctx.passes.get(other_pass, []):
            all_runs.append(row)
            all_runs.extend(lane for lane in (row.dual_proxied, row.dual_direct) if lane is not None)
    for run in all_runs:
        if True:
            for arm_name in ("DNS", "DNSALT"):
                result, _ = arm_result(run, arm_name)
                if result is None:
                    continue
                port = dig(result, "parameters" + SEP + "dnsPort")
                if port is None:
                    continue
                entry = totals.setdefault(
                    str(port),
                    {"ledger_udp": 0.0, "ledger_tcp": 0.0, "client_udp": 0.0, "client_tcp": 0.0, "duration_seconds": 0.0},
                )
                entry["client_udp"] += as_number(dig(result, "metrics" + SEP + "udpSent")) or 0.0
                entry["client_tcp"] += as_number(dig(result, "metrics" + SEP + "tcpSent")) or 0.0
                entry["duration_seconds"] += run.arm_window_seconds(arm_name) or 0.0
            result, _ = arm_result(run, "MIX")
            if result is None:
                continue
            run_dns_port = dig(run.run, "target" + SEP + "dnsPort")
            mix_dns = as_number(dig(result, "metrics" + SEP + "classes" + SEP + "dns" + SEP + "sent"))
            if run_dns_port is None or mix_dns is None:
                continue
            entry = totals.setdefault(
                str(run_dns_port),
                {"ledger_udp": 0.0, "ledger_tcp": 0.0, "client_udp": 0.0, "client_tcp": 0.0, "duration_seconds": 0.0},
            )
            entry["client_udp"] += mix_dns
            entry["duration_seconds"] = max(entry["duration_seconds"], run.arm_window_seconds("MIX") or 0.0)
    return totals


UDP_ECHO_ARMS = {"LAT", "LATLOAD", "LOSS", "MIX", "BASE"}


def table_row_profiles(ctx):
    lines = ["## 1. What each row is and what it does with UDP", ""]
    lines.append(
        "Generated from `ROW_PROFILES` in the analysis source, which is the single place the campaign's "
        "design is recorded: the plan each row runs, whether it has a dual phase, what it does with "
        "destination-port-53 UDP and what it does with general UDP. Every `not measured in this row`, "
        "`not carried (UDP bypassed)` and comparability rule elsewhere in this file is derived from this "
        "table, so a row's label can never drift from its treatment."
    )
    lines.append("")
    rows = []
    for row_id in ctx.row_ids:
        profile = ROW_PROFILES.get(row_id)
        observed = sorted({name for row in ctx.rows if row.row_id == row_id for name in row.arms})
        if profile is None:
            rows.append(
                [
                    row_id,
                    "n/a (row id not in ROW_PROFILES)",
                    "n/a (no declared configuration)",
                    "n/a (unknown plan)",
                    ", ".join(observed) or "none",
                    "n/a",
                    "n/a (unknown; the analysis falls back to the arms actually present)",
                    "n/a (unknown; the analysis falls back to the arms actually present)",
                ]
            )
            continue
        declared = plan_arms(row_id) or []
        extra = [name for name in observed if name not in declared]
        rows.append(
            [
                row_id,
                profile.product,
                profile.config,
                profile.plan,
                ", ".join(declared),
                "yes" if profile.dual else "no",
                UDP53_LABEL[profile.udp53],
                UDP_LABEL[profile.udp] + (" — except destination port 53" if row_id == "proxifyre" else ""),
            ]
        )
        if extra:
            rows[-1].append("undeclared arms present: " + ", ".join(extra))
    lines.append(
        md_table(
            [
                "row",
                "product",
                "configuration",
                "plan",
                "arms the plan runs",
                "dual phase",
                "what it does with UDP/53",
                "what it does with general UDP",
            ],
            [row[:8] for row in rows],
        )
    )
    lines.append("")
    extra_notes = [row[8] for row in rows if len(row) > 8]
    if extra_notes:
        lines.append("Plan/observation mismatches: " + "; ".join(extra_notes) + ".")
        lines.append("")
    lines.append(
        "The three WinForward rows are a deliberate design: `wf-aot-nativeudp` differs from `wf-aot-opt` "
        "only in the UDP carriage and `wf-aot-dnsrelay` only in the DNS local target, so a difference "
        "between `wf-aot-opt` and either is attributable to that one feature. Because a partial row never "
        "ran the arms the other rows ran, every comparison involving it is reported as `not measured in "
        "this row` and is excluded from the Holm family rather than treated as a missing value or a zero."
    )
    lines.append("")
    lines.append(
        "`DNSALT` targets a port no product special-cases, so it is the arm whose results are comparable "
        "across products; the port-53 `DNS` arm is not, because its UDP path differs per row (third column "
        "from the right above). `proxifier` cannot proxy UDP at all, so every UDP cell for that row reads "
        "`%s` and the row is excluded from the UDP-accuracy and DNS-latency comparisons; its TCP results are "
        "unaffected." % NOT_CARRIED_CELL
    )
    lines.append("")
    return "\n".join(lines)


def table_findings(ctx):
    lines = ["## 0. Correctness findings", ""]
    by_severity = findings_by_severity(ctx.findings)
    counts = " · ".join("%d %s" % (len(by_severity[severity]), severity) for severity in SEVERITY_ORDER)
    lines.append("**%s.**" % counts)
    lines.append("")
    lines.append(
        "A **correctness failure** means the campaign's answer is wrong rather than slow — traffic that "
        "went where it was not configured to go, or a datagram delivered into the wrong flow. A **harness "
        "error** means the measurement itself is broken: an accounting identity that does not hold, a lane "
        "that never ran, a sampling gap. A **measurement caveat** is disclosed with the number it qualifies. "
        "`informational` notes record the design's own direct-path arms so they cannot be mistaken for "
        "proxied results."
    )
    lines.append("")

    lines.append("### 0.1 `directLeak` — traffic the product was configured to send direct")
    lines.append("")
    leak_rows = []
    for pass_id in ctx.pass_ids:
        for row in ctx.passes[pass_id]:
            profile = ROW_PROFILES.get(row.row_id)
            if row.dual_truth is None:
                if profile is not None and profile.dual:
                    leak_rows.append(
                        [pass_id, row.row_id, "n/a (no dual/proxy-truth.json)", "n/a", "n/a", "n/a", "n/a", "n/a"]
                    )
                continue
            leak = as_number(row.dual_truth.get("directLeak"))
            truth = row.dual_truth
            lanes = "%s / %s" % (
                "present" if row.dual_proxied is not None else "missing",
                "present" if row.dual_direct is not None else "missing",
            )
            if leak is None:
                verdict = "n/a (no numeric directLeak in dual/proxy-truth.json)"
            elif leak > 0:
                verdict = "CORRECTNESS FAILURE: the product intercepted %s direct-path application connection(s)" % fmt_num(leak, 0)
            else:
                verdict = "0 direct-path interceptions observed"
            leak_rows.append(
                [
                    pass_id,
                    row.row_id,
                    fmt_num(leak, 0),
                    fmt_num(as_number(truth.get("tcp")), 0),
                    fmt_num(as_number(truth.get("udp")), 0),
                    fmt_num(as_number(truth.get("utcp")), 0),
                    lanes,
                    verdict,
                ]
            )
    if leak_rows:
        lines.append(
            md_table(
                ["pass", "row", "directLeak", "truth tcp", "truth udp", "truth utcp", "lanes", "verdict"],
                leak_rows,
            )
        )
    else:
        lines.append("n/a (no row in this tree ran a dual phase)")
    lines.append("")
    lines.append(
        "A non-zero `directLeak` is a correctness failure, not a performance result: the product "
        "intercepted an application it was configured to send direct. It is reported here, above every "
        "performance table, for exactly that reason."
    )
    lines.append("")

    lines.append("### 0.2 `foreignConnection` — datagrams delivered into the wrong flow")
    lines.append("")
    foreign_rows = []
    readable = 0
    for pass_id in ctx.pass_ids:
        for row in ctx.passes[pass_id]:
            for arm_name, path, label in (
                ("LOSS", "metrics/foreignConnection", "LOSS.foreignConnection"),
                ("MIX", "metrics/classes/udp/foreignConnection", "MIX.udp.foreignConnection"),
                ("LAT", "metrics/udp.foreignConnection", "LAT.udp.foreignConnection"),
                ("LATLOAD", "metrics/udp.foreignConnection", "LATLOAD.udp.foreignConnection"),
                ("BASE", "metrics/loss/foreignConnection", "BASE.loss.foreignConnection"),
            ):
                value, _ = arm_number(row, arm_name, path)
                if value is None:
                    continue
                readable += 1
                if value > 0:
                    foreign_rows.append(
                        [
                            pass_id,
                            row.row_id,
                            arm_name,
                            label,
                            fmt_num(value, 0),
                            "CORRECTNESS FAILURE: the product mixed datagrams between flows",
                        ]
                    )
            result, _ = arm_result(row, "MIX")
            desktops = dig(result, "metrics" + SEP + "desktops") if result else None
            if isinstance(desktops, list):
                for entry in desktops:
                    if not isinstance(entry, dict):
                        continue
                    value = as_number(entry.get("udpForeignConnection"))
                    if value:
                        foreign_rows.append(
                            [
                                pass_id,
                                row.row_id,
                                "MIX",
                                "desktop %s udpForeignConnection" % entry.get("desktop"),
                                fmt_num(value, 0),
                                "CORRECTNESS FAILURE: the product mixed datagrams between flows",
                            ]
                        )
    if foreign_rows:
        lines.append(md_table(["pass", "row", "arm", "counter", "value", "verdict"], foreign_rows))
    else:
        lines.append(
            "None observed: %d readable `foreignConnection` counter(s) across every (pass, row, arm). A zero "
            "bounds the true rate near 3/%d — it does not prove it is zero — and the counters are re-read "
            "every pass, so a small but non-zero value is reported here on the pass that produced it."
            % (readable, max(readable, 1))
        )
    lines.append("")
    lines.append(
        "A reply carrying another flow's connection id validates against that flow's own filler, so without "
        "this counter it would register as a legitimate arrival and the sequence it displaced would be "
        "reported as loss. A non-zero value is therefore a correctness finding, never a performance one."
    )
    lines.append("")

    lines.append("### 0.3 UDP accounting identities (asserted per record)")
    lines.append("")
    identity_rows = []
    held = 0
    unchecked = 0
    for pass_id in ctx.pass_ids:
        for row in ctx.passes[pass_id]:
            for arm_name, kind, ok, detail in identity_checks(row):
                if ok is None:
                    unchecked += 1
                    status = "not checkable"
                elif ok:
                    held += 1
                    continue
                elif kind == "scheduled-attempts":
                    status = "caveat — the values are kept and every cell that carries them says so"
                else:
                    status = "HARNESS ERROR — the record is excluded from the affected aggregates"
                if kind in ("udp-identity", "dns-partition", "scheduled-attempts"):
                    identity_rows.append([pass_id, row.row_id, arm_name, kind, status, detail or "—"])
    if identity_rows:
        lines.append(md_table(["pass", "row", "arm", "assertion", "result", "detail"], identity_rows))
        lines.append("")
    lines.append(
        "%d record-level identity check(s) **held** and %d could not be checked (their fields are not published "
        "by that record); only the ones that did not hold are listed above." % (held, unchecked)
    )
    lines.append("")
    lines.append(
        "`arrived + late + never + abandonedAtTeardown + corruptDatagrams == sent` is asserted per LOSS, MIX "
        "and BASE record; the DNS records are checked for `answered + servfail + timeout + other == sent`, and "
        "REL for `connectAttempts == scheduledAttempts`. A **violation** is reported as a harness error and "
        "the affected UDP fields are excluded rather than averaged over. A `scheduledAttempts` mismatch means "
        "the arm ended with work in flight: its values are kept but every cell that carries them says so."
    )
    lines.append("")

    lines.append("### 0.4 All findings by severity")
    lines.append("")
    for severity in SEVERITY_ORDER:
        entries = by_severity[severity]
        lines.append("**%s (%d)**" % (severity, len(entries)))
        lines.append("")
        if entries:
            lines.append(
                md_table(
                    ["kind", "scope", "detail"],
                    [[finding.kind, finding.scope, finding.detail] for finding in entries],
                )
            )
        else:
            lines.append("None.")
        lines.append("")
    return "\n".join(lines)


def table_environment(ctx):
    lines = ["## 2. Environment and provenance", ""]
    run_jsons = [row.run for row in ctx.rows if row.run]
    ticks_freq = [row.tick_frequency for row in ctx.rows if row.tick_frequency]
    oses = sorted({str(run.get("osDescription")) for run in run_jsons if run.get("osDescription")})
    clients = sorted({str(run.get("clientVersion")) for run in run_jsons if run.get("clientVersion")})
    frameworks = sorted(
        {str(run.get("frameworkDescription")) for run in run_jsons if run.get("frameworkDescription")}
    )
    processors = sorted({run.get("logicalProcessors") for run in run_jsons if run.get("logicalProcessors")})
    plans = sorted({str(run.get("planHash")) for run in run_jsons if run.get("planHash")})
    targets = sorted(
        {
            "%s tcp/%s udp/%s dns"
            % (dig(run, "target/address"), dig(run, "target/tcpPort"), dig(run, "target/udpPort"))
            for run in run_jsons
            if dig(run, "target/address")
        }
    )
    arms_seen = sorted({name for row in ctx.rows for name in row.arms})

    info = [
        ["row ids measured", ", ".join(ctx.row_ids) + " (%d rows)" % len(ctx.row_ids)],
        ["passes", ", ".join(ctx.pass_ids) + " (%d passes)" % len(ctx.pass_ids)],
        ["arms present", ", ".join(arms_seen) if arms_seen else "n/a"],
        [
            "tick frequency (Hz)",
            fmt_stat(ticks_freq, digits=1) if ticks_freq else "n/a (no run.json with ticks and wallSeconds)",
        ],
        ["logical processors", ", ".join(str(p) for p in processors) if processors else "n/a"],
        ["OS", "; ".join(oses) if oses else "n/a"],
        ["client version", ", ".join(clients) if clients else "n/a"],
        [".NET runtime", "; ".join(frameworks) if frameworks else "n/a"],
        ["plan hash(es)", ", ".join(plans) if plans else "n/a"],
        ["target", "; ".join(targets) if targets else "n/a"],
        ["steady-state warmup", "%.1f s of every arm excluded from the memory and CPU steady-state cells" % ctx.warmup_seconds],
        ["flat mode", "yes (one implicit pass named 'flat')" if ctx.flat else "no"],
    ]
    for pass_id in ctx.pass_ids:
        order = ctx.order.get(pass_id)
        info.append(
            [
                "%s run order (order.txt)" % pass_id,
                " -> ".join(order) if order else "n/a (no order.txt; pass dirs are unordered)",
            ]
        )
        ledgers = ctx.ledgers.get(pass_id) or []
        info.append(
            [
                "%s target ledger(s)" % pass_id,
                "%s (%d record(s), %d unparsable line(s); attribution: %s)"
                % (
                    ", ".join(str(ledger.path) for ledger in ledgers) if ledgers else "n/a (not found)",
                    sum(len(ledger.records) for ledger in ledgers),
                    sum(ledger.bad_lines for ledger in ledgers),
                    ledger_views(ctx)["attribution"].get(pass_id, "n/a"),
                ),
            ]
        )
    if ctx.environment:
        for key in sorted(ctx.environment):
            info.append(["environment.json: " + str(key), str(ctx.environment[key])])
    else:
        info.append(["environment.json", "n/a (not found beside the pass directories)"])
    lines.append(md_table(["item", "value"], info))
    lines.append("")

    lines.append("### Declared loss threshold (`window`, ms) per arm")
    lines.append("")
    window_rows = []
    for arm_name, path, parameter in (
        ("LOSS", "metrics/window", "parameters/lossWindowMs"),
        ("MIX", "metrics/classes/udp/window", "parameters/lossWindowMs"),
        ("BASE", "metrics/loss/window", "parameters/loss/lossWindowMs"),
    ):
        values = []
        declared = []
        for row in ctx.rows:
            value, _ = arm_number(row, arm_name, path)
            if value is not None:
                values.append(value)
            value, _ = arm_number(row, arm_name, parameter)
            if value is not None:
                declared.append(value)
        window_rows.append(
            [
                arm_name,
                path.replace(SEP, "."),
                fmt_stat(values, digits=1, unit=" ms") if values else "n/a (no %s arm with that metric)" % arm_name,
                fmt_stat(declared, digits=1, unit=" ms") if declared else "n/a",
            ]
        )
    gate_values = []
    for row in ctx.rows:
        for arm_name in row.arms:
            value, _ = arm_number(row, arm_name, "gates/windowMs")
            if value:
                gate_values.append(value)
    window_rows.append(
        [
            "any arm (client-side gate)",
            "gates.windowMs",
            fmt_stat(gate_values, digits=1, unit=" ms") if gate_values else "n/a (no non-zero client gate window)",
            "—",
        ]
    )
    lines.append(md_table(["arm", "field", "published value", "declared in the plan"], window_rows))
    lines.append("")
    lines.append(
        "`window` is **the plan's declared `lossWindowMs`** (200 ms when the plan declares none). It is a "
        "declared, published parameter, not derived from an observed latency: an earlier caption in this "
        "analysis claimed `max(200 ms, 5 × observed p99 RTT)` capped at 2000 ms, which was **wrong** and has "
        "been removed — the harness publishes `parameters.lossWindowMs` and repeats it as `metrics.window` "
        "and `gates.windowMs`, so the arrived/late/never split is reproducible from the record alone. A `null` "
        "rate in any table means its denominator was zero (nothing was sent) and is rendered as an empty "
        "cell, never as a zero."
    )
    lines.append("")

    lines.append("### Arm durations (s, from each run's own tick deltas)")
    lines.append("")
    duration_rows = []
    for arm_name in ARM_ORDER:
        values = [row.arm_window_seconds(arm_name) for row in ctx.rows]
        values = [value for value in values if value is not None]
        duration_rows.append(
            [arm_name, fmt_stat(values, digits=1, unit=" s") if values else "n/a (no %s arm)" % arm_name]
        )
    lines.append(md_table(["arm", "median [p25–p75] across rows and passes"], duration_rows))
    lines.append("")

    lines.append("### Product process sampling")
    lines.append("")
    proc_rows = []
    for row in ctx.rows:
        primary, all_names = primary_product_process(row)
        absent = sum(1 for sample in product_samples(row) if sample.get("absent"))
        total = len(product_samples(row))
        rejected = sum(1 for sample in product_samples(row) if not sample_is_readable(sample))
        errors = len(sampler_errors(row))
        proc_rows.append(
            [
                row.pass_id,
                row.run_id,
                ", ".join(row.run.get("samplerProcesses", []) or []) or "n/a (none configured)",
                primary or "n/a (no non-self samples)",
                ", ".join("%s x%d" % (name, count) for name, count in all_names) or "none",
                "%d of %d" % (absent, total) if total else "n/a",
                str(rejected),
                str(errors),
            ]
        )
    lines.append(
        md_table(
            [
                "pass",
                "row",
                "run.json samplerProcesses",
                "analysed process",
                "all non-self processes",
                "absent ticks",
                "rejected (readError)",
                "samplerError records",
            ],
            proc_rows,
        )
    )
    lines.append("")
    lines.append(
        "A sample whose `readError` is true is rejected from every CPU and memory cell, because the harness "
        "writes `null` (not `0`) for a process whose counters could not be read and a zero there is not a "
        "measurement; a `samplerError` record means a whole sampling tick failed and is disclosed rather than "
        "averaged over. Only the process with the most non-self samples is analysed; the campaign samples "
        "exactly one product process per row, so a second name here means the run needs a closer look."
    )
    lines.append("")

    lines.append("### Per-row effective configuration hashes (sha256, first 12 hex)")
    lines.append("")
    config_rows = []
    for row in ctx.rows:
        if row.configs:
            for name, digest, size in row.configs:
                config_rows.append([row.pass_id, row.run_id, name, digest, size])
        else:
            config_rows.append([row.pass_id, row.run_id, "n/a", "n/a (no config* file in the row)", "n/a"])
    lines.append(md_table(["pass", "row", "file", "sha256[:12]", "bytes"], config_rows))
    lines.append("")

    lines.append("### Per-run metadata")
    lines.append("")
    meta_rows = []
    runs = []
    for row in ctx.rows:
        runs.append(row)
        for lane in (row.dual_proxied, row.dual_direct):
            if lane is not None:
                runs.append(lane)
    for run in runs:
        meta_rows.append(
            [
                run.pass_id,
                run.run_id,
                run.run.get("label", "n/a"),
                run.run.get("startedUtc", "n/a"),
                fmt_num(as_number(run.run.get("wallSeconds")), 1),
                fmt_num(run.tick_frequency, 1),
                str(len(run.arms)),
                "yes" if run.run.get("failed") else ("n/a (no run.json)" if not run.run else "no"),
                "; ".join(run.load_errors) if run.load_errors else "—",
            ]
        )
    lines.append(
        md_table(
            ["pass", "run", "label", "startedUtc", "wallSeconds", "tick Hz", "arms", "run failed", "notes"],
            meta_rows,
        )
    )
    lines.append("")
    return "\n".join(lines)


def expected_carriage(row_id):
    """(expected UDP carriage, why) derived from the row's own profile."""
    profile = ROW_PROFILES.get(row_id)
    if profile is None:
        return None, "row id is not in the design table"
    if profile.udp == UDP_NOT_CARRIED:
        return "none", "the product cannot proxy UDP"
    if profile.udp == UDP_PROXIED_UTCP:
        return "utcp", "the row runs UDP-over-TCP v2"
    if profile.udp == UDP_PROXIED_NATIVE:
        return "native", "the row runs the native UDP relay"
    return None, "row does not declare a UDP carriage"


def observed_carriage(native_flows, utcp_flows):
    if utcp_flows and utcp_flows > 0:
        return "utcp"
    if native_flows and native_flows > 0:
        return "native"
    return "none"


def gate_flow_row(ctx, pass_id, row):
    truth = row.proxy_truth
    model = client_flow_model(row)

    presence = "n/a (no product process sampled)"
    candidates = product_samples(row)
    if candidates:
        readable = sum(
            1
            for s in candidates
            if sample_is_readable(s) and as_number(s.get("matched")) and s["matched"] > 0
        )
        presence = "%.0f%% (%d/%d)" % (100.0 * readable / len(candidates), readable, len(candidates))

    truth_tcp = as_number((truth or {}).get("tcp"))
    truth_native = as_number((truth or {}).get("udp"))
    truth_utcp = as_number((truth or {}).get("utcp"))
    if truth is None:
        truth_reason = "no proxy-truth.json"
    elif truth_tcp is None or (truth_native is None and truth_utcp is None):
        truth_reason = "proxy-truth.json has no usable tcp/udp/utcp"
    else:
        truth_reason = None
    truth_native = truth_native or 0.0
    truth_utcp = truth_utcp or 0.0
    truth_udp_total = truth_native + truth_utcp

    tcp_effective = truth_tcp
    tcp_adjust_note = None
    if truth_tcp is not None and truth_utcp > 0 and model.tcp_attempts > 0:
        excess = truth_tcp - model.tcp_attempts
        if excess >= max(2.0, 0.5 * truth_utcp):
            tcp_effective = truth_tcp - truth_utcp
            tcp_adjust_note = (
                "proxy-truth.tcp exceeds the client's own attempts by %.0f with utcp=%.0f, which looks like an "
                "orchestrator that still counts the UoT control CONNECT; the gate uses %.0f - %.0f = %.0f"
                % (excess, truth_utcp, truth_tcp, truth_utcp, tcp_effective)
            )

    if truth_reason is not None:
        tcp_gate = "n/a (%s)" % truth_reason
        tcp_ratio = None
    elif tcp_effective is None or model.tcp_attempts <= 0:
        tcp_gate = "n/a (no client-side TCP connection counters)"
        tcp_ratio = None
    else:
        tcp_ratio = tcp_effective / model.tcp_attempts
        tcp_gate = "%.4f" % tcp_ratio

    if truth_reason is not None:
        udp_gate = "n/a (%s)" % truth_reason
        udp_ratio = None
    elif model.udp_arms <= 0:
        udp_gate = "n/a (no UDP-carrying arm)"
        udp_ratio = None
    elif truth_udp_total <= 0:
        udp_ratio = 0.0
        udp_gate = "0 flows"
    else:
        udp_ratio = truth_udp_total / model.udp_arms
        udp_gate = "%d flow(s) over %d UDP-carrying arm(s)" % (truth_udp_total, model.udp_arms)

    carriage = observed_carriage(truth_native, truth_utcp) if truth is not None else "n/a"
    expected, expected_why = expected_carriage(row.row_id)
    profile = ROW_PROFILES.get(row.row_id)

    notes = []
    if model.missing and not is_control(row.row_id):
        notes.append("denominator gaps: " + "; ".join(sorted(set(model.missing))))
    if not is_control(row.row_id) and (tcp_ratio is None or tcp_ratio < TCP_GATE_MIN):
        notes.append("tcpAttempts terms: " + model.tcp_formula())
    if row.row_id not in UDP_INCAPABLE_ROWS and not is_control(row.row_id) and udp_ratio == 0.0:
        notes.append("udpArms counted: " + model.udp_formula())
    if tcp_adjust_note:
        notes.append(tcp_adjust_note)
    if truth is not None and truth_native > 0 and truth_utcp > 0:
        notes.append("both UDP carriages observed (native=%.0f, utcp=%.0f)" % (truth_native, truth_utcp))
    if row.load_errors:
        notes.append("; ".join(row.load_errors))
    if row.run.get("failed"):
        notes.append("harness marked this run failed")
    bad_lines = sum(arm.bad_lines for arm in row.arms.values())
    if bad_lines:
        notes.append("%d unparsable JSONL line(s)" % bad_lines)

    checks = []
    exempt = "exempt"
    if is_control(row.row_id):
        notes.append("control block: flow gates exempt (nothing is loaded); the BASE arm supplies the path-loss floor")
        checks.append(("tcpGate", exempt, "control block is exempt"))
        checks.append(("udpGate", exempt, "control block is exempt"))
        checks.append(("udpCarriage", exempt, "control block is exempt"))
    else:
        if tcp_ratio is None:
            checks.append(("tcpGate", None, str(tcp_gate)))
        else:
            checks.append(("tcpGate", tcp_ratio >= TCP_GATE_MIN, "%.4f >= %.2f" % (tcp_ratio, TCP_GATE_MIN)))
        if row.row_id in UDP_INCAPABLE_ROWS:
            checks.append(("udpGate", exempt, "this product cannot proxy UDP"))
        elif udp_ratio is None:
            checks.append(("udpGate", None, str(udp_gate)))
        elif udp_ratio <= 0.0:
            checks.append(("udpGate", False, str(udp_gate)))
        else:
            checks.append(("udpGate", True, str(udp_gate)))
        if expected is None or carriage == "n/a":
            checks.append(("udpCarriage", None, "cannot tell (expected %s)" % (expected or "unstated")))
        else:
            checks.append(
                ("udpCarriage", carriage == expected, "observed %s, expected %s (%s)" % (carriage, expected, expected_why))
            )

    if profile is not None and profile.udp53 in (UDP53_LOCAL_TARGET, UDP53_HARDCODED):
        checks.append(("udp53Carriage", "warn", "direct-path measurement: %s" % UDP53_LABEL[profile.udp53]))

    failed = [name for name, ok, _ in checks if ok is False]
    unknown = [name for name, ok, _ in checks if ok is None]
    if failed:
        verdict = "FAIL"
        notes.append("failed: " + ", ".join("%s (%s)" % (name, detail) for name, _, detail in checks if name in failed))
    elif unknown:
        verdict = "n/a"
        notes.append(
            "undecidable: " + ", ".join("%s (%s)" % (name, detail) for name, _, detail in checks if name in unknown)
        )
    else:
        verdict = "PASS"

    return [
        pass_id,
        row.row_id,
        presence,
        "%.0f" % model.tcp_attempts,
        fmt_num(truth_tcp, 0),
        tcp_gate,
        "%.0f" % model.udp_arms,
        fmt_num(truth_native, 0),
        fmt_num(truth_utcp, 0),
        udp_gate,
        carriage,
        expected or "unstated",
        UDP53_LABEL[profile.udp53] if profile else "n/a",
        check_verdict(checks),
        verdict,
        "; ".join(notes) if notes else "—",
    ]


def check_verdict(checks):
    if any(ok is False for _, ok, _ in checks):
        return "FAIL"
    if any(ok is None for _, ok, _ in checks):
        return "n/a"
    return "PASS"


def base_loss_rates(ctx, row):
    """[(source, lossRate, reason)] for the pass's control blocks, pre then post."""
    out = []
    for control_id in CONTROL_ROWS:
        control = ctx.control_in(row.pass_id, control_id)
        if control is None:
            out.append((control_id, None, "no %s row in %s" % (control_id, row.pass_id)))
            continue
        value, why = arm_number(control, "BASE", "metrics/loss/lossRate")
        out.append((control_id, value, why))
    return out


def gate_validity_row(ctx, pass_id, row):
    notes = []
    checks = []
    for arm_name in ("LOSS", "LAT", "LATLOAD", "BASE"):
        if arm_name not in row.arms:
            continue
        value, why = arm_number(row, arm_name, "gates/clientSendLoss")
        if value is None:
            value, why = arm_number(row, arm_name, "metrics/clientSendLoss")
        if value is None:
            checks.append(("%s clientSendLoss" % arm_name, None, why or "missing"))
        else:
            checks.append(("%s clientSendLoss" % arm_name, value == 0, "%s == 0" % fmt_num(value, 0)))
        backlog, _ = arm_number(row, arm_name, "gates/backlogDrops")
        if backlog is not None:
            checks.append(("%s backlogDrops" % arm_name, backlog == 0, "%s == 0" % fmt_num(backlog, 0)))
        truncated, _ = arm_number(row, arm_name, "gates/scheduleTruncated")
        if truncated is not None:
            checks.append(("%s scheduleTruncated" % arm_name, truncated == 0, "%s == 0" % fmt_num(truncated, 0)))
        shortfall, _ = arm_number(row, arm_name, "gates/laneShortfall")
        if shortfall is not None:
            checks.append(("%s laneShortfall" % arm_name, shortfall == 0, "%s == 0" % fmt_num(shortfall, 0)))
        overflow, _ = arm_number(row, arm_name, "gates/windowOverflow")
        ceiling, _ = arm_number(row, arm_name, "gates/inFlightCeilingMs")
        if overflow is not None:
            if overflow > 0:
                checks.append(
                    (
                        "%s latencyCeiling" % arm_name,
                        "warn",
                        "%s deferral(s); direct-latency ceiling %s ms reached, the tail is measured through the deferred queue"
                        % (fmt_num(overflow, 0), fmt_num(ceiling, 1)),
                    )
                )
            else:
                checks.append(
                    ("%s latencyCeiling" % arm_name, "warn", "not reached (inFlightCeilingMs=%s)" % fmt_num(ceiling, 1))
                )
    idle, _ = arm_number(row, "MIX", "gates/idleLanes")
    if idle is not None:
        checks.append(("MIX idleLanes", idle == 0, "%s == 0" % fmt_num(idle, 0)))
    witness_count = 0
    zero_witnesses = []
    for arm_name in ARM_ORDER:
        witnesses = lane_witnesses(row, arm_name)
        if witnesses is None:
            continue
        for label, ok in witnesses:
            witness_count += 1
            if not ok:
                zero_witnesses.append("%s %s" % (arm_name, label))
    if witness_count:
        checks.append(("lane witnesses", not zero_witnesses, "%d of %d witnesses are zero" % (len(zero_witnesses), witness_count)))
        if zero_witnesses:
            notes.append("zero witnesses: " + "; ".join(zero_witnesses))
    identity_failures = []
    identity_unchecked = []
    identity_warnings = []
    for arm_name, kind, ok, detail in identity_checks(row):
        if kind not in ("udp-identity", "dns-partition", "scheduled-attempts"):
            continue
        if ok is False and kind == "scheduled-attempts":
            identity_warnings.append("%s %s (%s)" % (arm_name, kind, detail))
        elif ok is False:
            identity_failures.append("%s %s (%s)" % (arm_name, kind, detail))
        elif ok is None:
            identity_unchecked.append("%s %s (%s)" % (arm_name, kind, detail))
    if identity_warnings:
        checks.append(("scheduledAttempts", "warn", "; ".join(identity_warnings)))
    if identity_failures or identity_unchecked:
        checks.append(
            (
                "accounting identity",
                not identity_failures,
                "; ".join(identity_failures) if identity_failures else "; ".join(identity_unchecked),
            )
        )
    errors = sampler_errors(row)
    rejected = sum(1 for s in row.arms.values() for s in s.samples if not sample_is_readable(s))
    checks.append(("sampling", not errors and not rejected, "%d samplerError, %d rejected sample(s)" % (len(errors), rejected)))

    pre_post = base_loss_rates(ctx, row)
    for source, value, why in pre_post:
        if value is None:
            checks.append(("BASE floor (%s)" % source, None, why or "missing"))
        else:
            checks.append(("BASE floor (%s)" % source, value < 1e-6, "%.3e < 1e-06" % value))
    if any(value is not None and value >= 1e-6 for _, value, _ in pre_post):
        notes.append(
            "the pass's control block is dirty, so every row in this pass inherits a failed harness floor"
        )

    failed = [name for name, ok, _ in checks if ok is False]
    unknown = [name for name, ok, _ in checks if ok is None]
    if failed:
        verdict = "FAIL"
        notes.append("failed: " + ", ".join("%s (%s)" % (name, detail) for name, _, detail in checks if name in failed))
    elif unknown:
        verdict = "n/a"
        notes.append("undecidable: " + ", ".join("%s (%s)" % (name, detail) for name, _, detail in checks if name in unknown))
    else:
        verdict = "PASS"
    return [
        pass_id,
        row.row_id,
        "%.0f" % (arm_number(row, "LOSS", "gates/clientSendLoss")[0] or 0) if "LOSS" in row.arms else "n/a",
        "%.0f" % (arm_number(row, "LAT", "gates/clientSendLoss")[0] or 0) if "LAT" in row.arms else "n/a",
        "%.0f" % (arm_number(row, "LATLOAD", "gates/clientSendLoss")[0] or 0) if "LATLOAD" in row.arms else "n/a",
        fmt_num(arm_number(row, "LAT", "gates/inFlightCeilingMs")[0], 1) if "LAT" in row.arms else "n/a",
        fmt_num(arm_number(row, "MIX", "gates/idleLanes")[0], 0) if "MIX" in row.arms else "n/a",
        "%d zero of %d" % (len(zero_witnesses), witness_count) if witness_count else "n/a",
        "FAIL: " + "; ".join(identity_failures)
        if identity_failures
        else ("holds (%d not checkable)" % len(identity_unchecked) if identity_unchecked else "n/a (no identity published)"),
        "%d / %d" % (len(errors), rejected),
        fmt_num(pre_post[0][1], 3) if pre_post[0][1] is not None else "n/a",
        fmt_num(pre_post[1][1], 3) if len(pre_post) > 1 and pre_post[1][1] is not None else "n/a",
        verdict,
        "; ".join(notes) if notes else "—",
    ]


def table_gates(ctx):
    lines = ["## 3. Gate table (one row per pass and row)", ""]
    lines.append(
        "Per-pass values, not aggregates: every line is one (pass, row) measurement. `tcpGate` and `udpGate` "
        "are the two flow gates; the measurement-validity table below carries the client-loss, lane-witness, "
        "identity and sampling checks. A cell that cannot be computed prints `n/a (<reason>)`."
    )
    lines.append("")
    lines.append("### 3.1 Flow gates and UDP carriage")
    lines.append("")
    headers = [
        "pass",
        "row",
        "presence",
        "tcpAttempts",
        "truth tcp",
        "tcpGate",
        "udpArms",
        "truth udp (native)",
        "truth utcp",
        "udpGate",
        "carriage",
        "expected",
        "UDP/53 carriage",
        "flow verdict",
        "verdict",
        "notes",
    ]
    rows = [gate_flow_row(ctx, pass_id, row) for pass_id in ctx.pass_ids for row in ctx.passes[pass_id]]
    lines.append(md_table(headers, rows))
    lines.append("")
    lines.append(gate_caption())

    lines.append("### 3.2 Measurement validity gates")
    lines.append("")
    lines.append(
        "Every value is one (pass, row) record. A `FAIL` here means the record cannot be compared against "
        "another without saying so; `warn` rows are disclosed and do not fail the row. The latency arms `LAT` "
        "and `LATLOAD` are gated on the same client-loss check the `LOSS` arm always had, and additionally on "
        "their own `backlogDrops` (samples the arm destroyed), `scheduleTruncated` (part of the offered "
        "schedule never offered), `laneShortfall` and the lane witnesses."
    )
    lines.append("")
    validity_headers = [
        "pass",
        "row",
        "LOSS clientSendLoss",
        "LAT clientSendLoss",
        "LATLOAD clientSendLoss",
        "LAT inFlightCeilingMs",
        "MIX idleLanes",
        "lane witnesses",
        "accounting identity",
        "samplerError / rejected",
        "BASE lossRate pre",
        "BASE lossRate post",
        "verdict",
        "notes",
    ]
    validity_rows = [gate_validity_row(ctx, pass_id, row) for pass_id in ctx.pass_ids for row in ctx.passes[pass_id]]
    lines.append(md_table(validity_headers, validity_rows))
    lines.append("")

    lines.append("### 3.3 Gate verdict summary per row")
    lines.append("")
    summary = []
    for row_id in ctx.row_ids:
        flow_verdicts, validity_verdicts = [], []
        for pass_id in ctx.pass_ids:
            row = ctx.row_in(pass_id, row_id)
            if row is None:
                continue
            flow_verdicts.append(gate_flow_row(ctx, pass_id, row)[-2])
            validity_verdicts.append(gate_validity_row(ctx, pass_id, row)[-2])
        summary.append(
            [
                row_id,
                str(len(flow_verdicts)),
                summarise_verdicts(flow_verdicts),
                summarise_verdicts(validity_verdicts),
            ]
        )
    lines.append(md_table(["row", "passes", "flow gates", "measurement validity"], summary))
    lines.append("")
    return "\n".join(lines)


def gate_caption():
    return "\n".join(
        [
            "**Two gates, both per (pass, row).** `proxy-truth.json` counts *flows*, not datagrams: the "
            "orchestrator logs one inbound TCP connection per TCP connection, one inbound packet connection "
            "per native `UDP ASSOCIATE` flow (`udp`) and one UoT control connection per UDP-over-TCP v2 flow "
            "(`utcp`). The LOSS arm pushes thousands of datagrams through a single UDP socket, so it "
            "contributes exactly one UDP flow.",
            "",
            "```",
            "tcpAttempts = LAT.tcp.connectAttempts + LATLOAD.tcp.connectAttempts",
            "            + REL.connectAttempts + PERSIST.connectAttempts",
            "            + THRU.streams (parameters.streams, else 1)",
            "            + DNS.tcpConnections (1 when metrics.tcpSent > 0, else 0)",
            "            + MIX.pageConnections + MIX.parameters.desktops",
            "tcpGate     = proxy-truth.tcp / tcpAttempts                  >= %.2f required" % TCP_GATE_MIN,
            "",
            "udpArms     = UDP sockets the row's plan is expected to relay:",
            "              LAT + LATLOAD + LOSS + MIX, plus DNSALT, plus DNS",
            "              only when the row's profile relays port 53",
            "udpGate     = (proxy-truth.udp + proxy-truth.utcp) > 0 required",
            "```",
            "",
            "**The TCP gate is the exact one.** A ratio slightly under 1 is legitimate: a connection attempt "
            "that fails before the proxy ever sees a SYN (REL's `connectFail` outcomes) never produces a "
            "proxied flow, which is why the bar is %.2f rather than 1. Both the denominator and the numerator "
            "are printed, never just the ratio. `PERSIST` is in the denominator because it opens TCP "
            "connections like any other arm, and `DNS` contributes **one** connection however many queries it "
            "pipelines over it — an earlier version counted `DNS.tcpSent` queries as flows, which inflated the "
            "denominator and could hide a leak; this file counts connections, as the gate requires."
            % TCP_GATE_MIN,
            "",
            "**The UDP gate is a presence check, and the arrival accounting is the real evidence.** It only asks "
            "that at least one UDP flow reached the proxy, because the flow count is not comparable across "
            "products: a product that multiplexes every UDP flow over one association reports one flow for the "
            "whole run while its datagrams still arrive. What proves a working UDP path is the `LOSS` arm's own "
            "`arrived / sent`, reported in section 8. The DNS arms' own UDP sockets are counted only when the "
            "row's profile says that port is relayed: a local-target or hardcoded port-53 datagram never reaches "
            "the proxy, so counting it would demand a flow that cannot exist. `%s` is exempt from the UDP gate "
            "entirely — it cannot proxy UDP — and every UDP cell of that row reads `%s` rather than a number."
            % (", ".join(sorted(UDP_INCAPABLE_ROWS)), NOT_CARRIED_CELL),
            "",
            "The `carriage` column is what the ledger of flows shows (`utcp` when `utcp > 0`, `native` when "
            "`udp > 0`, else `none`) against what the row's own profile declares. A mismatch is a gate failure, "
            "which is what catches a configuration wired to the wrong UDP carriage. The `UDP/53 carriage` "
            "column states what the row does with destination port 53 — relayed, direct through the local DNS "
            "target, or direct through a hardcoded pass-through — because those rows' port-53 DNS arm is a "
            "direct-path measurement and must never be read as a proxied result.",
            "",
            "The `%s` and `%s` rows are exempt from the flow gates: nothing is loaded, both numerators are "
            "legitimately near zero, and their role is to bracket the product block within each pass — which is "
            "also what makes the post block the only instrument in the campaign that can detect a product that "
            "left a driver filtering after it exited."
            % (CONTROL_PRE, CONTROL_POST),
        ]
    )


@dataclass
class FlowModel:
    tcp_attempts: float = 0.0
    tcp_terms: list = field(default_factory=list)
    udp_arms: float = 0.0
    udp_terms: list = field(default_factory=list)
    missing: list = field(default_factory=list)

    def tcp_formula(self):
        if not self.tcp_terms:
            return "n/a"
        return " + ".join("%s=%s" % (label, fmt_num(value, 0)) for label, value in self.tcp_terms)

    def udp_formula(self):
        return ", ".join(self.udp_terms) if self.udp_terms else "n/a"


def client_flow_model(row):
    """The client-side denominators behind the two flow gates.

    ``proxy-truth.json`` counts *flows* — one inbound TCP connection per TCP connection and
    one inbound packet connection per UDP association — so neither denominator may contain
    datagrams or queries. The DNS arm pipelines every query over one connection, so it
    contributes exactly one TCP connection however many queries it sent; counting its
    ``tcpSent`` queries instead (as an earlier version of this analysis did) inflates the
    denominator and can hide a leak.
    """
    model = FlowModel()

    def add_tcp(label, value, arm_present):
        if not arm_present:
            model.missing.append("no %s arm" % label.split(".")[0])
        elif value is None:
            model.missing.append(label)
        else:
            model.tcp_terms.append((label, value))
            model.tcp_attempts += value

    def add_udp_arm(label, carries, arm_present):
        if not arm_present:
            model.missing.append("no %s arm" % label)
        elif carries is None:
            model.missing.append("%s UDP indication" % label)
        elif carries:
            model.udp_terms.append(label)
            model.udp_arms += 1

    for arm_name in ("LAT", "LATLOAD"):
        present = arm_name in row.arms
        attempts, _ = arm_number(row, arm_name, "metrics/tcp.connectAttempts")
        add_tcp("%s.tcp.connectAttempts" % arm_name, attempts, present)
        udp_sent, _ = arm_number(row, arm_name, "metrics/udp.sentOk")
        carries = (udp_sent or 0) > 0 if present else None
        add_udp_arm(arm_name, carries, present)

    rel_present = "REL" in row.arms
    rel_attempts, _ = arm_number(row, "REL", "metrics/connectAttempts")
    add_tcp("REL.connectAttempts", rel_attempts, rel_present)

    persist_present = "PERSIST" in row.arms
    persist_attempts, _ = arm_number(row, "PERSIST", "metrics/connectAttempts")
    add_tcp("PERSIST.connectAttempts", persist_attempts, persist_present)

    thru_present = "THRU" in row.arms
    thru_result, _ = arm_result(row, "THRU")
    streams = as_number(dig(thru_result, "parameters/streams")) if thru_result else None
    if thru_present and streams is None:
        streams = 1.0
    add_tcp("THRU.streams", streams, thru_present)

    dns_present = "DNS" in row.arms
    dns_tcp, _ = arm_number(row, "DNS", "metrics/tcpSent")
    dns_connections = None if dns_tcp is None else (1.0 if dns_tcp > 0 else 0.0)
    add_tcp("DNS.tcpConnections", dns_connections, dns_present)

    mix_present = "MIX" in row.arms
    mix_connection_count, _ = arm_number(row, "MIX", "metrics/classes/page/connections")
    if mix_connection_count is None:
        mix_connection_count, _ = arm_number(row, "MIX", "metrics/pageConnections")
    add_tcp("MIX.pageConnections", mix_connection_count, mix_present)
    mix_result, _ = arm_result(row, "MIX")
    desktops = as_number(dig(mix_result, "parameters/desktops")) if mix_result else None
    add_tcp("MIX.desktops(bulk)", desktops, mix_present)

    loss_present = "LOSS" in row.arms
    loss_sent, _ = arm_number(row, "LOSS", "metrics/sent")
    add_udp_arm("LOSS", (loss_sent > 0) if loss_sent is not None else None, loss_present)

    profile = ROW_PROFILES.get(row.row_id)
    dnsalt_present = "DNSALT" in row.arms
    dnsalt_udp, _ = arm_number(row, "DNSALT", "metrics/udpSent")
    add_udp_arm("DNSALT", (dnsalt_udp > 0) if dnsalt_udp is not None else None, dnsalt_present)
    dns_udp, _ = arm_number(row, "DNS", "metrics/udpSent")
    dns_relayed = profile is not None and profile.udp53 == UDP53_RELAYED
    add_udp_arm(
        "DNS",
        ((dns_udp > 0) and dns_relayed) if dns_udp is not None else None,
        "DNS" in row.arms,
    )

    mix_udp_rate = as_number(dig(mix_result, "parameters/udpPacketsPerSecondPerDesktop")) if mix_result else None
    mix_carries = None
    if mix_present:
        if mix_udp_rate is not None:
            mix_carries = mix_udp_rate > 0
        else:
            mix_udp_sent, _ = arm_number(row, "MIX", "metrics/classes/udp/sent")
            mix_carries = mix_udp_sent is not None and mix_udp_sent > 0
    add_udp_arm("MIX", mix_carries, mix_present)

    if model.tcp_attempts <= 0:
        model.missing.append("no client-side TCP connection counters")
    if model.udp_arms <= 0 and not is_control(row.row_id) and row.row_id not in UDP_INCAPABLE_ROWS:
        model.missing.append("no UDP-carrying arm")
    return model


def table_headline(ctx):
    lines = ["## 4. Headline matrix", ""]
    lines.append(
        "Rows are measured programs. Every cell is `median [p25–p75] across passes (n=K)`. `REL "
        "unexpectedEofRate` is `metrics.unexpectedEof / metrics.connectAttempts` and `REL fidelityRate` is "
        "`metrics.fidelityMismatch / metrics.connectAttempts` (both over connect attempts, never over "
        "completed connections). Rates are percentages; `proxy CPU` is percent of one vCPU over the loaded arms "
        "(IDLE excluded) and `steady-state private bytes` is the per-pass p50 of the product's samples after "
        "the first %.1f s of each arm. `DNS(53)` columns are the port-53 arm, whose UDP path differs per row "
        "(section 9): use `DNSALT` for a cross-product comparison. A cell reading `%s` is traffic the product "
        "does not carry, and an empty cell is a rate whose denominator was zero — not a zero." %
        (ctx.warmup_seconds, NOT_CARRIED_CELL)
    )
    lines.append("")
    headers = ["row"] + ["%s (%s)" % (spec["label"], spec["unit"]) for spec in METRIC_SPECS]
    rows = []
    for row_id in ctx.row_ids:
        cells = [row_id]
        for spec in METRIC_SPECS:
            cells.append(cell_text(per_pass_values(ctx, spec, row_id), spec))
        rows.append(cells)
    lines.append(md_table(headers, rows))
    lines.append("")
    lines.append("### Pass counts behind each headline cell")
    lines.append("")
    count_rows = []
    for row_id in ctx.row_ids:
        cells = [row_id]
        have = []
        for spec in METRIC_SPECS:
            cell = per_pass_values(ctx, spec, row_id)
            value = len(cell.values)
            cells.append(str(value))
            have.append(value)
        cells.append(
            "%d of %d metrics have >= 2 passes" % (sum(1 for value in have if value >= 2), len(METRIC_SPECS))
        )
        count_rows.append(cells)
    lines.append(md_table(["row"] + [spec["key"] for spec in METRIC_SPECS] + ["coverage"], count_rows))
    lines.append("")
    return "\n".join(lines)


def table_latency(ctx):
    lines = ["## 5. Latency detail", ""]
    lines.append(
        "Percentiles are read straight from the harness's histograms (`minUs`, `meanUs`, `p50Us`, `p90Us`, "
        "`p99Us`, `p999Us`, `maxUs`) — never recomputed. Each cell is `median [p25–p75] across passes (n=K)`; "
        "the `count` column is the median histogram count, so it also shows how many observations back each "
        "percentile. A row that did not run an arm prints `not measured in this row`, and a UDP-derived cell "
        "for a row that does not carry UDP prints `%s`." % NOT_CARRIED_CELL
    )
    lines.append("")
    for latency_class in LATENCY_CLASSES:
        lines.append("### `%s`" % latency_class)
        lines.append("")
        rows = []
        for row_id in ctx.row_ids:
            for arm_name in ARM_ORDER:
                if not any(arm_name in row.arms for row in ctx.rows if row.row_id == row_id):
                    continue
                spec = {"digits": 1, "unit": "us", "arm": arm_name, "udp_path": "udp" if latency_class in ("udp-rtt", "dns-rtt") else None}
                status, reason = metric_row_status(ctx, spec, row_id)
                if status in ("not-in-plan", "declared-absent"):
                    rows.append([row_id, arm_name, "n/a (%s)" % reason] + ["n/a"] * (len(PERCENTILES) + 1))
                    continue
                if status == "not-carried":
                    rows.append([row_id, arm_name, NOT_CARRIED_CELL] + [NOT_CARRIED_CELL] * (len(PERCENTILES) + 1))
                    continue
                cell_values = {}
                reasons = []
                for stat in ["count"] + PERCENTILES:
                    cell = MetricValue(row_id=row_id)
                    for pass_id in ctx.pass_ids:
                        row = ctx.row_in(pass_id, row_id)
                        if row is None:
                            continue
                        value, why = arm_latency(row, arm_name, latency_class, stat)
                        if value is None:
                            reasons.append(why)
                        else:
                            cell.values[pass_id] = value
                    cell_values[stat] = cell
                if not cell_values["count"].values and not any(cell_values[stat].values for stat in PERCENTILES):
                    rows.append(
                        [row_id, arm_name, "n/a (%s)" % (reasons[0] if reasons else "no histogram")]
                        + ["n/a"] * (len(PERCENTILES) + 1)
                    )
                    continue
                cells = [
                    row_id,
                    arm_name,
                    fmt_stat(cell_values["count"].sorted_values(), digits=0) if cell_values["count"].values else "n/a",
                ]
                for stat in PERCENTILES:
                    values = cell_values[stat].sorted_values()
                    cells.append(fmt_stat(values, digits=1, unit=" us") if values else "n/a")
                rows.append(cells)
        lines.append(md_table(["row", "arm", "count"] + PERCENTILES, rows) if rows else "n/a (no data)")
        lines.append("")
    return "\n".join(lines)


def arm_denominators(row, arm_name):
    """(transactions, datagrams, transaction label, datagram label) for one arm."""
    kind = row.arms[arm_name].kind if arm_name in row.arms else None
    if kind == "latency":
        tcp, _ = arm_number(row, arm_name, "metrics/tcp.sentOk")
        udp, _ = arm_number(row, arm_name, "metrics/udp.sentOk")
        if row.row_id in UDP_INCAPABLE_ROWS:
            udp = None
        return (tcp or 0.0) + (udp or 0.0), udp, "tcp.sentOk + udp.sentOk", "udp.sentOk"
    if kind == "loss":
        sent, _ = arm_number(row, arm_name, "metrics/sent")
        return sent, sent, "sent (datagrams)", "sent (datagrams)"
    if kind == "reliability":
        attempts, _ = arm_number(row, arm_name, "metrics/connectAttempts")
        return attempts, None, "connectAttempts", None
    if kind == "throughput":
        frames, _ = arm_number(row, arm_name, "metrics/frames")
        return frames, None, "frames", None
    if kind == "dns":
        sent, _ = arm_number(row, arm_name, "metrics/sent")
        udp, _ = arm_number(row, arm_name, "metrics/udpSent")
        if row.row_id in UDP_INCAPABLE_ROWS:
            udp = None
        return sent, udp, "sent (queries)", "udpSent (queries)"
    if kind == "mix":
        page_messages, _ = arm_number(row, arm_name, "metrics/classes/page/messages")
        bulk_frames, _ = arm_number(row, arm_name, "metrics/classes/bulk/frames")
        dns_sent, _ = arm_number(row, arm_name, "metrics/classes/dns/sent")
        udp_sent, _ = arm_number(row, arm_name, "metrics/classes/udp/sent")
        if row.row_id in UDP_INCAPABLE_ROWS:
            udp_sent = None
        transactions = (page_messages or 0.0) + (bulk_frames or 0.0) + (dns_sent or 0.0) + (udp_sent or 0.0)
        return transactions, udp_sent, "page.messages + bulk.frames + dns.sent + udp.sent", "udp.sent"
    if kind == "persistent":
        requests, _ = arm_number(row, arm_name, "metrics/requests")
        return requests, None, "requests", None
    return None, None, None, None


def table_cpu(ctx):
    lines = ["## 6. CPU detail", ""]
    lines.append(
        "Per (row, arm). `proxy %vCPU` and `generator %vCPU` are computed **per process identity** "
        "`(pid, startUtc)`: each identity contributes the delta of its own cumulative `cpuSeconds` between its "
        "first and last readable sample, the deltas are summed, and the sum is divided by the wall-clock span "
        "of the arm's sample stream, so a process that restarted mid-run cannot corrupt the total the way a "
        "first/last difference over a process *name* does. Samples carrying `readError` are rejected (their "
        "counters are `null`, not `0`) and counted in the last column. `tickFrequency` is derived per run from "
        "`(endedTicks - startedTicks) / wallSeconds`; the counters are cumulative `Process.TotalProcessorTime` "
        "(100 % = one fully busy logical processor). `%machine` divides by the logical processor count. "
        "`headroom` is `100 x (P x 100 - generator %vCPU - proxy %vCPU) / (P x 100)` with P logical "
        "processors; machine-wide CPU is not sampled, so headroom is against logical-processor capacity, not a "
        "measured machine total. `CPU ms / 1000 tx` and `CPU ms / 1000 datagrams` normalise the arm's proxy CPU "
        "by the denominators named in the last two columns. All cells are `median [p25–p75] across passes "
        "(n=K)`."
    )
    lines.append("")
    headers = [
        "row",
        "arm",
        "proxy %vCPU",
        "proxy %machine",
        "generator %vCPU",
        "headroom %",
        "CPU ms / 1000 tx",
        "CPU ms / 1000 datagrams",
        "identities used/total",
        "restarts",
        "rejected samples",
        "tx denominator",
        "datagram denominator",
    ]
    rows = []
    for row_id in ctx.row_ids:
        for arm_name in ARM_ORDER:
            if not any(arm_name in row.arms for row in ctx.rows if row.row_id == row_id):
                continue
            proxy_values, machine_values, generator_values, headroom_values = [], [], [], []
            per_tx, per_dgram = [], []
            tx_labels, dgram_labels, reasons = set(), set(), []
            identity_notes, restart_notes, rejected_notes = set(), set(), set()
            for pass_id in ctx.pass_ids:
                row = ctx.row_in(pass_id, row_id)
                if row is None:
                    continue
                primary, _ = primary_product_process(row)
                if primary is None:
                    reasons.append("no product process was sampled")
                    continue
                product = present_product_samples(row, arm_name, primary)
                rejected = sum(
                    1
                    for sample in arm_samples(row, arm_name)
                    if sample.get("process") == primary and not sample_is_readable(sample)
                )
                if rejected:
                    rejected_notes.add(str(rejected))
                proxy, why, diagnostics = cpu_detail(product, row.tick_frequency)
                if diagnostics["identities"]:
                    identity_notes.add("%d/%d" % (diagnostics["identities_used"], diagnostics["identities"]))
                if diagnostics["restarts"]:
                    restart_notes.add(str(diagnostics["restarts"]))
                if proxy is None:
                    reasons.append(why)
                else:
                    proxy_values.append(proxy)
                    processors = as_number(row.run.get("logicalProcessors"))
                    if processors:
                        machine_values.append(proxy / processors)
                generator, why_gen, _ = cpu_detail(
                    arm_samples(row, arm_name, self_only=True), row.tick_frequency, fallback_field="generatorCpuSeconds"
                )
                if generator is None:
                    reasons.append(why_gen)
                else:
                    generator_values.append(generator)
                processors = as_number(row.run.get("logicalProcessors"))
                if proxy is not None and generator is not None and processors:
                    capacity = 100.0 * processors
                    headroom_values.append(100.0 * (capacity - generator - proxy) / capacity)
                transactions, datagrams, tx_label, dgram_label = arm_denominators(row, arm_name)
                cpu_seconds = None
                if product:
                    identity_cpu = sum(
                        (points[-1][1] - points[0][1])
                        for points in _identity_points(product).values()
                        if len(points) >= 2 and points[-1][1] >= points[0][1]
                    )
                    cpu_seconds = identity_cpu if identity_cpu else None
                if cpu_seconds is not None and transactions:
                    per_tx.append(1e6 * cpu_seconds / transactions)
                    tx_labels.add(tx_label)
                if cpu_seconds is not None and datagrams:
                    per_dgram.append(1e6 * cpu_seconds / datagrams)
                    dgram_labels.add(dgram_label)

            if not proxy_values:
                rows.append(
                    [row_id, arm_name, "n/a (%s)" % (sorted(set(reasons))[0] if reasons else "no product samples")]
                    + ["n/a"] * (len(headers) - 3)
                )
                continue
            rows.append(
                [
                    row_id,
                    arm_name,
                    fmt_stat(proxy_values, digits=2, unit=" %"),
                    fmt_stat(machine_values, digits=3, unit=" %") if machine_values else "n/a (no logicalProcessors)",
                    fmt_stat(generator_values, digits=2, unit=" %") if generator_values else "n/a",
                    fmt_stat(headroom_values, digits=2, unit=" %") if headroom_values else "n/a",
                    fmt_stat(per_tx, digits=4, unit=" ms") if per_tx else "n/a (no denominator)",
                    fmt_stat(per_dgram, digits=4, unit=" ms") if per_dgram else "n/a (no datagram denominator)",
                    ", ".join(sorted(identity_notes)) if identity_notes else "n/a",
                    ", ".join(sorted(restart_notes)) if restart_notes else "0",
                    ", ".join(sorted(rejected_notes)) if rejected_notes else "0",
                    ", ".join(sorted(tx_labels)) if tx_labels else "n/a",
                    ", ".join(sorted(dgram_labels)) if dgram_labels else "n/a",
                ]
            )
    lines.append(md_table(headers, rows))
    lines.append("")
    lines.append(
        "**Per-arm transaction and datagram denominators.** `LAT`, `LATLOAD`: "
        "`metrics.tcp.sentOk + metrics.udp.sentOk`; `DNS`: `metrics.sent`; `LOSS`: `metrics.sent`; "
        "`REL`: `metrics.connectAttempts`; `PERSIST`: `metrics.requests`; `THRU`: `metrics.frames`; `MIX`: "
        "`classes.page.messages + classes.bulk.frames + classes.dns.sent + classes.udp.sent`. "
        "Datagram denominators: `udp.sentOk` (LAT/LATLOAD), `sent` (LOSS), `udpSent` (DNS), "
        "`classes.udp.sent` (MIX). A row that does not carry UDP gets no datagram denominator at all — the "
        "column reads `n/a (no datagram denominator)` rather than a fabricated zero — and `IDLE` and `BASE` "
        "have no natural transaction denominator."
    )
    lines.append("")
    return "\n".join(lines)


def _identity_points(samples):
    """{identity: [(ticks, cpuSeconds)]} over readable samples of one arm."""
    per_identity = {}
    for sample in samples:
        ticks = as_number(sample.get("ticks"))
        if ticks is None:
            continue
        for key, cpu, read in sample_identities(sample):
            if read and cpu is not None:
                per_identity.setdefault(key, []).append((ticks, cpu))
    for points in per_identity.values():
        points.sort()
    return per_identity


def summarise_verdicts(verdicts):
    if not verdicts:
        return "n/a"
    distinct = sorted(set(verdicts))
    if len(distinct) == 1:
        return distinct[0]
    return "mixed across passes: " + "; ".join(distinct)


def table_memory(ctx):
    lines = ["## 7. Memory detail", ""]
    lines.append(
        "Steady state = the product's own readable samples after the first %.1f s of each arm (per-arm window "
        "from `run.json`); `absent` ticks are excluded and a sample carrying `readError` is rejected outright, "
        "because its counters are `null` rather than `0`. `private p50/p95` and `working set p50/p95` are the "
        "per-pass p50/p95 of that pass's steady-state samples, then `median [p25–p75] across passes (n=K)`. "
        "`peak working set` is the largest `peakWorkingSetBytes` the sampler reported (a monotone process "
        "peak). The OLS slope regresses private bytes (MiB) on elapsed time within the pass and is reported in "
        "MiB/min with its 95 %% CI; the verdict is *leaks* when that CI excludes zero from above. Two slopes are "
        "given: all measured arms of the row, and the `MIX` arm alone. The CI uses Student's t for df 1–30 and "
        "1.96 beyond." % ctx.warmup_seconds
    )
    lines.append("")
    headers = [
        "row",
        "passes",
        "steady samples/pass",
        "private p50",
        "private p95",
        "working set p50",
        "working set p95",
        "peak working set",
        "slope all arms (MiB/min)",
        "slope verdict",
        "slope MIX (MiB/min)",
        "slope MIX verdict",
    ]
    rows = []
    for row_id in ctx.row_ids:
        per_p50, per_p95, ws_p50, ws_p95, peak, counts = [], [], [], [], [], []
        slopes, slope_verdicts, mix_slopes, mix_verdicts = [], [], [], []
        for pass_id in ctx.pass_ids:
            row = ctx.row_in(pass_id, row_id)
            if row is None:
                continue
            primary, _ = primary_product_process(row)
            if primary is None:
                continue
            private, working, peaks, xs, ys = [], [], [], [], []
            for arm_name in row.arms:
                for sample in steady_samples(row, arm_name, primary, ctx.warmup_seconds):
                    value = process_private_bytes(sample)
                    ws = as_number(sample.get("workingSetBytes"))
                    pk = as_number(sample.get("peakWorkingSetBytes"))
                    tick = as_number(sample.get("ticks"))
                    if value is not None:
                        private.append(value / MIB)
                    if ws is not None:
                        working.append(ws / MIB)
                    if pk is not None:
                        peaks.append(pk / MIB)
                    if value is not None and tick is not None:
                        xs.append(tick)
                        ys.append(value / MIB)
            counts.append(len(private))
            if private:
                per_p50.append(quantile(private, 0.50))
                per_p95.append(quantile(private, 0.95))
            if working:
                ws_p50.append(quantile(working, 0.50))
                ws_p95.append(quantile(working, 0.95))
            if peaks:
                peak.append(max(peaks))
            if len(xs) >= 3 and row.tick_frequency:
                seconds = [(x - min(xs)) / row.tick_frequency for x in xs]
                slope, lo, hi, _, verdict = ols_slope(seconds, ys)
                if slope is not None:
                    slopes.append(slope * 60.0)
                    slope_verdicts.append("%s [%.2f–%.2f]" % (verdict, lo * 60.0, hi * 60.0))
            mix_x, mix_y = [], []
            for sample in steady_samples(row, "MIX", primary, ctx.warmup_seconds):
                value = process_private_bytes(sample)
                tick = as_number(sample.get("ticks"))
                if value is not None and tick is not None:
                    mix_x.append(tick)
                    mix_y.append(value / MIB)
            if len(mix_x) >= 3 and row.tick_frequency:
                seconds = [(x - min(mix_x)) / row.tick_frequency for x in mix_x]
                slope, lo, hi, _, verdict = ols_slope(seconds, mix_y)
                if slope is not None:
                    mix_slopes.append(slope * 60.0)
                    mix_verdicts.append("%s [%.2f–%.2f]" % (verdict, lo * 60.0, hi * 60.0))

        if not per_p50:
            rows.append([row_id, "0", "n/a (no steady-state product samples)"] + ["n/a"] * 9)
            continue
        rows.append(
            [
                row_id,
                str(len(per_p50)),
                fmt_stat(counts, digits=0) if counts else "n/a",
                fmt_stat(per_p50, digits=2, unit=" MiB"),
                fmt_stat(per_p95, digits=2, unit=" MiB"),
                fmt_stat(ws_p50, digits=2, unit=" MiB") if ws_p50 else "n/a",
                fmt_stat(ws_p95, digits=2, unit=" MiB") if ws_p95 else "n/a",
                fmt_stat(peak, digits=2, unit=" MiB") if peak else "n/a",
                fmt_stat(slopes, digits=3, unit=" MiB/min") if slopes else "n/a (fewer than three samples per pass)",
                summarise_verdicts(slope_verdicts),
                fmt_stat(mix_slopes, digits=3, unit=" MiB/min") if mix_slopes else "n/a (no MIX samples)",
                summarise_verdicts(mix_verdicts),
            ]
        )
    lines.append(md_table(headers, rows))
    lines.append("")
    return "\n".join(lines)


UDP_ACCURACY_FIELDS = [
    ("sent", "sent", "count", None),
    ("arrived", "arrived", "count", "sent"),
    ("late", "late", "count", "sent"),
    ("never", "never", "count", "sent"),
    ("corruptDatagrams", "corruptDatagrams", "count", "sent"),
    ("corrupt", "corrupt", "count", "sent"),
    ("duplicate", "duplicate", "count", "sent"),
    ("reordered", "reordered", "count", "sent"),
    ("foreignConnection", "foreignConnection", "count", "sent"),
    ("abandonedAtTeardown", "abandonedAtTeardown", "count", "sent"),
    ("lossRate", "lossRate", "rate", "sent"),
    ("strictLossRate", "strictLossRate", "rate", "sent"),
    ("corruptRate", "corruptRate", "rate", "sent"),
    ("reorderRate", "reorderRate", "rate", "sent"),
    ("clientSendLoss", "clientSendLoss", "count", "supplied"),
]


def table_udp(ctx):
    lines = ["## 8. UDP accuracy detail", ""]
    lines.append(
        "Arms that carry the full UDP classification: `LOSS` (whole arm) and `MIX` "
        "(`metrics.classes.udp`). Every cell is `median [p25–p75] across passes (n=K)`. Rates are percentages "
        "of the arm's `sent`. **A row that does not carry UDP prints `%s` in every UDP cell and is excluded "
        "from every UDP comparison** — reporting its datagrams as a measured result would be the single worst "
        "error this analysis could make. **Rule of three:** a cell is printed as `< 3/n` when *every* pass "
        "reports exactly zero, with n that row's denominator — zero observed events bound the true rate near "
        "3/n, they do not prove it is zero. A cell whose median is zero but which has a non-zero pass keeps "
        "its ordinary `0 [0–x]` rendering so the spread stays visible. An empty cell is a `null` rate: the "
        "harness wrote `null` because nothing was sent, which is not a zero rate." % NOT_CARRIED_CELL
    )
    lines.append("")
    headers = (
        ["row", "arm", "passes", "UDP carriage"]
        + [name for name, _, _, _ in UDP_ACCURACY_FIELDS]
        + ["window ms", "UDP/53 carriage"]
    )
    rows = []
    for row_id in ctx.row_ids:
        profile = ROW_PROFILES.get(row_id)
        for arm_name, prefix in (("LOSS", "metrics"), ("MIX", "metrics/classes/udp")):
            if not any(arm_name in row.arms for row in ctx.rows if row.row_id == row_id):
                spec = {"arm": arm_name, "digits": 0, "unit": "count"}
                status, reason = metric_row_status(ctx, spec, row_id)
                rows.append(
                    [row_id, arm_name, "0", UDP_LABEL.get(profile.udp, "n/a") if profile else "n/a"]
                    + ["n/a (%s)" % (reason or "no %s arm" % arm_name)] * (len(UDP_ACCURACY_FIELDS) + 2)
                )
                continue
            if profile is not None and profile.udp == UDP_NOT_CARRIED:
                rows.append(
                    [row_id, arm_name, "n/a", NOT_CARRIED_CELL]
                    + [NOT_CARRIED_CELL] * (len(UDP_ACCURACY_FIELDS) + 2)
                )
                continue
            cells = {}
            denominators = {}
            pass_count = 0
            for name, field, _, denominator in UDP_ACCURACY_FIELDS:
                cell = MetricValue(row_id=row_id)
                for pass_id in ctx.pass_ids:
                    row = ctx.row_in(pass_id, row_id)
                    if row is None:
                        continue
                    value, why = arm_number(row, arm_name, "%s/%s" % (prefix, field))
                    if value is None:
                        cell.reasons[pass_id] = why
                    else:
                        cell.values[pass_id] = value
                cells[name] = cell
                pass_count = max(pass_count, len(cell.values))
            for denominator in ("sent", "supplied"):
                collected = []
                for pass_id in ctx.pass_ids:
                    row = ctx.row_in(pass_id, row_id)
                    if row is None:
                        continue
                    value, _ = arm_number(row, arm_name, "%s/%s" % (prefix, denominator))
                    if value is not None:
                        collected.append(value)
                denominators[denominator] = median(collected) if collected else None

            rendered = [row_id, arm_name, str(pass_count), UDP_LABEL.get(profile.udp, "n/a") if profile else "n/a"]
            for name, _, kind, denominator in UDP_ACCURACY_FIELDS:
                cell = cells[name]
                bound_n = denominators.get(denominator) if denominator else None
                bound_n = int(round(bound_n)) if bound_n else None
                if not cell.values and not cell.null_passes:
                    rendered.append(
                        "n/a (%s does not publish %s)" % (arm_name, name)
                        if arm_name == "MIX"
                        else "n/a (no %s metric)" % name
                    )
                    continue
                missing = len(cell.reasons) - cell.null_passes
                if kind == "rate":
                    rendered.append(
                        fmt_stat(
                            [value * 100.0 for value in cell.sorted_values()],
                            digits=4,
                            unit=" %",
                            zero_bound_n=bound_n,
                            bound_scale=100.0,
                            null_passes=cell.null_passes,
                            missing_passes=missing,
                        )
                        if cell.values
                        else ""
                    )
                elif name == "sent":
                    rendered.append(
                        fmt_stat(cell.sorted_values(), digits=0, null_passes=cell.null_passes, missing_passes=missing)
                    )
                else:
                    rendered.append(
                        fmt_stat(
                            cell.sorted_values(),
                            digits=0,
                            zero_bound_n=bound_n,
                            null_passes=cell.null_passes,
                            missing_passes=missing,
                        )
                    )
            window_values = []
            for pass_id in ctx.pass_ids:
                row = ctx.row_in(pass_id, row_id)
                if row is None:
                    continue
                value, _ = arm_number(row, arm_name, prefix + "/window")
                if value is not None:
                    window_values.append(value)
            rendered.append(fmt_stat(window_values, digits=1) if window_values else "n/a (no window metric)")
            rendered.append(UDP53_LABEL.get(profile.udp53, "n/a") if profile else "n/a")
            rows.append(rendered)
    lines.append(md_table(headers, rows))
    lines.append("")
    lines.append("### UDP denominators behind the rule-of-three bounds")
    lines.append("")
    denom_rows = []
    for row_id in ctx.row_ids:
        cells = [row_id]
        for arm_name, prefix in (("LOSS", "metrics"), ("MIX", "metrics/classes/udp")):
            for field in ("sent", "supplied"):
                collected = []
                for pass_id in ctx.pass_ids:
                    row = ctx.row_in(pass_id, row_id)
                    if row is None:
                        continue
                    value, _ = arm_number(row, arm_name, "%s/%s" % (prefix, field))
                    if value is not None:
                        collected.append(value)
                cells.append(fmt_stat(collected, digits=0) if collected else "n/a")
        denom_rows.append(cells)
    lines.append(
        md_table(["row", "LOSS sent", "LOSS supplied", "MIX udp sent", "MIX udp supplied"], denom_rows)
    )
    lines.append("")
    lines.append(
        "`MIX` has no `supplied` counter in its UDP class, so `clientSendLoss` for that arm falls back to the "
        "`sent` denominator for its rule-of-three bound."
    )
    lines.append("")
    return "\n".join(lines)


def table_dns(ctx):
    lines = ["## 9. DNS comparability detail", ""]
    lines.append(
        "`DNS` is the port-53 arm and `DNSALT` targets a port no product special-cases. **Only `DNSALT` is "
        "comparable across products**: the port-53 UDP path differs per row — relayed through the proxy on "
        "some, forwarded verbatim to the target by a local DNS target on others, and passed through "
        "unredirected by ProxiFyre's hardcoded port-53 rule — so a port-53 number is a measurement of that "
        "row's own wiring, not of the product's DNS handling. The `UDP/53 carriage` column states which of "
        "those each row is, and the `DNS udp share` column shows how much of the arm rode that path (the TCP "
        "part of the arm is proxied on every row). A row that does not carry UDP has no comparable DNS number "
        "at all."
    )
    lines.append("")
    headers = [
        "row",
        "DNS arm port",
        "DNSALT arm port",
        "UDP/53 carriage",
        "DNS udp share",
        "DNS answerRate",
        "DNS dns-rtt p50",
        "DNSALT udp share",
        "DNSALT answerRate",
        "DNSALT dns-rtt p50",
        "comparability",
    ]
    rows = []
    for row_id in ctx.row_ids:
        profile = ROW_PROFILES.get(row_id)
        spec_dns = {"arm": "DNS", "digits": 1, "unit": "us", "dns53": True, "udp_path": "dns"}
        spec_alt = {"arm": "DNSALT", "digits": 1, "unit": "us", "udp_path": "dns"}
        status_dns, reason_dns = metric_row_status(ctx, spec_dns, row_id)
        status_alt, reason_alt = metric_row_status(ctx, spec_alt, row_id)
        if status_dns == "not-in-plan" and status_alt == "not-in-plan":
            rows.append([row_id, "n/a", "n/a", "n/a", "n/a", "n/a", "n/a", "n/a", "n/a", "n/a", reason_dns])
            continue
        dns_port = median(
            [value for value in (_port(row, "DNS") for row in ctx.rows if row.row_id == row_id) if value is not None]
        )
        alt_port = median(
            [value for value in (_port(row, "DNSALT") for row in ctx.rows if row.row_id == row_id) if value is not None]
        )
        udp_share = _dns_udp_share(ctx, row_id, "DNS")
        alt_share = _dns_udp_share(ctx, row_id, "DNSALT")
        dns_rate = MetricValue(row_id=row_id)
        alt_rate = MetricValue(row_id=row_id)
        for pass_id in ctx.pass_ids:
            row = ctx.row_in(pass_id, row_id)
            if row is None:
                continue
            value, why = metric_rate(row, "DNS", "metrics/answerRate")
            (dns_rate.values if value is not None else dns_rate.reasons)[pass_id] = value if value is not None else why
            value, why = metric_rate(row, "DNSALT", "metrics/answerRate")
            (alt_rate.values if value is not None else alt_rate.reasons)[pass_id] = value if value is not None else why
        not_carried = profile is not None and profile.udp == UDP_NOT_CARRIED
        dns_rate_cell = NOT_CARRIED_CELL if not_carried else cell_text(dns_rate, {"digits": 4, "unit": "pp"})
        alt_rate_cell = NOT_CARRIED_CELL if not_carried else cell_text(alt_rate, {"digits": 4, "unit": "pp"})
        dns_rtt = _dns_rtt_cell(ctx, row_id, "DNS")
        alt_rtt = _dns_rtt_cell(ctx, row_id, "DNSALT")
        if not_carried:
            dns_rtt = NOT_CARRIED_CELL
            alt_rtt = NOT_CARRIED_CELL
        if status_dns == "not-in-plan":
            dns_rate_cell = dns_rtt = "n/a (%s)" % reason_dns
        if status_alt == "not-in-plan":
            alt_rate_cell = alt_rtt = "n/a (%s)" % reason_alt
        if status_dns == "dns-carriage":
            note = "port-53 arm is not cross-product comparable (%s); use DNSALT" % UDP53_LABEL[profile.udp53]
        elif status_alt == "not-in-plan":
            note = "DNSALT not measured in this row"
        elif not_carried:
            note = "excluded from DNS-latency comparisons: UDP bypassed"
        else:
            note = "comparable on DNSALT"
        rows.append(
            [
                row_id,
                fmt_num(dns_port, 0),
                fmt_num(alt_port, 0),
                UDP53_LABEL.get(profile.udp53, "n/a") if profile else "n/a",
                fmt_num(udp_share * 100.0, 1, " %") if udp_share is not None else "n/a",
                dns_rate_cell,
                dns_rtt,
                fmt_num(alt_share * 100.0, 1, " %") if alt_share is not None else "n/a",
                alt_rate_cell,
                alt_rtt,
                note,
            ]
        )
    lines.append(md_table(headers, rows))
    lines.append("")
    return "\n".join(lines)


def _port(row, arm_name):
    value, _ = arm_text(row, arm_name, "parameters/dnsPort")
    return as_number(value)


def _dns_udp_share(ctx, row_id, arm_name):
    shares = []
    for pass_id in ctx.pass_ids:
        row = ctx.row_in(pass_id, row_id)
        if row is None:
            continue
        udp, _ = arm_number(row, arm_name, "metrics/udpSent")
        tcp, _ = arm_number(row, arm_name, "metrics/tcpSent")
        if udp is None or tcp is None or (udp + tcp) <= 0:
            continue
        shares.append(udp / (udp + tcp))
    return median(shares) if shares else None


def _dns_rtt_cell(ctx, row_id, arm_name):
    cell = MetricValue(row_id=row_id)
    for pass_id in ctx.pass_ids:
        row = ctx.row_in(pass_id, row_id)
        if row is None:
            continue
        value, why = arm_latency(row, arm_name, "dns-rtt", "p50Us")
        if value is None:
            cell.reasons[pass_id] = why
        else:
            cell.values[pass_id] = value
    if not cell.values:
        return "n/a (%s)" % cell.reason_summary()
    return fmt_stat(cell.sorted_values(), digits=1, unit=" us", null_passes=cell.null_passes)


def table_persist(ctx):
    lines = ["## 10. Long-lived connection detail (`PERSIST`)", ""]
    lines.append(
        "`PERSIST` holds one TCP connection open across a paced workload with an idle gap in the middle. "
        "`survivedIdle=false` or `reconnects>0` means the product dropped or broke a long-lived connection, "
        "which is a headline result rather than a footnote: a product that cannot keep a connection alive "
        "changes the behaviour of every application that expects one. Cells are `median [p25–p75] across "
        "passes (n=K)`; `survivedIdle` is a per-pass fact, so it is listed per pass."
    )
    lines.append("")
    headers = [
        "row",
        "requests",
        "responses",
        "responseRate",
        "reconnects",
        "survivedIdle per pass",
        "idle s scheduled",
        "idle s observed",
        "tcp-rtt p50",
        "tcp-rtt p99",
        "verdict",
    ]
    rows = []
    for row_id in ctx.row_ids:
        spec = {"arm": "PERSIST", "digits": 1, "unit": "count"}
        status, reason = metric_row_status(ctx, spec, row_id)
        if status in ("not-in-plan", "declared-absent"):
            rows.append([row_id] + ["n/a (%s)" % reason] * (len(headers) - 1))
            continue
        requests = _arm_stat(ctx, row_id, "PERSIST", "metrics/requests")
        responses = _arm_stat(ctx, row_id, "PERSIST", "metrics/responses")
        rate = _arm_stat(ctx, row_id, "PERSIST", "metrics/responseRate", scale=100.0, unit=" %")
        reconnects = _arm_stat(ctx, row_id, "PERSIST", "metrics/reconnects")
        scheduled = _arm_stat(ctx, row_id, "PERSIST", "metrics/idleSecondsScheduled", unit=" s")
        observed = _arm_stat(ctx, row_id, "PERSIST", "metrics/idleSecondsObserved", unit=" s")
        rtt50 = _arm_latency_stat(ctx, row_id, "PERSIST", "tcp-rtt", "p50Us")
        rtt99 = _arm_latency_stat(ctx, row_id, "PERSIST", "tcp-rtt", "p99Us")
        survived = []
        broke = []
        for pass_id in ctx.pass_ids:
            row = ctx.row_in(pass_id, row_id)
            if row is None:
                continue
            value, why = arm_flag(row, "PERSIST", "metrics/survivedIdle")
            if value is None:
                survived.append("%s: n/a (%s)" % (pass_id, why))
            else:
                survived.append("%s: %s" % (pass_id, "yes" if value else "NO"))
                if not value:
                    broke.append(pass_id)
        reconnect_values = [
            value
            for value in (
                arm_number(ctx.row_in(pass_id, row_id), "PERSIST", "metrics/reconnects")[0]
                if ctx.row_in(pass_id, row_id) is not None
                else None
                for pass_id in ctx.pass_ids
            )
            if value
        ]
        if broke or reconnect_values:
            verdict = "BROKE THE IDLE CONNECTION" + ("" if not broke else " in %s" % ", ".join(broke))
            if reconnect_values:
                verdict += "; reconnects>0"
        elif survived:
            verdict = "held the connection across the idle gap"
        else:
            verdict = "n/a (no PERSIST record)"
        rows.append(
            [
                row_id,
                requests,
                responses,
                rate,
                reconnects,
                "; ".join(survived) if survived else "n/a",
                scheduled,
                observed,
                rtt50,
                rtt99,
                verdict,
            ]
        )
    lines.append(md_table(headers, rows))
    lines.append("")
    return "\n".join(lines)


def _arm_stat(ctx, row_id, arm_name, path, scale=1.0, unit="", digits=1):
    values = []
    nulls = 0
    for pass_id in ctx.pass_ids:
        row = ctx.row_in(pass_id, row_id)
        if row is None:
            continue
        value, why = arm_number(row, arm_name, path)
        if value is None:
            if why == NULL_RATE_REASON:
                nulls += 1
            continue
        values.append(value * scale)
    return fmt_stat(values, digits=digits, unit=unit, null_passes=nulls)


def _arm_latency_stat(ctx, row_id, arm_name, latency_class, stat, unit=" us"):
    values = []
    for pass_id in ctx.pass_ids:
        row = ctx.row_in(pass_id, row_id)
        if row is None:
            continue
        value, _ = arm_latency(row, arm_name, latency_class, stat)
        if value is not None:
            values.append(value)
    return fmt_stat(values, digits=1, unit=unit) if values else "n/a"


def table_tcp(ctx):
    lines = ["## 11. TCP reliability detail", ""]
    lines.append(
        "From the `REL` arm. Outcome rates divide by `metrics.connectAttempts` (never by completed connections) "
        "and are printed as percentages. `unexpectedEofRate = metrics.unexpectedEof / connectAttempts` and "
        "`fidelityRate = metrics.fidelityMismatch / connectAttempts`; the same definitions drive the headline "
        "matrix and verdict.json. `unexpectedEof` here excludes the expected early EOFs that `modeSchedule` "
        "deliberately provokes (`expectedEarlyEof`), so it is normally smaller than `outcomes.unexpectedEof`. "
        "`connectAttempts == scheduledAttempts` is an invariant: a mismatch means the arm ended with work in "
        "flight, and the record must not be compared against another without saying so — the last column says "
        "it. Rule of three applies to zero rates. `meanConnectMs` is the mean connect duration over the "
        "attempts that connected and `meanTransferMs` the mean duration of the request send alone, over the "
        "attempts that completed one; each is `null` — printed `n/a` — when it has no sample, never a zero. "
        "Where the outcome marginals leave a question open, the arm's raw record also carries "
        "`metrics.byMode` (the joint mode × observed distribution with each mode's echoed and trailer bytes) "
        "and one `type: \"attempt\"` line per disconfirming attempt, joining a client observation to the "
        "target ledger's verdict through the same `connectionId`. Every cell is `median [p25–p75] across "
        "passes (n=K)`."
    )
    lines.append("")
    headers = (
        ["row", "passes"]
        + ["%s %%" % key for key in OUTCOME_KEYS]
        + [
            "unexpectedEofRate %",
            "fidelityRate %",
            "connectAttempts",
            "scheduledAttempts",
            "in-flight check",
            "meanConnectMs",
            "meanTransferMs",
        ]
    )
    rows = []
    for row_id in ctx.row_ids:
        spec = {"arm": "REL", "digits": 1, "unit": "count"}
        status, reason = metric_row_status(ctx, spec, row_id)
        if status in ("not-in-plan", "declared-absent"):
            rows.append([row_id, "0", "n/a (%s)" % reason] + ["n/a"] * (len(headers) - 3))
            continue
        attempts_all = []
        rate_values = {key: [] for key in OUTCOME_KEYS}
        unexpected_values, fidelity_values = [], []
        mean_connect, mean_transfer = [], []
        scheduled_all = []
        in_flight_notes = []
        passes = 0
        for pass_id in ctx.pass_ids:
            row = ctx.row_in(pass_id, row_id)
            if row is None:
                continue
            attempts, _ = arm_number(row, "REL", "metrics/connectAttempts")
            if attempts is None:
                continue
            passes += 1
            attempts_all.append(attempts)
            scheduled, why = arm_number(row, "REL", "metrics/scheduledAttempts")
            if scheduled is None:
                in_flight_notes.append("%s: n/a (%s)" % (pass_id, why))
            else:
                scheduled_all.append(scheduled)
                if scheduled != attempts:
                    in_flight_notes.append(
                        "%s: connectAttempts %.0f != scheduledAttempts %.0f, work in flight at teardown"
                        % (pass_id, attempts, scheduled)
                    )
            for key in OUTCOME_KEYS:
                value, _ = arm_number(row, "REL", "metrics/outcomes/" + key)
                if value is not None and attempts:
                    rate_values[key].append(100.0 * value / attempts)
            value, _ = arm_ratio(row, "REL", "metrics/unexpectedEof", "metrics/connectAttempts")
            if value is not None:
                unexpected_values.append(value * 100.0)
            value, _ = arm_ratio(row, "REL", "metrics/fidelityMismatch", "metrics/connectAttempts")
            if value is not None:
                fidelity_values.append(value * 100.0)
            value, _ = arm_number(row, "REL", "metrics/meanConnectMs")
            if value is not None:
                mean_connect.append(value)
            value, _ = arm_number(row, "REL", "metrics/meanTransferMs")
            if value is not None:
                mean_transfer.append(value)

        if passes == 0:
            rows.append([row_id, "0", "n/a (no REL result)"] + ["n/a"] * (len(headers) - 3))
            continue
        bound = int(round(median(attempts_all))) if attempts_all else None
        cells = [row_id, str(passes)]
        for key in OUTCOME_KEYS:
            collected = rate_values[key]
            cells.append(
                fmt_stat(collected, digits=4, unit=" %", zero_bound_n=bound, bound_scale=100.0)
                if collected
                else "n/a"
            )
        cells.append(
            fmt_stat(unexpected_values, digits=4, unit=" %", zero_bound_n=bound, bound_scale=100.0)
            if unexpected_values
            else "n/a"
        )
        cells.append(
            fmt_stat(fidelity_values, digits=4, unit=" %", zero_bound_n=bound, bound_scale=100.0)
            if fidelity_values
            else "n/a"
        )
        cells.append(fmt_stat(attempts_all, digits=1) if attempts_all else "n/a")
        cells.append(fmt_stat(scheduled_all, digits=1) if scheduled_all else "n/a (not published)")
        cells.append("; ".join(in_flight_notes) if in_flight_notes else "connectAttempts == scheduledAttempts")
        cells.append(fmt_stat(mean_connect, digits=3, unit=" ms") if mean_connect else "n/a")
        cells.append(fmt_stat(mean_transfer, digits=3, unit=" ms") if mean_transfer else "n/a")
        rows.append(cells)
    lines.append(md_table(headers, rows))
    lines.append("")
    return "\n".join(lines)


def dual_records(ctx):
    out = []
    for pass_id in ctx.pass_ids:
        for row in ctx.passes[pass_id]:
            profile = ROW_PROFILES.get(row.row_id)
            if profile is None or not profile.dual:
                continue
            record = {
                "pass": pass_id,
                "row": row.run_id,
                "proxied": row.dual_proxied,
                "direct": row.dual_direct,
                "truth": row.dual_truth,
                "leak": as_number((row.dual_truth or {}).get("directLeak")),
                "shape_notes": dual_shape_notes(row.dual_proxied, row.dual_direct),
            }
            for lane in ("proxied", "direct"):
                run = record[lane]
                if run is None:
                    continue
                record[lane + "_lat_p50"] = arm_latency(run, "LAT", "tcp-rtt", "p50Us")[0]
                record[lane + "_lat_p99"] = arm_latency(run, "LAT", "tcp-rtt", "p99Us")[0]
                value, _ = arm_number(run, "LOSS", "metrics/lossRate")
                record[lane + "_loss_rate"] = None if value is None else value * 100.0
                record[lane + "_loss_sent"] = arm_number(run, "LOSS", "metrics/sent")[0]
                record[lane + "_tcp_conn"] = arm_latency(run, "LAT", "tcp-connect", "count")[0]
                record[lane + "_udp_foreign"] = arm_number(run, "LOSS", "metrics/foreignConnection")[0]
            out.append(record)
    return out


DUAL_SHAPE_PARAMETERS = (
    "seconds",
    "ratePerSecond",
    "payloadBytes",
    "protocol",
    "lanes",
    "inFlightWindow",
    "lossWindowMs",
    "desktops",
    "connectionsPerSecond",
    "streams",
    "targetBytesPerSecond",
    "intervalMs",
    "idleSeconds",
)


def dual_shape_notes(proxied, direct):
    if proxied is None or direct is None:
        return ["a lane run directory is missing"]
    notes = []
    for arm_name in sorted(set(proxied.arms) & set(direct.arms)):
        left, _ = arm_result(proxied, arm_name)
        right, _ = arm_result(direct, arm_name)
        if left is None or right is None:
            continue
        for key in DUAL_SHAPE_PARAMETERS:
            a = dig(left, "parameters" + SEP + key)
            b = dig(right, "parameters" + SEP + key)
            if a != b:
                notes.append("%s.%s differs (%s vs %s)" % (arm_name, key, a, b))
    only_left = sorted(set(proxied.arms) - set(direct.arms))
    only_right = sorted(set(direct.arms) - set(proxied.arms))
    if only_left:
        notes.append("arms only in the proxied lane: " + ", ".join(only_left))
    if only_right:
        notes.append("arms only in the direct lane: " + ", ".join(only_right))
    return notes


def dual_row_summary(ctx):
    records = dual_records(ctx)
    summary = {}
    for record in records:
        entry = summary.setdefault(
            record["row"],
            {"leak": None, "direct_lat_p50": [], "proxied_lat_p50": [], "direct_loss": [], "proxied_loss": [],
             "shape_notes": set(), "passes": 0},
        )
        entry["passes"] += 1
        if record["leak"] is not None:
            entry["leak"] = max(entry["leak"] or 0.0, record["leak"])
        for key, lane in (("direct_lat_p50", "direct"), ("proxied_lat_p50", "proxied")):
            value = record.get(lane + "_lat_p50")
            if value is not None:
                entry[key].append(value)
        for key, lane in (("direct_loss", "direct"), ("proxied_loss", "proxied")):
            value = record.get(lane + "_loss_rate")
            if value is not None:
                entry[key].append(value)
        for note in record["shape_notes"]:
            entry["shape_notes"].add(note)
    return summary


def dual_findings(ctx):
    out = []
    summary = dual_row_summary(ctx)
    direct_latency = {
        row: median(entry["direct_lat_p50"]) for row, entry in summary.items() if entry["direct_lat_p50"]
    }
    direct_loss = {row: median(entry["direct_loss"]) for row, entry in summary.items() if entry["direct_loss"]}
    if direct_latency:
        best_row = min(direct_latency, key=lambda key: direct_latency[key])
        best = direct_latency[best_row]
        threshold = THRESHOLDS["latency"]["value"]
        for row, value in sorted(direct_latency.items()):
            if best > 0 and value / best - 1.0 > threshold:
                out.append(
                    Finding(
                        PATH_INTERFERENCE,
                        "direct-lane-latency",
                        row,
                        "the direct lane's LAT tcp-rtt p50 is %s us against %s us on %s (%.1f %% worse): the "
                        "direct lane is a property of the path, so the product is interfering with traffic it "
                        "was configured to leave alone"
                        % (fmt_num(value, 1), fmt_num(best, 1), best_row, 100.0 * (value / best - 1.0)),
                    )
                )
    if direct_loss:
        best_row = min(direct_loss, key=lambda key: direct_loss[key])
        best = direct_loss[best_row]
        threshold = THRESHOLDS["udp-loss"]["value"]
        for row, value in sorted(direct_loss.items()):
            if value - best > threshold:
                out.append(
                    Finding(
                        PATH_INTERFERENCE,
                        "direct-lane-loss",
                        row,
                        "the direct lane's LOSS lossRate is %.4f pp against %.4f pp on %s: the direct lane's "
                        "loss is the path's, so a product whose direct lane loses more is interfering"
                        % (value, best, best_row),
                    )
                )
    for row, entry in sorted(summary.items()):
        if entry["shape_notes"]:
            out.append(
                Finding(
                    MEASUREMENT_CAVEAT,
                    "dual-shape-mismatch",
                    row,
                    "the two dual lanes do not run the same declared workload shape: " + "; ".join(sorted(entry["shape_notes"])),
                )
            )
        if entry["leak"] is None:
            out.append(
                Finding(
                    MEASUREMENT_CAVEAT,
                    "direct-leak-unreadable",
                    row,
                    "no numeric directLeak is available for this row's dual phase",
                )
            )
    return out


def table_dual(ctx):
    lines = ["## 12. Dual-phase detail (proxied vs direct lane)", ""]
    lines.append(
        "Each full-plan row that supports it runs a second phase in `dual/`: a `proxied` lane and a `direct` "
        "lane execute the same workload shape against two targets, with only the path differing. The direct "
        "lane's latency and loss are a property of the *path*, not of the product, so a row whose direct lane "
        "is worse than another's is showing interference; `directLeak > 0` is a correctness failure and is "
        "reported in section 0.1. Cells are `median [p25–p75] across passes (n=K)`; a row that did not run the "
        "phase prints `not measured in this row`."
    )
    lines.append("")
    headers = [
        "row",
        "passes",
        "directLeak",
        "proxied LAT tcp-rtt p50",
        "direct LAT tcp-rtt p50",
        "direct / best - 1",
        "proxied LOSS lossRate",
        "direct LOSS lossRate",
        "direct - best (pp)",
        "shape",
        "verdict",
    ]
    summary = dual_row_summary(ctx)
    direct_latency = {row: median(e["direct_lat_p50"]) for row, e in summary.items() if e["direct_lat_p50"]}
    direct_loss = {row: median(e["direct_loss"]) for row, e in summary.items() if e["direct_loss"]}
    best_latency = min(direct_latency.values()) if direct_latency else None
    best_loss = min(direct_loss.values()) if direct_loss else None
    rows = []
    for row_id in ctx.row_ids:
        profile = ROW_PROFILES.get(row_id)
        entry = summary.get(row_id)
        if profile is None or not profile.dual:
            rows.append([row_id, "0", "n/a (no dual phase: the row's plan is %s)" % (profile.plan if profile else "unknown")]
                        + ["n/a"] * (len(headers) - 3))
            continue
        if entry is None:
            rows.append([row_id, "0", "n/a (the plan declares a dual phase but no dual/ directory was found)"]
                        + ["n/a"] * (len(headers) - 3))
            continue
        latency = direct_latency.get(row_id)
        loss = direct_loss.get(row_id)
        excess = (latency / best_latency - 1.0) if latency is not None and best_latency else None
        excess_loss = (loss - best_loss) if loss is not None and best_loss is not None else None
        if entry["leak"]:
            verdict = "CORRECTNESS FAILURE: directLeak=%s" % fmt_num(entry["leak"], 0)
        elif excess is not None and excess > THRESHOLDS["latency"]["value"]:
            verdict = "PATH INTERFERENCE: the direct lane is %.1f %% slower than the best direct lane" % (100.0 * excess)
        elif excess_loss is not None and excess_loss > THRESHOLDS["udp-loss"]["value"]:
            verdict = "PATH INTERFERENCE: the direct lane loses %.4f pp more than the best direct lane" % excess_loss
        else:
            verdict = "no leak observed; direct lane within the pre-declared band of the best row"
        rows.append(
            [
                row_id,
                str(entry["passes"]),
                fmt_num(entry["leak"], 0) if entry["leak"] is not None else "n/a",
                fmt_stat(entry["proxied_lat_p50"], digits=1, unit=" us") if entry["proxied_lat_p50"] else "n/a",
                fmt_stat(entry["direct_lat_p50"], digits=1, unit=" us") if entry["direct_lat_p50"] else "n/a",
                fmt_num(excess * 100.0, 2, " %") if excess is not None else "n/a",
                fmt_stat(entry["proxied_loss"], digits=4, unit=" pp") if entry["proxied_loss"] else "n/a",
                fmt_stat(entry["direct_loss"], digits=4, unit=" pp") if entry["direct_loss"] else "n/a",
                fmt_num(excess_loss, 4) if excess_loss is not None else "n/a",
                "declared parameters match" if not entry["shape_notes"] else "; ".join(sorted(entry["shape_notes"])),
                verdict,
            ]
        )
    lines.append(md_table(headers, rows))
    lines.append("")
    return "\n".join(lines)


def table_control(ctx):
    lines = ["## 13. Control block comparison (`control-pre` vs `control-post`)", ""]
    drift = control_drift(ctx)
    if not drift["available"]:
        lines.append(
            "n/a (both control blocks are needed in at least one pass; the tree holds %s)"
            % (
                ", ".join(
                    "%s: %s, %s"
                    % (
                        entry["pass"],
                        "control-pre present" if entry["pre_present"] else "no control-pre",
                        "control-post present" if entry["post_present"] else "no control-post",
                    )
                    for entry in drift["per_pass"]
                )
                or "no passes"
            )
        )
        lines.append("")
        return "\n".join(lines)
    lines.append(
        "The two control blocks bracket the product block inside each pass and run `BASE` only. Because the "
        "post block runs after every product has exited, it is the only instrument in the campaign that can "
        "detect a product that left a driver filtering after it exited: a difference between the blocks is "
        "reported as a finding. The comparison bootstraps **passes** with the same pre-declared thresholds as "
        "the rest of the analysis (5 % for latency ratios, 0.5 pp for loss differences)."
    )
    lines.append("")
    rows = []
    for entry in drift["comparisons"]:
        pre_median, pre_p25, pre_p75 = median_iqr(list(entry["pre"].values()))
        post_median, post_p25, post_p75 = median_iqr(list(entry["post"].values()))
        unit = "" if entry["unit"] == "pp" else " " + entry["unit"]
        rows.append(
            [
                entry["metric"],
                fmt_stat(list(entry["pre"].values()), digits=4, unit=unit) if entry["pre"] else "n/a",
                fmt_stat(list(entry["post"].values()), digits=4, unit=unit) if entry["post"] else "n/a",
                fmt_num(pre_median, 4, unit) if pre_median is not None else "n/a",
                fmt_num(post_median, 4, unit) if post_median is not None else "n/a",
                entry["statement"],
                entry["verdict"],
            ]
        )
    lines.append(
        md_table(
            ["metric (BASE arm)", "pre", "post", "pre median", "post median", "post vs pre", "verdict"],
            rows,
        )
    )
    lines.append("")
    lines.append("### Per-pass control blocks and bracketing")
    lines.append("")
    order_rows = []
    for entry in drift["per_pass"]:
        order_rows.append(
            [
                entry["pass"],
                "present" if entry["pre_present"] else "MISSING",
                "present" if entry["post_present"] else "MISSING",
                entry["ordering"] or "control-pre precedes and control-post follows every product row",
            ]
        )
    lines.append(md_table(["pass", "control-pre", "control-post", "bracketing"], order_rows))
    lines.append("")
    return "\n".join(lines)


def table_ledger(ctx):
    lines = ["## 14. Target-ledger cross-check", ""]
    views = ledger_views(ctx)
    if not views["available"]:
        lines.append(
            "n/a (no `target-ledger.jsonl` / `ledger.jsonl` found beside the pass directories, in the raw "
            "directory or in its parent; `--ledger PATH` overrides the search). The ledger is the independent "
            "second opinion the campaign is designed around — the client counts what it supplied and the "
            "target counts what arrived — so without it every arrival number above rests on the client alone."
        )
        lines.append("")
        return "\n".join(lines)
    lines.append(
        "The ledger is the target's own account of what arrived, written independently of the client. Every "
        "record carries `utc` and `label`; `udpSummary` carries `sources[{address,port,datagrams}]` and "
        "`sourceOverflow`, `dnsSummary` carries `port`, and every TCP connection is its own `tcp` record. A "
        "campaign can run more than one target instance — the shipped launcher starts a proxied target and a "
        "separate direct-lane target, each with its own ledger — so every ledger found is read, and a record is "
        "attributed to a (run, arm) by the ledger's own label where the campaign supplied one and by the arm's "
        "UTC window otherwise. The window is derived from the client's `startedUtc` plus the arm's tick offsets, "
        "which is the only bridge between the client's stopwatch and the ledger's wall clock. A connection that "
        "opens inside one arm and closes inside the next is attributed where it closed. When two runs' windows "
        "overlap and the ledger carries no per-run label, the counts are reported as unattributable rather than "
        "silently merged, because merging would double-count a lane. The tolerances used to judge a mismatch are "
        "declared in the source (`LEDGER_CONNECTION_*`, `LEDGER_DATAGRAM_*`)."
    )
    lines.append("")
    lines.append("### 14.1 Provenance and attribution")
    lines.append("")
    prov_rows = []
    for pass_id, entry in views["passes"].items():
        types = ", ".join("%s=%d" % (key, value) for key, value in sorted(entry["types"].items(), key=lambda kv: str(kv[0])))
        labels = sorted(entry["labels"].items(), key=lambda kv: str(kv[0]))
        label_text = ", ".join("%s x%d" % (label, count) for label, count in labels[:4]) or "none"
        if len(labels) > 4:
            label_text += " … (%d labels in all)" % len(labels)
        prov_rows.append(
            [
                pass_id,
                ", ".join(entry["paths"]),
                str(entry["records"]),
                types,
                label_text,
                views["attribution"].get(pass_id, "n/a"),
                str(entry["bad_lines"]),
            ]
        )
    lines.append(md_table(["pass", "ledger", "records", "record types", "labels", "attribution used", "unparsable"], prov_rows))
    lines.append("")
    lines.append("### 14.2 Client vs target accounting, per (pass, run, arm)")
    lines.append("")
    check_rows = []
    for pass_id, entry in views["passes"].items():
        for arm in entry["per_arm"]:
            if not (
                arm["tcp_connections"]
                or arm["udp_datagrams"]
                or arm["client_connections"]
                or arm["client_datagrams"]
            ):
                continue
            connections = arm["client_connections"]
            datagrams = arm["client_datagrams"]
            band = datagram_tolerance(datagrams, arm.get("duration_seconds")) if datagrams else None
            check_rows.append(
                [
                    pass_id,
                    arm["row"],
                    arm["arm"],
                    str(arm["tcp_connections"]),
                    fmt_num(connections, 0) if connections is not None else "n/a",
                    "ok" if within_tolerance(arm["tcp_connections"], connections, LEDGER_CONNECTION_TOLERANCE, LEDGER_CONNECTION_SLACK) else ("n/a" if connections is None else "MISMATCH"),
                    str(arm["udp_datagrams"]),
                    fmt_num(datagrams, 0) if datagrams is not None else "n/a",
                    (
                        "n/a"
                        if datagrams is None
                        else ("ok (±%s)" % fmt_num(band, 0) if abs(arm["udp_datagrams"] - datagrams) <= band else "MISMATCH (±%s)" % fmt_num(band, 0))
                    ),
                    str(len(arm["udp_endpoints"])),
                    ", ".join(arm["udp_endpoints"][:3]) + ("…" if len(arm["udp_endpoints"]) > 3 else "") if arm["udp_endpoints"] else "—",
                    str(arm["udp_overflow"]),
                ]
            )
    if check_rows:
        lines.append(
            md_table(
                [
                    "pass",
                    "run",
                    "arm",
                    "ledger TCP connections",
                    "client connections",
                    "TCP check",
                    "ledger datagrams",
                    "client datagrams",
                    "datagram check",
                    "distinct UDP source endpoints",
                    "endpoints",
                    "census overflow",
                ],
                check_rows,
            )
        )
    else:
        lines.append("n/a (no arm in this tree published a client-side connection or datagram count)")
    lines.append("")
    lines.append("### 14.3 UDP source endpoints and the proxied/direct partition")
    lines.append("")
    partition_rows = []
    for pass_id, entry in views["passes"].items():
        for row_id, slot in sorted(endpoint_partition(ctx, pass_id, entry).items()):
            overlap = sorted(slot["proxied"] & slot["direct"])
            if not slot["proxied"] and not slot["direct"]:
                continue
            partition_rows.append(
                [
                    pass_id,
                    row_id,
                    ", ".join(sorted(set(slot["proxied_arms"]))) or "—",
                    str(len(slot["proxied"])) if slot["proxied"] else "0",
                    ", ".join(sorted(set(slot["direct_arms"]))) or "—",
                    str(len(slot["direct"])) if slot["direct"] else "0",
                    ", ".join(overlap) if overlap else "none",
                    "CORRECTNESS FAILURE: an endpoint served both a proxied and a direct-path window"
                    if overlap
                    else "disjoint",
                ]
            )
    if partition_rows:
        lines.append(
            md_table(
                [
                    "pass",
                    "run",
                    "proxied-path arms",
                    "proxied endpoints",
                    "direct-path arms",
                    "direct endpoints",
                    "shared endpoints",
                    "check",
                ],
                partition_rows,
            )
        )
    else:
        lines.append("n/a (no UDP echo window in this tree carried a source census)")
    lines.append("")
    lines.append(
        "A datagram that arrives from an endpoint the row's configured path does not use is evidence that the "
        "product carried traffic it was configured to pass, or passed traffic it was configured to carry. A "
        "row with no window on one side of the partition cannot be checked this way and says so."
    )
    lines.append("")
    lines.append("### 14.4 DNS queries per port (client vs target)")
    lines.append("")
    dns_rows = []
    for port, totals in sorted(ledger_dns_totals(ctx, ctx.pass_ids[0] if ctx.pass_ids else "").items()):
        clientless = totals["client_udp"] == 0 and totals["client_tcp"] == 0
        if True:
            dns_rows.append(
                [
                    "all passes",
                    port,
                    fmt_num(totals["ledger_udp"], 0),
                    fmt_num(totals["client_udp"], 0),
                    "no client counterpart" if clientless else (
                        "ok (±%s)" % fmt_num(dns_tolerance(totals), 0)
                        if abs(totals["ledger_udp"] - totals["client_udp"]) <= dns_tolerance(totals)
                        else "MISMATCH"
                    ),
                    fmt_num(totals["ledger_tcp"], 0),
                    fmt_num(totals["client_tcp"], 0),
                    "no client counterpart" if clientless else (
                        "ok" if within_tolerance(totals["ledger_tcp"], totals["client_tcp"], LEDGER_CONNECTION_TOLERANCE, LEDGER_CONNECTION_SLACK) else "MISMATCH"
                    ),
                ]
            )
    if dns_rows:
        lines.append(
            md_table(
                ["pass", "dns port", "ledger UDP queries", "client UDP queries", "check", "ledger TCP queries", "client TCP queries", "check"],
                dns_rows,
            )
        )
        lines.append("")
        lines.append(
            "The target writes one `dnsSummary` per listener at shutdown, so it covers the ledger's whole "
            "lifetime: for a campaign ledger that is every pass, and the client column is summed over every "
            "pass to match, while a one-pass ledger simply covers that pass. The client column adds up every "
            "DNS and DNSALT arm that targeted that port, dual lanes included, plus the MIX arm's DNS class, "
            "which queries the run's own DNS port. A port the ledger reports that no client arm used says "
            "`no client counterpart`."
        )
    else:
        lines.append("n/a (the ledger holds no dnsSummary records)")
    lines.append("")
    lines.append("### 14.5 TCP verdicts: target vs the client's own expectation")
    lines.append("")
    verdict_rows = []
    for pass_id in ctx.pass_ids:
        ledger_counts = {}
        for ledger in ctx.ledgers.get(pass_id) or []:
            for record in ledger.records:
                if record.get("type") != "tcp":
                    continue
                verdict = str(record.get("verdict"))
                ledger_counts[verdict] = ledger_counts.get(verdict, 0) + 1
        client_counts = {}
        for row in ctx.passes.get(pass_id, []):
            for arm_name in ("REL",):
                result, _ = arm_result(row, arm_name)
                expected = dig(result, "metrics" + SEP + "expected") if result else None
                if not isinstance(expected, dict):
                    continue
                for key, value in expected.items():
                    client_counts[key] = client_counts.get(key, 0) + (as_number(value) or 0)
        verdict_rows.append(
            [
                pass_id,
                fmt_num(ledger_counts.get("clean"), 0),
                fmt_num(client_counts.get("clean"), 0),
                fmt_num(ledger_counts.get("reset"), 0),
                fmt_num(client_counts.get("reset"), 0),
                fmt_num(ledger_counts.get("partialFin"), 0),
                fmt_num(client_counts.get("partialFin"), 0),
                fmt_num(ledger_counts.get("halfClose"), 0),
                fmt_num(client_counts.get("halfClose"), 0),
            ]
        )
    if verdict_rows:
        lines.append(
            md_table(
                [
                    "pass",
                    "target clean",
                    "client expected clean",
                    "target reset",
                    "client expected reset",
                    "target partialFin",
                    "client expected partialFin",
                    "target halfClose",
                    "client expected halfClose",
                ],
                verdict_rows,
            )
        )
        lines.append("")
        lines.append(
            "The target's verdict and the client's own expectation are two independent readings of the same "
            "connection; a large difference is a fidelity question for the report, not a gate."
        )
    else:
        lines.append("n/a (no tcp records in the ledger)")
    lines.append("")
    return "\n".join(lines)


def table_availability(ctx):
    lines = ["## 15. Data availability", ""]
    lines.append(
        "What every row actually contributed, so a reader can see exactly which cells above are thin. `arms "
        "present` lists the arm names found; `arms with a result` counts the files that carried a `result` "
        "record; `declared but absent` is the plan table's expectation that was not met, which is a data gap "
        "rather than a design statement."
    )
    lines.append("")
    rows = []
    for row in ctx.rows:
        declared = plan_arms(row.row_id) or []
        present = sorted(row.arms)
        missing = [name for name in declared if name not in row.arms]
        extra = [name for name in present if name not in declared]
        lanes = []
        if row.dual_proxied is not None:
            lanes.append("proxied")
        if row.dual_direct is not None:
            lanes.append("direct")
        rows.append(
            [
                row.pass_id,
                row.run_id,
                ", ".join(present) or "none",
                "%d/%d" % (sum(1 for arm in row.arms.values() if arm.result is not None), len(row.arms)),
                ", ".join(missing) if missing else "—",
                ", ".join(extra) if extra else "—",
                str(len(self_samples(row))),
                str(len(product_samples(row))),
                str(sum(1 for sample in product_samples(row) if not sample_is_readable(sample))),
                str(len(sampler_errors(row))),
                "yes" if row.proxy_truth is not None else "no",
                ", ".join(lanes) if lanes else ("declared, missing" if ROW_PROFILES.get(row.row_id) and ROW_PROFILES[row.row_id].dual else "n/a"),
                str(len(row.configs)),
                "; ".join(row.load_errors) if row.load_errors else "—",
            ]
        )
    lines.append(
        md_table(
            [
                "pass",
                "row",
                "arms present",
                "arms with a result",
                "declared but absent",
                "present but undeclared",
                "self samples",
                "product samples",
                "rejected samples",
                "samplerError",
                "proxy-truth.json",
                "dual lanes",
                "config files",
                "notes",
            ],
            rows,
        )
    )
    lines.append("")
    return "\n".join(lines)


def build_tables_md(ctx):
    parts = [
        "# End-to-end transparent-proxy campaign — aggregated tables",
        "",
        "Generated by `analysis/analyze.py` from `%s`%s."
        % (ctx.raw, " (flat mode: immediate subdirectories are rows of one implicit pass)" if ctx.flat else ""),
        "",
        "**Aggregation policy.** Every aggregated number is computed over **passes**: one value per pass first, "
        "then the median with its interquartile range `[p25–p75]` and the pass count `(n=K)` beside it. "
        "Percentiles come from the harness's own histograms and are never recomputed. A cell that cannot be "
        "computed is printed as `n/a (<reason>)`; a row that never ran an arm prints `not measured in this "
        "row`; a rate the harness wrote as `null` (its denominator was zero, so nothing was sent) is printed "
        "as an empty cell, never as a zero; and no cell averages over a pass count it does not print.",
        "",
        "**Read section 1 first.** It is generated from the single table in the source that records each row's "
        "plan and what it does with UDP on port 53 and in general, and it explains why some rows have no "
        "comparable UDP or DNS number at all.",
        "",
        table_findings(ctx),
        table_row_profiles(ctx),
        table_environment(ctx),
        table_gates(ctx),
        table_headline(ctx),
        table_latency(ctx),
        table_cpu(ctx),
        table_memory(ctx),
        table_udp(ctx),
        table_dns(ctx),
        table_persist(ctx),
        table_tcp(ctx),
        table_dual(ctx),
        table_control(ctx),
        table_ledger(ctx),
        table_availability(ctx),
    ]
    return "\n".join(parts).rstrip() + "\n"


def bootstrap_pair(pass_values_a, pass_values_b, kind, threshold, resamples, seed):
    """Bootstrap the paired (or unpaired) metric difference between two rows.

    Bootstrap resamples **passes**, never samples. ``kind`` is 'ratio' (log space,
    paired where both rows ran the same passes) or 'diff' (percentage points).
    Returns the point estimate, its 95 % CI, the bootstrap p-value for H0 (no
    difference, used by the 'different' claim) and the bootstrap TOST p-value for
    the equivalence claim behind 'same', plus the resampling mode actually used.
    """
    if not pass_values_a or not pass_values_b:
        return {"error": "one side has no per-pass value for this metric"}
    shared = sorted(set(pass_values_a) & set(pass_values_b))
    paired = len(shared) >= 2 and len(shared) == len(pass_values_a) == len(pass_values_b)
    rng = random.Random(seed)
    estimates = []
    if kind == "ratio":
        if paired:
            pairs = [(pass_values_a[key], pass_values_b[key]) for key in shared]
            if any(a <= 0 or b <= 0 for a, b in pairs):
                return {"error": "non-positive values, ratio undefined"}
            logs = [math.log(a) - math.log(b) for a, b in pairs]
            for _ in range(resamples):
                draw = [logs[rng.randrange(len(logs))] for _ in logs]
                estimates.append(math.exp(median(draw)))
            point = math.exp(median(logs))
            mode = "paired (%d passes)" % len(pairs)
        else:
            a = list(pass_values_a.values())
            b = list(pass_values_b.values())
            if any(value <= 0 for value in a + b):
                return {"error": "non-positive values, ratio undefined"}
            for _ in range(resamples):
                draw_a = [a[rng.randrange(len(a))] for _ in a]
                draw_b = [b[rng.randrange(len(b))] for _ in b]
                estimates.append(median(draw_a) / median(draw_b))
            point = median(a) / median(b)
            mode = "unpaired (%d vs %d passes)" % (len(a), len(b))
    else:
        if paired:
            pairs = [pass_values_a[key] - pass_values_b[key] for key in shared]
            for _ in range(resamples):
                draw = [pairs[rng.randrange(len(pairs))] for _ in pairs]
                estimates.append(median(draw))
            point = median(pairs)
            mode = "paired (%d passes)" % len(pairs)
        else:
            a = list(pass_values_a.values())
            b = list(pass_values_b.values())
            for _ in range(resamples):
                draw_a = [a[rng.randrange(len(a))] for _ in a]
                draw_b = [b[rng.randrange(len(b))] for _ in b]
                estimates.append(median(draw_a) - median(draw_b))
            point = median(a) - median(b)
            mode = "unpaired (%d vs %d passes)" % (len(a), len(b))

    estimates.sort()
    if kind == "ratio":
        below = sum(1 for value in estimates if value <= 1.0) / len(estimates)
        above = sum(1 for value in estimates if value >= 1.0) / len(estimates)
        low_edge, high_edge = 1.0 - (threshold or 0.0), 1.0 + (threshold or 0.0)
    else:
        below = sum(1 for value in estimates if value <= 0.0) / len(estimates)
        above = sum(1 for value in estimates if value >= 0.0) / len(estimates)
        low_edge, high_edge = -(threshold or 0.0), (threshold or 0.0)
    equivalence_p = None
    if threshold is not None:
        below_edge = sum(1 for value in estimates if value <= low_edge) / len(estimates)
        above_edge = sum(1 for value in estimates if value >= high_edge) / len(estimates)
        equivalence_p = max(below_edge, above_edge)
    return {
        "estimate": point,
        "ci_low": quantile(estimates, 0.025),
        "ci_high": quantile(estimates, 0.975),
        "p_value": min(1.0, 2.0 * min(below, above)),
        "p_equivalence": equivalence_p,
        "mode": mode,
        "degenerate": estimates[0] == estimates[-1],
    }


def holm_adjust(pvalues):
    """Holm–Bonferroni step-down adjustment over one metric's pair family."""
    count = len(pvalues)
    order = sorted(range(count), key=lambda index: pvalues[index])
    adjusted = [1.0] * count
    running = 0.0
    for rank, index in enumerate(order):
        running = max(running, min(1.0, (count - rank) * pvalues[index]))
        adjusted[index] = running
    return adjusted


def decide(kind, threshold, comparison, passes_used, min_passes):
    """Practical-significance verdict for one pairwise comparison.

    'different' needs a CI that excludes both zero and the threshold band (for
    ratio metrics the band is 1 +- threshold, for diff metrics +- threshold pp);
    'same' needs a CI wholly inside the band; anything else is 'inconclusive',
    and so is any comparison backed by fewer than ``min_passes`` passes.
    """
    if comparison.get("error"):
        return "inconclusive", comparison["error"]
    if passes_used < min_passes:
        return "inconclusive", "only %d pass(es) contribute; at least %d are needed" % (passes_used, min_passes)
    if threshold is None:
        return "no-threshold-declared", "no pre-declared practical threshold covers this metric"
    lo, hi = comparison["ci_low"], comparison["ci_high"]
    if kind == "ratio":
        band_lo, band_hi = 1.0 - threshold, 1.0 + threshold
        excludes_zero = lo > 1.0 or hi < 1.0
    else:
        band_lo, band_hi = -threshold, threshold
        excludes_zero = lo > 0.0 or hi < 0.0
    if excludes_zero and (lo > band_hi or hi < band_lo):
        return "different", "CI excludes both zero and the ±%.2f threshold band" % threshold
    if lo >= band_lo and hi <= band_hi:
        return "same", "CI lies inside the ±%.2f threshold band" % threshold
    return "inconclusive", "CI straddles the ±%.2f threshold band" % threshold


def pair_comparability(ctx, spec, row_a, row_b):
    """(comparable, reason) for one pair: a design absence or a path difference blocks it."""
    cell_a = per_pass_values(ctx, spec, row_a)
    cell_b = per_pass_values(ctx, spec, row_b)
    for row_id, cell in ((row_a, cell_a), (row_b, cell_b)):
        if cell.status in ("not-in-plan", "declared-absent"):
            return False, "not measured in this row: %s (%s)" % (cell.status_reason, row_id)
        if cell.status == "not-carried":
            return False, "%s: %s" % (row_id, NOT_CARRIED_CELL)
    if spec.get("dns53"):
        profile_a = ROW_PROFILES.get(row_a)
        profile_b = ROW_PROFILES.get(row_b)
        if profile_a is not None and profile_b is not None and profile_a.udp53 != profile_b.udp53:
            return False, (
                "the port-53 UDP carriage differs (%s vs %s), so this arm is not comparable across the two "
                "rows; the DNSALT arm is" % (UDP53_LABEL[profile_a.udp53], UDP53_LABEL[profile_b.udp53])
            )
    if not cell_a.values or not cell_b.values:
        return False, "one side has no per-pass value for this metric"
    return True, None


def build_verdict(ctx):
    metrics_out = {}
    for spec in METRIC_SPECS:
        family = spec["family"]
        threshold_spec = THRESHOLDS[family]
        kind = threshold_spec["kind"]
        threshold = threshold_spec["value"]
        cells = {row_id: per_pass_values(ctx, spec, row_id) for row_id in ctx.row_ids}
        per_row = {}
        for row_id, cell in cells.items():
            med, p25, p75 = median_iqr(cell.sorted_values())
            per_row[row_id] = {
                "unit": spec["unit"],
                "passes": len(cell.values),
                "per_pass": {key: cell.values[key] for key in sorted(cell.values, key=natural_key)},
                "unavailable_passes": {key: cell.reasons[key] for key in sorted(cell.reasons, key=natural_key)},
                "null_passes": cell.null_passes,
                "median": med,
                "iqr": [p25, p75],
                "status": cell.status,
                "status_reason": cell.status_reason,
                "comparable": cell.status in ("ok", "dns-carriage") and bool(cell.values),
            }
        candidates = []
        for index, row_a in enumerate(ctx.row_ids):
            for row_b in ctx.row_ids[index + 1 :]:
                comparable, reason = pair_comparability(ctx, spec, row_a, row_b)
                candidates.append((row_a, row_b, comparable, reason))
        tested = [entry for entry in candidates if entry[2]]
        comparisons = []
        for row_a, row_b, _, _ in tested:
            seed = ctx.seed + stable_hash("%s|%s|%s" % (spec["key"], row_a, row_b)) % 1000000
            comparisons.append(
                bootstrap_pair(
                    dict(per_row[row_a]["per_pass"]),
                    dict(per_row[row_b]["per_pass"]),
                    kind,
                    threshold,
                    ctx.resamples,
                    seed,
                )
            )
        adjusted_difference = holm_adjust([comparison.get("p_value", 1.0) for comparison in comparisons])
        adjusted_equivalence = holm_adjust(
            [
                1.0 if comparison.get("p_equivalence") is None else comparison["p_equivalence"]
                for comparison in comparisons
            ]
        )
        pairs = []
        tested_index = 0
        for row_a, row_b, comparable, reason in candidates:
            if not comparable:
                pairs.append(
                    {
                        "a": row_a,
                        "b": row_b,
                        "comparable": False,
                        "reason": reason,
                        "verdict": "n/a (not comparable)",
                        "in_holm_family": False,
                    }
                )
                continue
            comparison = comparisons[tested_index]
            passes_used = len(set(per_row[row_a]["per_pass"]) & set(per_row[row_b]["per_pass"]))
            raw_verdict, raw_reason = decide(kind, threshold, comparison, passes_used, ctx.min_passes)
            claim_p = adjusted_equivalence[tested_index] if raw_verdict == "same" else adjusted_difference[tested_index]
            holm_verdict, holm_reason = raw_verdict, raw_reason
            if raw_verdict in ("different", "same") and claim_p >= 0.05:
                holm_verdict = "inconclusive"
                holm_reason = "Holm-adjusted p=%.4f >= 0.05 (%s)" % (claim_p, raw_reason)
            entry = {
                "a": row_a,
                "b": row_b,
                "comparable": True,
                "in_holm_family": True,
                "order": "ratio = a/b, difference = a - b (percentage points)",
                "passes_used": passes_used,
                "passes_a": per_row[row_a]["passes"],
                "passes_b": per_row[row_b]["passes"],
                "resampling": comparison.get("mode"),
                "estimate": comparison.get("estimate"),
                "ci95": [comparison.get("ci_low"), comparison.get("ci_high")],
                "p_value": comparison.get("p_value"),
                "holm_p_value": adjusted_difference[tested_index],
                "p_equivalence": comparison.get("p_equivalence"),
                "holm_p_equivalence": adjusted_equivalence[tested_index],
                "raw_verdict": raw_verdict,
                "raw_reason": raw_reason,
                "holm_verdict": holm_verdict,
                "holm_reason": holm_reason,
            }
            if comparison.get("degenerate"):
                entry["note"] = "degenerate bootstrap CI (zero variance across passes)"
            if comparison.get("error"):
                entry["note"] = comparison["error"]
            pairs.append(entry)
            tested_index += 1
        metrics_out[spec["key"]] = {
            "label": spec["label"],
            "unit": spec["unit"],
            "family": family,
            "threshold": {"kind": kind, "value": threshold, "text": threshold_spec["text"]},
            "definition": METRIC_DEFINITIONS.get(spec["key"], "n/a"),
            "arm": spec.get("arm"),
            "udp_path": spec.get("udp_path"),
            "dns53": bool(spec.get("dns53")),
            "rows": per_row,
            "holm_family_size": len(tested),
            "not_comparable_pairs": len(candidates) - len(tested),
            "pairs": pairs,
        }
    drift = control_drift(ctx)
    views = ledger_views(ctx)
    return {
        "generated_by": "benchmarks/results/2026-10-06-e2e-competitors/analysis/analyze.py",
        "raw": str(ctx.raw),
        "flat_mode": ctx.flat,
        "passes": ctx.pass_ids,
        "rows": ctx.row_ids,
        "row_profiles": {
            row_id: {
                "product": profile.product,
                "configuration": profile.config,
                "plan": profile.plan,
                "plan_arms": plan_arms(row_id),
                "dual_phase": profile.dual,
                "udp53_carriage": profile.udp53,
                "udp53_label": UDP53_LABEL[profile.udp53],
                "udp_carriage": profile.udp,
                "udp_label": UDP_LABEL[profile.udp],
                "udp_capable": profile.udp != UDP_NOT_CARRIED,
            }
            for row_id, profile in ROW_PROFILES.items()
        },
        "bootstrap": {
            "resamples": ctx.resamples,
            "seed": ctx.seed,
            "resampling_unit": "passes (never samples)",
            "method": "percentile bootstrap over per-pass values, 95 % CI",
            "min_passes": ctx.min_passes,
        },
        "thresholds": {
            "latency": "5 %",
            "cpu": "10 %",
            "memory": "10 %",
            "udp-loss": "0.5 percentage points",
            "tcp-unexpected": "0.1 percentage points",
            "note": "A pair is 'different' only when the bootstrap CI excludes both zero and the threshold "
            "band, and 'same' only when the CI lies wholly inside the band; anything else is 'inconclusive'. "
            "Holm–Bonferroni is applied across each metric's family of *comparable* pairwise comparisons: a "
            "pair whose other side never ran the arm, or does not carry UDP, or measured a different port-53 "
            "path, makes no claim and is reported as 'n/a (not comparable)' outside the family. The adjusted "
            "verdict requires the adjusted p-value of the claim actually made to be below 0.05 (the "
            "no-difference p-value for 'different', the bootstrap TOST equivalence p-value for 'same'). A "
            "metric with no pre-declared threshold (throughput, event counts) reports its CI as "
            "'no-threshold-declared' instead of guessing.",
        },
        "findings": [finding.as_dict() for finding in ctx.findings],
        "findings_by_severity": {
            severity: len([f for f in ctx.findings if f.severity == severity]) for severity in SEVERITY_ORDER
        },
        "control_blocks": {
            "available": drift["available"],
            "comparisons": [
                {
                    "metric": entry["metric"],
                    "key": entry["key"],
                    "kind": entry["kind"],
                    "unit": entry["unit"],
                    "threshold": entry["threshold"],
                    "pre": entry["pre"],
                    "post": entry["post"],
                    "unavailable": entry["unavailable"],
                    "estimate": entry["comparison"].get("estimate"),
                    "ci95": [entry["comparison"].get("ci_low"), entry["comparison"].get("ci_high")],
                    "statement": entry["statement"],
                    "verdict": entry["verdict"],
                    "reason": entry["reason"],
                }
                for entry in drift["comparisons"]
            ],
            "per_pass": drift["per_pass"],
        },
        "dual_phase": {
            "rows": [
                {
                    "pass": record["pass"],
                    "row": record["row"],
                    "direct_leak": record["leak"],
                    "proxied_latency_p50_us": record.get("proxied_lat_p50"),
                    "direct_latency_p50_us": record.get("direct_lat_p50"),
                    "proxied_loss_rate_pp": record.get("proxied_loss_rate"),
                    "direct_loss_rate_pp": record.get("direct_loss_rate"),
                    "shape_notes": record["shape_notes"],
                }
                for record in dual_records(ctx)
            ]
        },
        "ledger": {
            "available": views["available"],
            "passes": {
                pass_id: {
                    "paths": entry["paths"],
                    "records": entry["records"],
                    "types": entry["types"],
                    "labels": entry["labels"],
                    "bad_lines": entry["bad_lines"],
                    "attribution": views["attribution"].get(pass_id),
                }
                for pass_id, entry in views["passes"].items()
            },
            "dns_ports": {
                port: {key: (sorted(value) if isinstance(value, set) else value) for key, value in totals.items()}
                for port, totals in ledger_dns_totals(ctx, ctx.pass_ids[0] if ctx.pass_ids else "").items()
            },
        },
        "metrics": metrics_out,
    }


PLOT_FILES = [
    "latency-percentiles.png",
    "loss-rates.png",
    "rel-outcomes.png",
    "mix-private-bytes.png",
    "cpu-per-arm.png",
    "lat-p99-by-pass.png",
    "dual-direct-lanes.png",
]


def plot_latency_percentiles(ctx, plt, out_dir):
    """Percentile curve per row for LAT and LATLOAD tcp-rtt, latency on a log x-axis."""
    stats = ["p50Us", "p90Us", "p99Us", "p999Us"]
    probabilities = [50, 90, 99, 99.9]
    fig, axes = plt.subplots(1, 2, figsize=(11, 4.6), sharey=True)
    for axis, arm_name in zip(axes, ["LAT", "LATLOAD"]):
        plotted = 0
        for row_id in ctx.row_ids:
            series = []
            for stat in stats:
                values = []
                for pass_id in ctx.pass_ids:
                    row = ctx.row_in(pass_id, row_id)
                    if row is None:
                        continue
                    value, _ = arm_latency(row, arm_name, "tcp-rtt", stat)
                    if value is not None:
                        values.append(value)
                series.append(median(values) if values else None)
            if any(value is None for value in series):
                continue
            axis.plot(series, probabilities, marker="o", label=row_id)
            plotted += 1
        axis.set_xscale("log")
        axis.set_title("%s arm — tcp-rtt percentiles" % arm_name)
        axis.set_xlabel("latency (us, log scale)")
        axis.grid(True, which="both", alpha=0.3)
        if plotted == 0:
            axis.text(0.5, 0.5, "n/a (no tcp-rtt histogram)", ha="center", transform=axis.transAxes)
        if axis.get_legend_handles_labels()[1]:
            axis.legend(fontsize=7, loc="lower right")
    axes[0].set_ylabel("percentile (%)")
    fig.suptitle("tcp-rtt percentile curve per row (median across passes)")
    fig.tight_layout()
    fig.savefig(out_dir / PLOT_FILES[0], dpi=150)
    plt.close(fig)


def plot_loss_rates(ctx, plt, out_dir):
    """Grouped bars of LOSS lossRate and corruptRate, rule-of-three bound as error bar."""
    row_ids = [row_id for row_id in ctx.row_ids if row_id not in UDP_INCAPABLE_ROWS]
    loss_med, loss_err, corrupt_med = [], [], []
    for row_id in row_ids:
        losses, corrupts, bounds = [], [], []
        for pass_id in ctx.pass_ids:
            row = ctx.row_in(pass_id, row_id)
            if row is None:
                continue
            value, _ = arm_number(row, "LOSS", "metrics/lossRate")
            if value is not None:
                losses.append(value * 100.0)
            value, _ = arm_number(row, "LOSS", "metrics/corruptRate")
            if value is not None:
                corrupts.append(value * 100.0)
            sent, _ = arm_number(row, "LOSS", "metrics/sent")
            if sent:
                bounds.append(300.0 / sent)
        loss = median(losses) if losses else 0.0
        loss_med.append(loss)
        loss_err.append(max(bounds) if bounds and loss == 0 else 0.0)
        corrupt_med.append(median(corrupts) if corrupts else 0.0)
    positions = list(range(len(row_ids)))
    fig, axis = plt.subplots(figsize=(max(7.0, 1.1 * max(1, len(row_ids))), 4.6))
    width = 0.38
    axis.bar([p - width / 2 for p in positions], loss_med, width, yerr=[loss_err, loss_err], capsize=3, label="LOSS lossRate")
    axis.bar([p + width / 2 for p in positions], corrupt_med, width, label="LOSS corruptRate")
    axis.set_xticks(positions)
    axis.set_xticklabels(row_ids, rotation=30, ha="right")
    axis.set_ylabel("rate (% of sent)")
    axis.set_title("LOSS arm rates per row (median across passes; zero bars carry the < 3/n bound)")
    axis.legend(fontsize=8)
    axis.grid(True, axis="y", alpha=0.3)
    fig.tight_layout()
    fig.savefig(out_dir / PLOT_FILES[1], dpi=150)
    plt.close(fig)


def plot_rel_outcomes(ctx, plt, out_dir):
    """Stacked bar of the REL outcome distribution per row (rates over connectAttempts)."""
    series = {key: [] for key in OUTCOME_KEYS}
    kept = []
    for row_id in ctx.row_ids:
        per_key = {}
        for pass_id in ctx.pass_ids:
            row = ctx.row_in(pass_id, row_id)
            if row is None:
                continue
            attempts, _ = arm_number(row, "REL", "metrics/connectAttempts")
            if not attempts:
                continue
            for key in OUTCOME_KEYS:
                value, _ = arm_number(row, "REL", "metrics/outcomes/" + key)
                if value is not None:
                    per_key.setdefault(key, []).append(100.0 * value / attempts)
        if not per_key:
            continue
        kept.append(row_id)
        for key in OUTCOME_KEYS:
            series[key].append(median(per_key.get(key, [0.0])))
    fig, axis = plt.subplots(figsize=(max(7.0, 1.1 * max(1, len(kept))), 4.6))
    bottom = [0.0] * len(kept)
    for key in OUTCOME_KEYS:
        axis.bar(kept, series[key], bottom=bottom, label=key)
        bottom = [low + value for low, value in zip(bottom, series[key])]
    axis.set_ylabel("share of connectAttempts (%)")
    axis.set_title("REL arm outcome distribution per row (median across passes)")
    axis.tick_params(axis="x", rotation=30)
    axis.legend(fontsize=7, ncol=2)
    axis.grid(True, axis="y", alpha=0.3)
    fig.tight_layout()
    fig.savefig(out_dir / PLOT_FILES[2], dpi=150)
    plt.close(fig)


def plot_mix_private_bytes(ctx, plt, out_dir):
    """Private bytes over time per row inside the MIX arm."""
    fig, axis = plt.subplots(figsize=(9, 4.8))
    plotted = 0
    for row_id in ctx.row_ids:
        for pass_id in ctx.pass_ids:
            row = ctx.row_in(pass_id, row_id)
            if row is None:
                continue
            primary, _ = primary_product_process(row)
            if primary is None or not row.tick_frequency:
                continue
            pairs = [
                (as_number(s.get("ticks")), process_private_bytes(s))
                for s in present_product_samples(row, "MIX", primary)
            ]
            pairs = [(t, v) for t, v in pairs if t is not None and v is not None]
            if not pairs:
                continue
            base = pairs[0][0]
            axis.plot(
                [(t - base) / row.tick_frequency for t, _ in pairs],
                [v / MIB for _, v in pairs],
                marker=".",
                label="%s (%s)" % (row_id, pass_id),
            )
            plotted += 1
    if plotted:
        axis.legend(fontsize=7)
    else:
        axis.text(0.5, 0.5, "n/a (no MIX product samples)", ha="center", transform=axis.transAxes)
    axis.set_xlabel("seconds into the MIX arm")
    axis.set_ylabel("private bytes (MiB)")
    axis.set_title("MIX arm — product private bytes over time")
    axis.grid(True, alpha=0.3)
    fig.tight_layout()
    fig.savefig(out_dir / PLOT_FILES[3], dpi=150)
    plt.close(fig)


def plot_cpu_per_arm(ctx, plt, out_dir):
    """Proxy CPU percent of one vCPU per row per arm."""
    row_ids = ctx.row_ids
    arms = [name for name in ARM_ORDER if any(name in row.arms for row in ctx.rows)] or ["IDLE"]
    width = 0.8 / len(arms)
    fig, axis = plt.subplots(figsize=(max(7.5, 1.3 * max(1, len(row_ids))), 4.8))
    for index, arm_name in enumerate(arms):
        values = []
        for row_id in row_ids:
            per_pass = []
            for pass_id in ctx.pass_ids:
                row = ctx.row_in(pass_id, row_id)
                if row is None:
                    continue
                primary, _ = primary_product_process(row)
                if primary is None:
                    continue
                value, _ = cpu_percent(
                    present_product_samples(row, arm_name, primary), "cpuSeconds", row.tick_frequency
                )
                if value is not None:
                    per_pass.append(value)
            values.append(median(per_pass) if per_pass else 0.0)
        axis.bar([p + index * width for p in range(len(row_ids))], values, width, label=arm_name)
    axis.set_xticks([p + 0.4 - width / 2 for p in range(len(row_ids))])
    axis.set_xticklabels(row_ids, rotation=30, ha="right")
    axis.set_ylabel("proxy CPU (% of one vCPU)")
    axis.set_title("Proxy CPU per arm per row (median across passes, per-process identity)")
    axis.legend(fontsize=7, ncol=2)
    axis.grid(True, axis="y", alpha=0.3)
    fig.tight_layout()
    fig.savefig(out_dir / PLOT_FILES[4], dpi=150)
    plt.close(fig)


def plot_lat_p99_by_pass(ctx, plt, out_dir):
    """Run-order plot: LAT tcp-rtt p99 against pass index, one line per row."""
    fig, axis = plt.subplots(figsize=(8, 4.6))
    plotted = 0
    for row_id in ctx.row_ids:
        xs, ys = [], []
        for index, pass_id in enumerate(ctx.pass_ids):
            row = ctx.row_in(pass_id, row_id)
            if row is None:
                continue
            value, _ = arm_latency(row, "LAT", "tcp-rtt", "p99Us")
            if value is not None:
                xs.append(index + 1)
                ys.append(value)
        if xs:
            axis.plot(xs, ys, marker="o", label=row_id)
            plotted += 1
    if plotted:
        axis.legend(fontsize=7)
    else:
        axis.text(0.5, 0.5, "n/a (no LAT tcp-rtt p99)", ha="center", transform=axis.transAxes)
    axis.set_xlabel("pass index (within-pass run order lives in passN/order.txt)")
    axis.set_ylabel("LAT tcp-rtt p99 (us)")
    axis.set_title("Run-order drift check")
    axis.grid(True, alpha=0.3)
    fig.tight_layout()
    fig.savefig(out_dir / PLOT_FILES[5], dpi=150)
    plt.close(fig)


def plot_dual_direct_lanes(ctx, plt, out_dir):
    """Direct-lane LAT tcp-rtt p50 and LOSS lossRate per row, next to the proxied lane."""
    summary = dual_row_summary(ctx)
    rows = [row_id for row_id in ctx.row_ids if row_id in summary]
    fig, axes = plt.subplots(1, 2, figsize=(max(8.0, 1.3 * max(1, len(rows))), 4.6))
    positions = list(range(len(rows)))
    for axis, key, title, unit in (
        (axes[0], "lat_p50", "LAT tcp-rtt p50", "us"),
        (axes[1], "loss", "LOSS lossRate", "pp"),
    ):
        proxied = [median(summary[row]["proxied_" + key]) if summary[row]["proxied_" + key] else 0.0 for row in rows]
        direct = [median(summary[row]["direct_" + key]) if summary[row]["direct_" + key] else 0.0 for row in rows]
        width = 0.38
        axis.bar([p - width / 2 for p in positions], proxied, width, label="proxied lane")
        axis.bar([p + width / 2 for p in positions], direct, width, label="direct lane")
        axis.set_xticks(positions)
        axis.set_xticklabels(rows, rotation=30, ha="right")
        axis.set_ylabel("%s (%s)" % (title, unit))
        axis.set_title(title)
        axis.legend(fontsize=8)
        axis.grid(True, axis="y", alpha=0.3)
    fig.suptitle("Dual phase — the direct lane is the path, the proxied lane is the product")
    fig.tight_layout()
    fig.savefig(out_dir / PLOT_FILES[6], dpi=150)
    plt.close(fig)


def build_plots(ctx, plots_dir):
    plots_dir.mkdir(parents=True, exist_ok=True)
    try:
        import matplotlib

        matplotlib.use("Agg")
        import matplotlib.pyplot as plt
    except Exception as exc:
        (plots_dir / "SKIPPED.md").write_text(
            "\n".join(
                [
                    "# Plots skipped",
                    "",
                    "matplotlib could not be imported, so no PNG was written:",
                    "",
                    "```",
                    "%s: %s" % (exc.__class__.__name__, exc),
                    "```",
                    "",
                    "`tables.md` and `verdict.json` are complete without it — plotting is the only part",
                    "of the analysis that needs matplotlib, and no number in either file comes from a",
                    "plot.",
                    "",
                    "To generate the plots, run the same command with an interpreter that has matplotlib:",
                    "",
                    "```bash",
                    "python3 -m venv /tmp/wf-analysis-venv",
                    "/tmp/wf-analysis-venv/bin/pip install matplotlib",
                    "/tmp/wf-analysis-venv/bin/python analyze.py --raw <raw> --out <out>",
                    "```",
                    "",
                    "or with nix:",
                    "",
                    "```bash",
                    "nix shell nixpkgs#python3Packages.matplotlib -c \\",
                    "  python3 analyze.py --raw <raw> --out <out>",
                    "```",
                    "",
                    "The script writes these files into `plots/` when matplotlib is available:",
                    "",
                ]
                + ["- `%s`" % name for name in PLOT_FILES]
                + [
                    "",
                    "An ECDF per arm is impossible from the summary histograms the harness writes, so",
                    "the percentile curve uses p50/p90/p99/p999 from those histograms instead.",
                    "",
                ]
            ),
            encoding="utf-8",
        )
        return {"matplotlib": False, "reason": str(exc), "files": []}

    written = []
    for name, function in (
        (PLOT_FILES[0], plot_latency_percentiles),
        (PLOT_FILES[1], plot_loss_rates),
        (PLOT_FILES[2], plot_rel_outcomes),
        (PLOT_FILES[3], plot_mix_private_bytes),
        (PLOT_FILES[4], plot_cpu_per_arm),
        (PLOT_FILES[5], plot_lat_p99_by_pass),
        (PLOT_FILES[6], plot_dual_direct_lanes),
    ):
        try:
            function(ctx, plt, plots_dir)
            written.append(name)
        except Exception as exc:
            print("warning: plot %s failed: %s: %s" % (name, exc.__class__.__name__, exc), file=sys.stderr)
    stale = plots_dir / "SKIPPED.md"
    if written and stale.exists():
        stale.unlink()
    return {"matplotlib": True, "files": written}


def parse_args(argv):
    parser = argparse.ArgumentParser(add_help=True, usage=USAGE)
    parser.add_argument("--raw", default=DEFAULT_RAW, help="campaign tree (default: %s)" % DEFAULT_RAW)
    parser.add_argument("--out", default=DEFAULT_OUT, help="output directory (default: %s)" % DEFAULT_OUT)
    parser.add_argument(
        "--ledger",
        action="append",
        default=None,
        help="target ledger JSONL (repeatable: a campaign runs one target per lane); default: search the pass "
        "directories, --raw and --raw's parent for *ledger*.jsonl",
    )
    parser.add_argument(
        "--flat",
        action="store_true",
        help="treat the immediate subdirectories of --raw as rows of one implicit pass",
    )
    parser.add_argument("--warmup-seconds", type=float, default=DEFAULT_WARMUP_SECONDS)
    parser.add_argument("--resamples", type=int, default=DEFAULT_RESAMPLES)
    parser.add_argument("--seed", type=int, default=DEFAULT_SEED)
    return parser.parse_args(argv)


def main(argv=None):
    args = parse_args(argv)
    raw = Path(args.raw)
    if not raw.is_dir():
        print("analyze.py: input directory not found: %s" % raw, file=sys.stderr)
        print("usage: " + USAGE, file=sys.stderr)
        return 2
    passes, order, ledgers, ledger_paths = discover(raw, args.flat, args.ledger)
    if not passes:
        print(
            "analyze.py: no rows found under %s (%s)"
            % (
                raw,
                "immediate subdirectories with run.json or *.jsonl"
                if args.flat
                else "pass*/<row>/ directories",
            ),
            file=sys.stderr,
        )
        if not args.flat:
            print("analyze.py: a one-off flat run tree can be read with --flat", file=sys.stderr)
        print("usage: " + USAGE, file=sys.stderr)
        return 2

    environment = None
    for candidate in (raw / "environment.json", raw.parent / "environment.json"):
        if candidate.is_file():
            try:
                environment = json.loads(candidate.read_text(encoding="utf-8-sig"))
            except (OSError, json.JSONDecodeError):
                environment = None
            break

    ctx = Context(
        raw=raw,
        out=Path(args.out),
        flat=args.flat,
        passes=passes,
        warmup_seconds=args.warmup_seconds,
        resamples=args.resamples,
        seed=args.seed,
        min_passes=DEFAULT_MIN_PASSES,
        environment=environment,
        order=order,
        ledger_paths=ledger_paths,
        ledgers=ledgers,
    )
    findings = collect_findings(ctx) + dual_findings(ctx)
    deduped = []
    seen = set()
    for finding in findings:
        key = (finding.severity, finding.kind, finding.scope, finding.detail)
        if key in seen:
            continue
        seen.add(key)
        deduped.append(finding)
    ctx.findings = deduped
    ctx.out.mkdir(parents=True, exist_ok=True)
    (ctx.out / "tables.md").write_text(build_tables_md(ctx), encoding="utf-8")
    (ctx.out / "verdict.json").write_text(
        json.dumps(build_verdict(ctx), indent=2, sort_keys=False) + "\n", encoding="utf-8"
    )
    plots = build_plots(ctx, ctx.out / "plots")

    by_severity = findings_by_severity(ctx.findings)
    print(
        "analyze.py: %d pass(es), %d row(s), %d metric(s) -> %s"
        % (len(ctx.pass_ids), len(ctx.row_ids), len(METRIC_SPECS), ctx.out / "tables.md")
    )
    print("analyze.py: verdict -> %s" % (ctx.out / "verdict.json"))
    print(
        "analyze.py: findings: "
        + ", ".join("%d %s" % (len(by_severity[severity]), severity) for severity in SEVERITY_ORDER)
    )
    if plots["matplotlib"]:
        print("analyze.py: plots -> %s" % ", ".join(plots["files"]))
    else:
        print("analyze.py: matplotlib unavailable, wrote plots/SKIPPED.md (%s)" % plots["reason"])
    return 0


if __name__ == "__main__":
    sys.exit(main())
