#!/usr/bin/env python3
"""Generate the synthetic multi-pass campaign tree the analysis oracle is frozen on.

The tree is fabricated but every record keeps the harness's own schema (record types, key
sets, and the three states a key can be in: present with a value, present and JSON null,
absent because the kind does not publish it), and it deliberately contains:

* the seven measured rows with their different plans plus both control blocks;
* a dual phase on the five full-plan rows, with a `directLeak` on one of them;
* a `DNSALT` arm on every row that runs DNS, and a `PERSIST` arm on every full-plan row;
* a `foreignConnection` finding, a UDP accounting-identity violation, a zero lane
  witness, a `samplerError` record, a sample carrying `readError`, a mid-run product
  restart, a `scheduledAttempts` mismatch and a `control-post` drift;
* the reliability arm's per-attempt records and its `byMode` block;
* a `target-ledger.jsonl` per pass whose source census, connection records and per-port
  DNS summaries agree with the client records (except where a mismatch is intentional);
* per-run ledger labels in one pass and a single pass-level label in another, so both
  attribution paths are exercised.

The tree is the one input of the two-implementation oracle, so its **key set** is part of
the frozen contract and is checked mechanically: `../check-fixture-drift.py` flattens
everything this script writes and compares the result, in both directions, against the
paths the harness contract declares (`contract-inventory.json` composed with the rename
table `contract-rename.json`). Values are deliberately *not* covered by that check: every
number here is invented, and the oracle compares the two implementations with each other,
never with the harness. **An edit here changes the frozen tree, so it requires the golden
oracle to be frozen again** (see `../FROZEN.md`).

Layout (the tarball's root is the *contents* of the raw directory's parent):

    <raw>/                     the tree the analysis is pointed at with `--raw`
    <raw>/../*ledger*.jsonl    the target ledgers, beside the raw tree
    <raw>/../plan-<pass>.json  the plan files `run.json`'s `planPath` points at

Usage: python3 make_tree.py [--window-overflow ROW] [--zero-denominator] [--undecodable N]
                            [--truncated-tcp N] [--truncated-dns N] [<output-raw-dir>]

`--window-overflow ROW` makes that row's `LAT` arm report a reached in-flight ceiling in
pass 3 (`gates.windowOverflow = 3`, with `supplied == sentOk` and `clientSendLoss = 0`,
because a deferral that went out is not a drop). The default tree has no reached ceiling, so
the analysis's rendering of an ordinary latency row is byte-for-byte what it was.

`--zero-denominator` writes the shapes a **zero denominator** produces, which the clean tree
cannot reach and the reference cannot survive (D21.2 #2):

* pass1's `wf-aot-opt` `LAT` arm publishes `udp.sent = 0`, so its client-side datagram count
  is the float zero and §14.2 has no band to judge the target's census with -- the shape the
  retired reference raises `TypeError` on (`analyze.py:5037`) and this analysis answers with `n/a`;
* every pass's `wf-fdd-opt` `PERSIST` arm publishes `requests = 0`, which the harness writes
  as an explicit JSON `null` rate (`responseRate`), so the metric has no per-pass value at all
  and its cell is **empty** rather than a zero;
* pass1's `wf-aot-opt` `MIX` arm publishes `classes.udp.lossRate = null` (a null is the
  harness saying the denominator was zero, so the arm-level fallback must **not** fire) and
  pass2's drops the `classes.udp` block altogether (a missing field, so the fallback **must**
  fire).

`--undecodable N` makes the main ledger report `N` datagrams the target could not decode,
written the way the target writes them: a running total, so the last `udpSummary` of the
campaign and the closing `targetSummary/udp` block both carry `N`. The datagrams arrive from
no particular arm — that is the point of the counter — so the knob adds no per-arm record.

`--truncated-tcp N` and `--truncated-dns N` make the main ledger report `N` frames the peer
cut off mid-frame: the TCP counter is the echo listener's own (`tcpSummary/truncatedFrames`
and `targetSummary/tcp/truncatedFrames`), the DNS counter is a length-prefix short read by
one of the two DNS listeners (`dnsSummary/truncatedFrames`, `targetSummary/dns/truncatedFrames`
and `targetSummary/dnsAlt/truncatedFrames`). Two mechanisms share one spelling, so the flags
are separate. Under `--truncated-tcp` the cut-off connections also carry the target's existing
`protocolError` verdict rather than a verdict of their own, exactly as the server records them.

All three counter flags default to zero, and a counter is published whether or not it is
non-zero: they change values, never the key set, so the default tree is the clean campaign
and the boundary trees (`--window-overflow`, `--undecodable`, `--truncated-*`) are only ever
asserted against the C# analysis, never diffed between the two implementations.

Two ledgers are generated, as the shipped `start-targets.sh` starts two target instances: the
proxied rows' traffic goes to `ledger-main.jsonl` (port 40010, dns 53) and the dual phase's
direct lane to `ledger-direct.jsonl` (port 40011, dns 40054), both beside the raw directory
rather than inside it. Every run carries its own label, exactly as the orchestrator labels
them.
"""

from __future__ import annotations

import json
import random
import shutil
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path

FREQ = 10_000_000
T0 = datetime(2026, 10, 6, 8, 0, 0, tzinfo=timezone.utc)
PASS_SECONDS = 3600
PROXY_ENDPOINT = "192.168.77.2:51234"
CLIENT_ENDPOINT = "192.168.77.2:40001"
DIRECT_ENDPOINT = "192.168.77.2:40077"
TARGET_ADDRESS = "192.168.77.4"

ARM_SECONDS = {
    "IDLE": 10,
    "LAT": 30,
    "LATLOAD": 30,
    "DNS": 20,
    "DNSALT": 20,
    "LOSS": 30,
    "REL": 30,
    "THRU": 20,
    "MIX": 40,
    "PERSIST": 30,
    "BASE": 40,
}

FULL_ARMS = ["IDLE", "LAT", "LATLOAD", "DNS", "DNSALT", "LOSS", "REL", "THRU", "MIX", "PERSIST"]
PLAN_ARMS = {
    "full": FULL_ARMS,
    "udp only": ["LAT", "LATLOAD", "LOSS"],
    "dns only": ["DNS", "DNSALT"],
    "BASE only": ["BASE"],
}

ROWS = [
    # row id, plan, dual, sampler process, proxy endpoint, base tcp-rtt p50 us
    ("control-pre", "BASE only", False, None, "192.168.77.2:40055", 600.0),
    ("wf-aot-opt", "full", True, "WinForward", PROXY_ENDPOINT, 650.0),
    ("wf-fdd-opt", "full", True, "WinForward", PROXY_ENDPOINT, 665.0),
    ("wf-aot-nativeudp", "udp only", False, "WinForward", PROXY_ENDPOINT, 700.0),
    ("wf-aot-dnsrelay", "dns only", False, "WinForward", PROXY_ENDPOINT, 655.0),
    ("proxifyre", "full", True, "ProxiFyre", PROXY_ENDPOINT, 900.0),
    ("proxifier", "full", True, "Proxifier", "192.168.77.2:40099", 1400.0),
    ("proxybridge", "full", True, "ProxyBridge_CLI", PROXY_ENDPOINT, 1100.0),
    ("control-post", "BASE only", False, None, "192.168.77.2:40055", 610.0),
]

UDP_CARRIAGE = {
    "wf-aot-opt": "utcp",
    "wf-fdd-opt": "utcp",
    "wf-aot-nativeudp": "native",
    "wf-aot-dnsrelay": "utcp",
    "proxifyre": "native",
    "proxifier": "none",
    "proxybridge": "native",
}

KIND_OF_ARM = {
    "IDLE": "idle",
    "LAT": "latency",
    "LATLOAD": "latency",
    "DNS": "dns",
    "DNSALT": "dns",
    "LOSS": "loss",
    "REL": "reliability",
    "THRU": "throughput",
    "MIX": "mix",
    "PERSIST": "persistent",
    "BASE": "base",
}

DNS_PORTS = {"DNS": 53, "DNSALT": 40053}
PROXIED_TCP_PORT = 40010
DIRECT_TCP_PORT = 40011
PROXIED_DNS_PORT = 53
DIRECT_DNS_PORT = 40054

PRODUCT_LOSS_WINDOW = {"wf-aot-opt": {1: 500}, "proxifyre": {2: 250}}

# The verdict names `tcpSummary.verdicts` and `targetSummary.tcp.verdicts` publish, in write order.
TCP_VERDICT_NAMES = ("clean", "reset", "partialFin", "halfClose", "stall", "clientClosedEarly",
                     "protocolError", "error")

# The UDP echo listener's receive loops that started, which the target publishes per summary.
UDP_RECEIVERS = 4

AUTO_LABELS = {"pass1": True, "pass2": True, "pass3": True}

ATTEMPT_STRIDE = 16
ATTEMPT_RECORD_CAP = 4096


def iso(stamp):
    return stamp.strftime("%Y-%m-%dT%H:%M:%S.%f") + "+00:00"


class Builder:
    def __init__(self, out: Path):
        self.out = out
        self.records = []
        self.tick = 0
        self.utc = T0

    def advance(self, seconds):
        self.tick += int(seconds * FREQ)
        self.utc += timedelta(seconds=seconds)
        return self.tick

    def write_jsonl(self, path: Path, records):
        path.parent.mkdir(parents=True, exist_ok=True)
        with path.open("w", encoding="utf-8") as handle:
            for record in records:
                handle.write(json.dumps(record, separators=(",", ":")) + "\n")

    def write_json(self, path: Path, payload):
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")


def histogram(rng, count, p50, p99):
    if count <= 0:
        return None
    return {
        "count": count,
        "minUs": round(max(50.0, p50 * 0.6), 3),
        "maxUs": round(max(p50, p99) * 1.4, 3),
        "meanUs": round(p50 * 1.25, 3),
        "p50Us": round(p50, 3),
        "p90Us": round(p50 * 1.35, 3),
        "p99Us": round(p99, 3),
        "p999Us": round(p99 * 1.6, 3),
    }


def attempt_records(records, attempts, modes, per_mode, echoed_bytes, trailer_bytes):
    """Append the REL arm's `attempt` records to `records`; return (written, omitted).

    The arm keeps a bounded evidence budget: a disconfirming attempt (observed != expected, or a
    truncated echo) is written outright and an ordinary one is sampled at one in `ATTEMPT_STRIDE`,
    up to `ATTEMPT_RECORD_CAP` records for the whole arm. Every attempt in this fixture observes
    what its own mode calls for, so the budget is spent on the sampled ordinary attempts alone.
    """
    ordinary = [(mode, index) for mode in modes for index in range(per_mode[mode])]
    chosen = ordinary[ATTEMPT_STRIDE - 1::ATTEMPT_STRIDE]
    written = chosen[:ATTEMPT_RECORD_CAP]
    for mode, index in written:
        observed = {"clean": "clean", "resetAfterN": "reset", "partialFin": "unexpectedEof",
                    "halfClose": "clean"}[mode]
        cut_short = mode == "partialFin"
        records.append(
            {
                "type": "attempt",
                "connectionId": 0x6000_0000 + index,
                "mode": mode,
                "status": "exchanged",
                "observed": observed,
                "expected": observed,
                "truncated": False,
                "echoedBytes": 4096 if cut_short else echoed_bytes,
                "trailerBytes": 0 if cut_short else trailer_bytes,
                "eof": mode in ("partialFin", "halfClose"),
                "reset": mode == "resetAfterN",
                "protocolError": False,
                "otherError": False,
                "connectTicks": 120_000 + index,
                "transferTicks": 240_000 + index,
            }
        )
    return len(written), len(ordinary) - len(chosen)


def loss_block(rng, sent, rate, window_ms, corrupt=0, foreign=0, identity_break=False,
               abandoned_at_teardown=0, out_of_range=False):
    """The LOSS/MIX UDP classification, with the identity holding unless asked otherwise.

    ``out_of_range`` moves two datagrams out of the arrival population the way a tracker whose
    bounded sequence space was exhausted records them: one *received* datagram whose sequence it
    refused, and one *offered* slot it refused to send. The identity keeps holding because both
    counters describe the same two datagrams from opposite ends.
    """
    late = int(round(sent * rate * 0.4))
    never = int(round(sent * rate * 0.6))
    corrupt_datagrams = corrupt
    abandoned = abandoned_at_teardown
    arrived = sent - late - never - corrupt_datagrams - abandoned
    if identity_break:
        arrived += 1
    loss = late + never
    duplicate = 1 if sent % 7 == 0 else 0
    reordered = 2 if sent % 11 == 0 else 0
    return {
        "sent": sent,
        "arrived": arrived,
        "late": late,
        "never": never,
        "corrupt": corrupt,
        "corruptDatagrams": corrupt_datagrams,
        "duplicate": duplicate,
        "reordered": reordered,
        "unmatchedReplies": 1 if sent % 13 == 0 else 0,
        "foreignConnection": foreign,
        "abandonedAtTeardown": abandoned,
        "clientSendLoss": abandoned,
        "lossRate": round(loss / sent, 6) if sent else None,
        "strictLossRate": round((loss + corrupt) / sent, 6) if sent else None,
        "lateRate": round(late / sent, 6) if sent else None,
        "corruptRate": round(corrupt / sent, 6) if sent else None,
        "duplicateRate": round(duplicate / sent, 6) if sent else None,
        "reorderRate": round(reordered / sent, 6) if sent else None,
        "clientSendLossRate": round(abandoned / sent, 6) if sent else None,
        "outOfRangeSequences": 1 if out_of_range else 0,
        "sentOutOfRangeSequences": 1 if out_of_range else 0,
        "window": float(window_ms),
    }


def samples_for(builder, arm, seconds, process, rng, loaded=True, read_error_at=None, restart_at=None):
    """1 Hz samples for one arm, with optional readError and mid-run restart injections."""
    out = []
    start_tick = builder.tick
    start_utc = builder.utc
    cpu_product = 4.0 + rng.random()
    cpu_generator = 2.0 + rng.random()
    private = 90 * 1024 * 1024 + int(rng.random() * 5_000_000)
    pid, started = 1000 + rng.randrange(500), iso(start_utc)
    pid2, started2 = 2000 + rng.randrange(500), iso(start_utc + timedelta(seconds=seconds / 2 + 1))
    for index in range(seconds):
        tick = start_tick + index * FREQ
        utc = start_utc + timedelta(seconds=index)
        cpu_product += (0.45 if loaded else 0.02) + rng.random() * 0.05
        cpu_generator += (0.30 if loaded else 0.01) + rng.random() * 0.04
        use_second = restart_at is not None and index >= restart_at
        identity_pid = pid2 if use_second else pid
        identity_start = started2 if use_second else started
        cpu_value = (0.2 + index * 0.45) if use_second else cpu_product
        read_error = read_error_at == index
        process_entry = {
            "pid": identity_pid,
            "startUtc": identity_start,
            "countersRead": not read_error,
            "cpuSeconds": None if read_error else round(cpu_value, 4),
            "privateBytes": None if read_error else private + index * 4096,
        }
        sample = {
            "type": "sample",
            "ticks": tick,
            "arm": arm,
            "process": process or "unused",
            "self": False,
            "matched": 1,
            "cpuSeconds": None if read_error else round(cpu_value, 4),
            "privateBytes": None if read_error else private + index * 4096,
            "workingSetBytes": private // 2 + index * 2048,
            "peakWorkingSetBytes": private // 2 + index * 3072,
            "threads": 18,
            "handles": None,
            "processes": [process_entry],
            "readErrors": 1 if read_error else 0,
        }
        if read_error:
            sample["readError"] = True
        out.append(sample)
        self_sample = {
            "type": "sample",
            "ticks": tick,
            "arm": arm,
            "process": "WinForward.E2E",
            "self": True,
            "matched": 1,
            "cpuSeconds": round(cpu_generator, 4),
            "privateBytes": 200 * 1024 * 1024,
            "workingSetBytes": 70 * 1024 * 1024,
            "peakWorkingSetBytes": 72 * 1024 * 1024,
            "threads": 19,
            "handles": None,
            "processes": [
                {
                    "pid": 4242,
                    "startUtc": iso(start_utc),
                    "countersRead": True,
                    "cpuSeconds": round(cpu_generator, 4),
                    "privateBytes": 200 * 1024 * 1024,
                }
            ],
            "readErrors": 0,
            "generatorCpuSeconds": round(cpu_generator, 4),
            "envWorkingSetBytes": 70 * 1024 * 1024,
        }
        out.append(self_sample)
    builder.advance(seconds)
    return out


def build_arm(builder, arm, row_id, plan, pass_index, rng, product_process, notes_extra=(), injections=None):
    """(records, latency, metrics, parameters, gates, windows) for one arm of one row."""
    injections = injections or {}
    seconds = ARM_SECONDS[arm]
    loaded = arm != "IDLE"
    records = []
    latency = {}
    metrics = {}
    parameters = {"seconds": seconds}
    gates = {"clientSendLoss": 0, "windowMs": 0}
    windows = {"udp_echo": None, "tcp_connections": 0, "udp_datagrams": 0, "dns": {}}
    window_ms = PRODUCT_LOSS_WINDOW.get(row_id, {}).get(pass_index, 200)

    if product_process:
        records.extend(
            samples_for(
                builder,
                arm,
                seconds,
                product_process,
                rng,
                loaded=loaded,
                read_error_at=injections.get("read_error_at"),
                restart_at=injections.get("restart_at"),
            )
        )
    else:
        builder.advance(seconds)

    if arm == "IDLE":
        metrics["elapsedSeconds"] = float(seconds)
        parameters["traffic"] = "none"
        gates["clientSendLoss"] = 0
        gates["windowMs"] = 0
    elif arm in ("LAT", "LATLOAD"):
        lanes = 2 if arm == "LAT" else 1
        rate = 20 if arm == "LAT" else 200
        p50 = injections.get("latency_scale", 1.0) * rng.uniform(0.97, 1.03) * (650.0 if arm == "LAT" else 900.0)
        sent_tcp = rate * seconds
        sent_udp = rate * seconds
        carries_udp = plan != "dns only"
        udp_only = plan == "udp only"
        connections = lanes + seconds
        parameters.update(
            {"ratePerSecond": rate, "payloadBytes": 120,
             "protocol": "udp" if udp_only else ("tcp+udp" if carries_udp else "tcp"),
             "lanes": lanes, "inFlightWindow": 4096}
        )
        if not udp_only:
            latency["tcp-connect"] = histogram(rng, connections, p50 * 0.7, p50 * 3.0)
            latency["tcp-rtt"] = histogram(rng, sent_tcp, p50, p50 * 4.5)
        # A deferral is not a drop: a window overflow the arm eventually sent leaves supplied == sentOk
        # and clientSendLoss == 0, which is the shape the analysis has to render as unmeasurable.
        overflow = injections.get("window_overflow", 0)
        if carries_udp:
            latency["udp-rtt"] = histogram(rng, sent_udp, p50 * 1.05, p50 * 5.0)
            metrics.update(
                {
                    "tcp.laneStarted": lanes,
                    "tcp.laneSupplied": [sent_tcp // lanes] * lanes,
                    "tcp.laneSentOk": [sent_tcp // lanes] * lanes,
                    "tcp.supplied": sent_tcp,
                    "tcp.sent": sent_tcp,
                    "tcp.windowOverflow": overflow,
                    "tcp.backlogDrops": 0,
                    "tcp.sendFailures": 0,
                    "tcp.sendWouldBlock": 0,
                    "tcp.abandonedAtTeardown": 0,
                    "tcp.outstandingAtTeardown": 0,
                    "tcp.clientSendLoss": 0,
                    "tcp.received": sent_tcp,
                    "tcp.corrupt": 0,
                    "tcp.protocolErrors": 0,
                    "tcp.remoteClosed": 0,
                    "tcp.unmatchedReplies": 0,
                    "tcp.windowCeilingMs": round(1000.0 * 4096 * lanes / rate, 3),
                    "tcp.scheduleTruncated": 0,
                    "tcp.achievedRate": float(rate),
                    "tcp.connectAttempts": connections,
                    "tcp.connectFailures": 0,
                    "tcp.meanConnectMs": 0.5,
                    "udp.laneStarted": 1,
                    "udp.supplied": sent_udp,
                    "udp.sent": sent_udp,
                    "udp.windowOverflow": overflow,
                    "udp.backlogDrops": 0,
                    "udp.sendFailures": 0,
                    "udp.sendWouldBlock": 0,
                    "udp.abandonedAtTeardown": 0,
                    "udp.clientSendLoss": 0,
                    "udp.received": sent_udp,
                    "udp.corrupt": 0,
                    "udp.protocolErrors": 0,
                    "udp.unmatchedReplies": 0,
                    "udp.foreignConnection": injections.get("foreign_connection", 0) if arm == "LAT" else 0,
                    "udp.outstandingAtTeardown": 0,
                    "udp.windowCeilingMs": round(1000.0 * 4096 / rate, 3),
                    "udp.scheduleTruncated": 0,
                    "udp.lossRate": round(injections.get("udp_loss_rate", 0.0), 6),
                    "udp.achievedRate": float(rate),
                }
            )
            if injections.get("zero_udp_sent"):
                # A client-side datagram count of exactly zero: the census the ledger reports has no
                # band to be judged against, because the band is derived from the client's own rate.
                metrics["udp.sent"] = 0
        elif not udp_only:
            metrics.update(
                {
                    "tcp.laneStarted": lanes,
                    "tcp.laneSupplied": [sent_tcp // lanes] * lanes,
                    "tcp.laneSentOk": [sent_tcp // lanes] * lanes,
                    "tcp.supplied": sent_tcp,
                    "tcp.sent": sent_tcp,
                    "tcp.windowOverflow": 0,
                    "tcp.backlogDrops": 0,
                    "tcp.sendFailures": 0,
                    "tcp.sendWouldBlock": 0,
                    "tcp.abandonedAtTeardown": 0,
                    "tcp.outstandingAtTeardown": 0,
                    "tcp.clientSendLoss": 0,
                    "tcp.received": sent_tcp,
                    "tcp.corrupt": 0,
                    "tcp.protocolErrors": 0,
                    "tcp.remoteClosed": 0,
                    "tcp.unmatchedReplies": 0,
                    "tcp.windowCeilingMs": round(1000.0 * 4096 * lanes / rate, 3),
                    "tcp.scheduleTruncated": 0,
                    "tcp.achievedRate": float(rate),
                    "tcp.connectAttempts": connections,
                    "tcp.connectFailures": 0,
                    "tcp.meanConnectMs": 0.5,
                }
            )
        gates.update(
            {
                "clientSendLoss": 0,
                "windowOverflow": overflow,
                "backlogDrops": 0,
                "sendFailures": 0,
                "lanesPlanned": lanes + (1 if carries_udp else 0),
                "lanesStarted": lanes + (1 if carries_udp else 0),
                "laneShortfall": 0,
                "scheduleTruncated": 0,
                "inFlightCeilingMs": round(1000.0 * 4096 * lanes / rate, 3),
                "windowMs": 0,
            }
        )
        if udp_only:
            for key in [key for key in metrics if key.startswith("tcp.")]:
                metrics.pop(key)
            for key in [key for key in gates if key in ("lanesPlanned", "lanesStarted")]:
                gates[key] = 1
        windows["tcp_connections"] = 0 if udp_only else connections
        if carries_udp:
            windows["udp_echo"] = (sent_udp, injections.get("udp_endpoint", CLIENT_ENDPOINT))
    elif arm in ("DNS", "DNSALT"):
        rate = 50
        tcp_percent = 20
        udp_sent = int(rate * (100 - tcp_percent) / 100.0 * seconds)
        tcp_sent = int(rate * tcp_percent / 100.0 * seconds)
        sent = udp_sent + tcp_sent
        answered = sent - injections.get("dns_unanswered", 0)
        port = DNS_PORTS[arm]
        parameters.update(
            {"ratePerSecond": rate, "tcpPercent": tcp_percent, "cnameEvery": 0, "dnsPort": port,
             "drainWindowMs": 1000}
        )
        latency["dns-rtt"] = histogram(rng, sent, 700.0, 1300.0)
        metrics.update(
            {
                "sent": sent,
                "udp.sent": udp_sent,
                "tcpSent": tcp_sent,
                "unsent": 0,
                "udpUnsent": 0,
                "tcpUnsent": 0,
                "offered": sent,
                "answered": answered,
                "servfail": 0,
                "timeout": injections.get("dns_unanswered", 0),
                "other": 0,
                "unanswered": injections.get("dns_unanswered", 0),
                "socketErrors": 0,
                "emptyAnswers": 70,
                "malformed": 0,
                "unmatched": 0,
                "answerRate": round(answered / sent, 6) if sent else None,
                "achievedRate": float(rate),
                "rcodes": {"0": answered},
                "queryTypes": {
                    "A": sent - 3 * (sent // 8),
                    "AAAA": sent // 8,
                    "HTTPS": sent // 8,
                    "TXT": sent // 8,
                },
            }
        )
        windows["dns"][port] = {"udp": udp_sent, "tcp": tcp_sent, "tcp_connections": 1 if tcp_sent else 0}
        windows["tcp_connections"] = 0
    elif arm == "LOSS":
        rate = 200
        sent = rate * seconds
        rate_loss = injections.get("loss_rate", 0.0)
        block = loss_block(
            rng,
            sent,
            rate_loss,
            window_ms,
            corrupt=injections.get("corrupt", 0),
            foreign=injections.get("foreign_connection", 0),
            identity_break=injections.get("identity_break", False),
            abandoned_at_teardown=injections.get("abandoned", 0),
        )
        block["supplied"] = sent
        block["receivedDatagrams"] = block["arrived"] + block["unmatchedReplies"]
        block["receivedBytes"] = block["receivedDatagrams"] * 200
        block["sendWouldBlock"] = 0
        block["sendFailures"] = 0
        block["windowOverflow"] = 0
        block["achievedRate"] = float(rate)
        metrics.update(block)
        parameters.update({"ratePerSecond": rate, "payloadBytes": 200, "lossWindowMs": window_ms})
        gates["clientSendLoss"] = block["clientSendLoss"]
        gates["windowMs"] = float(window_ms)
        windows["udp_echo"] = (sent, injections.get("udp_endpoint", CLIENT_ENDPOINT))
    elif arm == "REL":
        rate = 5
        attempts = rate * seconds
        scheduled = attempts + injections.get("scheduled_extra", 0)
        clean = int(attempts * 0.5)
        reset = int(attempts * 0.25)
        unexpected = attempts - clean - reset
        modes = ("clean", "resetAfterN", "partialFin", "halfClose")
        # The schedule splits the attempts evenly over its four modes; every mode observes what its
        # own exchange calls for, so the joint mode x observed block reads back the mode's own count.
        per_mode = {mode: attempts // len(modes) for mode in modes}
        per_mode[modes[0]] += attempts - sum(per_mode.values())
        echoed = attempts * 8192
        trailer = attempts * 64
        parameters.update(
            {"connectionsPerSecond": rate, "expectedBytes": 8192,
             "modeMix": "clean=25,resetAfterN=25,partialFin=25,halfClose=25", "framePayloadBytes": 1024}
        )
        latency["tcp-connect"] = histogram(rng, attempts, 550.0, 1400.0)
        attempts_written, attempts_omitted = attempt_records(
            records, attempts, modes, per_mode, echoed // max(1, attempts), trailer // max(1, attempts)
        )
        by_mode = {}
        for mode in modes:
            produced = per_mode[mode]
            observed = dict.fromkeys(
                ("clean", "reset", "unexpectedEof", "timeout", "connectFail", "halfCloseViolation", "otherError"), 0
            )
            observed["clean" if mode in ("clean", "halfClose") else "reset" if mode == "resetAfterN" else "unexpectedEof"] = produced
            by_mode[mode] = {
                "attempts": produced,
                "observed": observed,
                "truncated": 0,
                "echoedBytes": produced * 8192,
                "trailerBytes": produced * 64,
                "minEchoedBytes": 8192 if produced else None,
                "maxEchoedBytes": 8192 if produced else None,
                "minTrailerBytes": 64 if produced else None,
                "maxTrailerBytes": 64 if produced else None,
            }
        metrics.update(
            {
                "connectAttempts": attempts,
                "scheduledAttempts": scheduled,
                "outcomes": {"clean": clean, "reset": reset, "unexpectedEof": unexpected, "timeout": 0,
                             "connectFail": 0, "halfCloseViolation": 0, "otherError": 0},
                "expected": {"clean": clean, "reset": reset, "unexpectedEof": unexpected, "timeout": 0,
                             "connectFail": 0, "halfCloseViolation": 0, "otherError": 0},
                "unexpectedEof": 0,
                "expectedEarlyEof": unexpected,
                "truncated": 0,
                "fidelityMismatch": injections.get("fidelity_mismatch", 0),
                "fidelityRate": round(injections.get("fidelity_mismatch", 0) / attempts, 6),
                "connectFail": 0,
                "expectedBytes": 8192,
                "modeSchedule": "clean,resetAfterN,partialFin,halfClose",
                "echoedBytes": echoed,
                "trailerBytes": trailer,
                "byMode": by_mode,
                "attemptRecords": attempts_written,
                "attemptRecordsOmitted": attempts_omitted,
                "meanConnectMs": 0.52,
                "meanTransferMs": 0.84,
                "achievedRate": float(rate),
                "effectiveModeMix": "clean=25,resetAfterN=25,partialFin=25,halfClose=25",
            }
        )
        windows["tcp_connections"] = attempts
    elif arm == "THRU":
        streams = 4
        parameters.update({"streams": streams, "targetBytesPerSecond": 20_000_000, "framePayloadBytes": 32768})
        metrics.update(
            {
                "bytes": 160_000_000,
                "bytesSent": 160_100_000,
                "frames": 4800,
                "framesSent": 4885,
                "framesEchoed": 4800,
                "streamConnects": streams,
                "budgetBytes": 20_000_000 * seconds,
                "budgetReached": False,
                "budgetRemainingBytes": 160_100_000 % 32768,
                "elapsedSeconds": float(seconds),
                "goodputBps": 19_988_646.195,
                "goodputMbps": round(160.0 / seconds * 1.0, 4),
                "perStreamMinBytes": 39_000_000,
                "perStreamMaxBytes": 40_100_000,
                "corrupt": 0,
                "protocolErrors": 0,
                "targetBytesPerSecond": 20_000_000,
            }
        )
        windows["tcp_connections"] = streams
    elif arm == "MIX":
        desktops = 2
        pages = max(1, seconds // 20)
        page_connections = pages * 13 * desktops
        bulk_frames = 200 * desktops
        dns_sent = pages * 4 * desktops
        udp_sent = 30 * seconds * desktops
        identity_break = injections.get("identity_break", False)
        foreign = injections.get("foreign_connection", 0)
        idle_desktop = injections.get("idle_desktop")
        block = loss_block(rng, udp_sent, injections.get("loss_rate", 0.0), window_ms, foreign=foreign,
                           identity_break=identity_break, abandoned_at_teardown=injections.get("abandoned", 0))
        per_desktop = []
        per_desktop_sent = []
        for desktop in range(desktops):
            desktop_sent = udp_sent // desktops
            per_desktop_sent.append(desktop_sent)
            per_desktop.append(
                {
                    "desktop": desktop,
                    "udp.sent": desktop_sent,
                    "udp.arrived": desktop_sent - (block["late"] + block["never"]) // desktops,
                    "udpNever": block["never"] // desktops,
                    "udp.foreignConnection": foreign if desktop == 0 else 0,
                    "pageConnections": 0 if idle_desktop == desktop else pages * 13,
                    "bulkFrames": 0 if idle_desktop == desktop else bulk_frames // desktops,
                    "dnsSent": dns_sent // desktops if idle_desktop != desktop else 0,
                }
            )
        idle_lanes = 3 if idle_desktop is not None else 0
        parameters.update(
            {"desktops": desktops, "pageIntervalSeconds": 20, "pageConnections": 13, "pageRequestsTotal": 73,
             "pageMessageBytes": 38000, "bulkBitsPerSecondPerDesktop": 5_000_000, "dnsQueriesPerPage": 4,
             "udpPacketsPerSecondPerDesktop": 30, "udpPayloadBytes": 120, "lossWindowMs": window_ms}
        )
        latency["tcp-connect"] = histogram(rng, page_connections, 9000.0, 11000.0)
        latency["tcp-rtt"] = histogram(rng, pages * 73 * desktops, 600.0, 5000.0)
        latency["udp-rtt"] = histogram(rng, udp_sent, 620.0, 1000.0)
        latency["dns-rtt"] = histogram(rng, dns_sent, 1500.0, 5000.0)
        metrics.update(
            {
                "classes": {
                    "page": {"pages": pages, "connections": page_connections, "messages": page_connections * 5,
                             "bytes": page_connections * 38000, "errors": 0,
                             "bytesPerPage": page_connections * 38000 // max(1, pages)},
                    "bulk": {"bytes": 12_500_000, "bytesSent": 12_530_000, "frames": bulk_frames, "errors": 0,
                             "goodputBps": 1_249_575.372},
                    "dns": {"sent": dns_sent, "answered": dns_sent, "servfail": 0, "timeout": 0, "other": 0},
                    "udp": {
                        "sent": block["sent"],
                        "arrived": block["arrived"],
                        "late": block["late"],
                        "never": block["never"],
                        "corrupt": block["corrupt"],
                        "corruptDatagrams": block["corruptDatagrams"],
                        "duplicate": block["duplicate"],
                        "reordered": block["reordered"],
                        "unmatchedReplies": block["unmatchedReplies"],
                        "foreignConnection": block["foreignConnection"],
                        "abandonedAtTeardown": block["abandonedAtTeardown"],
                        "sendFailures": 0,
                        "windowOverflow": 0,
                        "outOfRangeSequences": block["outOfRangeSequences"],
                        "sentOutOfRangeSequences": block["sentOutOfRangeSequences"],
                        "bytes": udp_sent * 120,
                        "clientSendLoss": block["clientSendLoss"],
                        "lossRate": block["lossRate"],
                        "window": float(window_ms),
                        "sentPerDesktop": per_desktop_sent,
                    },
                },
                "pages": pages,
                "pageConnections": page_connections,
                "pageBytes": page_connections * 38000,
                "bulkBytes": 12_500_000,
                "dnsSent": dns_sent,
                "udp.sent": udp_sent,
                "udp.lossRate": block["lossRate"],
                "clientSendLoss": block["clientSendLoss"],
                "desktops": per_desktop,
            }
        )
        gates.update({"clientSendLoss": block["clientSendLoss"], "idleLanes": idle_lanes, "windowMs": float(window_ms)})
        windows["tcp_connections"] = page_connections + desktops
        windows["udp_echo"] = (udp_sent, injections.get("udp_endpoint", CLIENT_ENDPOINT))
        if injections.get("null_udp_class_rate"):
            metrics["classes"]["udp"]["lossRate"] = None
        if injections.get("drop_udp_class"):
            del metrics["classes"]["udp"]
    elif arm == "PERSIST":
        requests = 0 if injections.get("zero_requests") else max(2, seconds // 2)
        responses = 0 if injections.get("zero_requests") else requests - injections.get("persist_timeouts", 0)
        reconnects = injections.get("reconnects", 0)
        parameters.update({"intervalMs": 500, "idleSeconds": 3, "payloadBytes": 200, "expectedBytes": 0,
                           "responseTimeoutMs": 2000})
        latency["tcp-rtt"] = histogram(rng, responses, 520.0, 6800.0)
        metrics.update(
            {
                "requests": requests,
                "responses": responses,
                "reconnects": reconnects,
                "survivedIdle": injections.get("survived_idle", True),
                "idleSecondsScheduled": 3.0,
                "idleSecondsObserved": 3.0 + reconnects * 0.5,
                "sendWouldBlock": 0,
                "sendFailures": 0,
                "timeouts": injections.get("persist_timeouts", 0),
                "remoteClosed": reconnects,
                "protocolErrors": 0,
                "corrupt": 0,
                "unmatchedReplies": 0,
                "connectAttempts": 1 + reconnects,
                "connectFailures": injections.get("connect_failures", 0),
                "meanConnectMs": 3.4,
                "responseRate": round(responses / requests, 6) if requests else None,
                "achievedRate": round(requests / seconds, 6) if seconds else None,
                "completionRate": round(responses / seconds, 6) if seconds else None,
            }
        )
        gates["clientSendLoss"] = injections.get("connect_failures", 0) + 0
        windows["tcp_connections"] = 1 + reconnects
    elif arm == "BASE":
        phase = seconds // 2
        connections = phase
        latency["tcp-connect"] = histogram(rng, connections, 300.0, 1300.0)
        latency["tcp-rtt"] = histogram(rng, 20 * phase, 620.0, 1000.0)
        latency["udp-rtt"] = histogram(rng, 20 * phase, 640.0, 2400.0)
        base_loss_rate = injections.get("base_loss_rate", 0.0)
        block = loss_block(rng, 500 * phase, base_loss_rate, window_ms)
        block["supplied"] = block["sent"]
        block["receivedDatagrams"] = block["arrived"]
        block["receivedBytes"] = block["arrived"] * 200
        block["sendWouldBlock"] = 0
        block["sendFailures"] = 0
        block["windowOverflow"] = 0
        block["achievedRate"] = 480.0
        parameters.update(
            {
                "phaseSeconds": phase,
                "phases": ["latency", "loss"],
                "latency": {"seconds": phase, "ratePerSecond": 20, "payloadBytes": 120, "protocol": "tcp+udp",
                            "lanes": 1, "inFlightWindow": 64},
                "loss": {"seconds": phase, "ratePerSecond": 500, "payloadBytes": 200,
                         "lossWindowMs": window_ms},
            }
        )
        metrics.update(
            {
                "elapsedSeconds": float(seconds),
                "latency": {
                    "tcp.laneStarted": 1,
                    "tcp.laneSupplied": [20 * phase],
                    "tcp.laneSentOk": [20 * phase],
                    "tcp.supplied": 20 * phase,
                    "tcp.sent": 20 * phase,
                    "tcp.clientSendLoss": 0,
                    "tcp.received": 20 * phase,
                    "tcp.corrupt": 0,
                    "tcp.protocolErrors": 0,
                    "tcp.remoteClosed": 0,
                    "tcp.unmatchedReplies": 0,
                    "tcp.windowOverflow": 0,
                    "tcp.backlogDrops": 0,
                    "tcp.sendFailures": 0,
                    "tcp.sendWouldBlock": 0,
                    "tcp.abandonedAtTeardown": 0,
                    "tcp.outstandingAtTeardown": 0,
                    "tcp.windowCeilingMs": round(1000.0 * 64 / 20.0, 3),
                    "tcp.scheduleTruncated": 0,
                    "tcp.achievedRate": 20.0,
                    "tcp.connectAttempts": connections,
                    "tcp.connectFailures": 0,
                    "tcp.meanConnectMs": 0.24,
                    "udp.laneStarted": 1,
                    "udp.supplied": 20 * phase,
                    "udp.sent": 20 * phase,
                    "udp.clientSendLoss": 0,
                    "udp.received": 20 * phase,
                    "udp.corrupt": 0,
                    "udp.protocolErrors": 0,
                    "udp.unmatchedReplies": 0,
                    "udp.foreignConnection": injections.get("foreign_connection", 0),
                    "udp.windowOverflow": 0,
                    "udp.backlogDrops": 0,
                    "udp.sendFailures": 0,
                    "udp.sendWouldBlock": 0,
                    "udp.abandonedAtTeardown": 0,
                    "udp.outstandingAtTeardown": 0,
                    "udp.windowCeilingMs": round(1000.0 * 64 / 20.0, 3),
                    "udp.scheduleTruncated": 0,
                    "udp.lossRate": 0.0,
                    "udp.achievedRate": 20.0,
                },
                "loss": block,
            }
        )
        gates.update(
            {
                "clientSendLoss": 0,
                "windowOverflow": 0,
                "backlogDrops": 0,
                "sendFailures": 0,
                "laneShortfall": 0,
                "scheduleTruncated": 0,
                "inFlightCeilingMs": round(1000.0 * 64 / 20.0, 3),
                "windowMs": float(window_ms),
            }
        )
        windows["tcp_connections"] = connections
        windows["udp_echo"] = (20 * phase + block["sent"], injections.get("udp_endpoint", CLIENT_ENDPOINT))
    else:
        raise SystemExit("unknown arm %s" % arm)

    notes = [
        "synthetic record generated by make_tree.py; field names and shapes follow WinForward.E2E",
    ]
    notes.extend(notes_extra)
    result = {
        "type": "result",
        "arm": arm,
        "kind": KIND_OF_ARM[arm],
        "label": None,
        "parameters": parameters,
        "metrics": metrics,
        "latency": latency,
        "gates": gates,
        "notes": notes,
    }
    return records, result, windows


def tcp_summary_verdicts(observed):
    """The eight verdict counters, in the order `tcpSummary.verdicts` declares them."""
    return {name: observed.get(name, 0) for name in TCP_VERDICT_NAMES}


def dns_summary_block(port, totals):
    """One DNS listener's counters, the shape both `dnsSummary` and `targetSummary.dns` write."""
    totals = totals or {"udp": 0, "tcp": 0, "tcpConnections": 0}
    return {
        "port": port,
        "udpQueries": totals["udp"],
        "udpAnswers": totals["udp"],
        "udpEmptyAnswers": 0,
        "udpMalformed": 0,
        "udpSendErrors": 0,
        "tcpQueries": totals["tcp"],
        "tcpAnswers": totals["tcp"],
        "tcpEmptyAnswers": 0,
        "tcpMalformed": 0,
        "truncatedFrames": 0,
        "tcpConnections": totals["tcpConnections"],
        "acceptErrors": 0,
        "tcpAborted": 0,
    }


def stamp_truncated(records, tcp_frames, dns_frames):
    """Move the two truncation counters of the main ledger off zero, where they are published.

    Both are running totals for the whole target, so the closing `tcpSummary`/`dnsSummary` and the
    `targetSummary` block repeat them. The cut-off TCP connections carry the target's existing
    `protocolError` verdict rather than a verdict of their own (D19.3 D).
    """
    for record in reversed(records):
        if record.get("type") == "tcpSummary":
            record["truncatedFrames"] = tcp_frames
            record["protocolErrors"] = tcp_frames
            record["verdicts"]["protocolError"] = tcp_frames
            break
    for record in reversed(records):
        if record.get("type") == "dnsSummary":
            record["truncatedFrames"] = dns_frames
            break
    for record in reversed(records):
        if record.get("type") == "targetSummary":
            record["truncatedFrames"] = tcp_frames
            record["tcp"]["truncatedFrames"] = tcp_frames
            record["tcp"]["protocolErrors"] = tcp_frames
            record["tcp"]["verdicts"]["protocolError"] = tcp_frames
            record["dns"]["truncatedFrames"] = dns_frames
            record["dnsAlt"]["truncatedFrames"] = dns_frames
            break


def ledger_error_record(stamp, label, detail):
    """The target's own failure record: the family is the record type, the cause is `detail`."""
    return {"utc": stamp, "label": label, "type": "error", "detail": detail}


USAGE = (
    "usage: make_tree.py [--window-overflow ROW] [--zero-denominator] [--undecodable N] "
    "[--truncated-tcp N] [--truncated-dns N] [<output-raw-dir>]"
)

COUNTER_FLAGS = ("--undecodable", "--truncated-tcp", "--truncated-dns")


def parse_arguments(argv):
    out = None
    window_overflow_row = None
    zero_denominator = False
    counters = dict.fromkeys(COUNTER_FLAGS, 0)
    remaining = list(argv)
    while remaining:
        argument = remaining.pop(0)
        if argument == "--zero-denominator":
            zero_denominator = True
        elif argument == "--window-overflow" or argument in COUNTER_FLAGS:
            if not remaining:
                raise SystemExit(USAGE)
            value = remaining.pop(0)
            if argument == "--window-overflow":
                window_overflow_row = value
                continue
            try:
                counters[argument] = int(value)
            except ValueError:
                raise SystemExit(USAGE) from None
            if counters[argument] < 0:
                raise SystemExit(USAGE)
        elif argument.startswith("--") or out is not None:
            raise SystemExit(USAGE)
        else:
            out = Path(argument)
    return (
        (out if out is not None else Path("/tmp/wf-synth/raw")),
        window_overflow_row,
        zero_denominator,
        counters["--undecodable"],
        counters["--truncated-tcp"],
        counters["--truncated-dns"],
    )


def stamp_undecodable(records, total):
    for record in reversed(records):
        if record.get("type") == "udpSummary":
            record["undecodable"] = total
            break
    for record in reversed(records):
        if record.get("type") == "targetSummary":
            record["udp"]["undecodable"] = total
            break


def plan_document(pass_index):
    """One plan file, the shape `PlanFile` accepts for the rows of one pass."""
    arms = [
        {"name": arm, "kind": KIND_OF_ARM[arm], "seconds": ARM_SECONDS[arm]}
        for arm in FULL_ARMS
    ]
    return {
        "label": "synthetic-full-plan-%d" % pass_index,
        "arms": arms,
        "environment": {"campaign": "synthetic-multi-pass", "pass": pass_index},
    }


def main():
    (out, window_overflow_row, zero_denominator, undecodable_total,
     truncated_tcp, truncated_dns) = parse_arguments(sys.argv[1:])
    if out.exists():
        shutil.rmtree(out)
    out.mkdir(parents=True)
    builder = Builder(out)
    builder.write_json(
        out / "environment.json",
        {"campaign": "synthetic-multi-pass", "host": "analysis-host", "note": "generated by make_tree.py"},
    )
    rng = random.Random(20261006)
    campaign_ledgers = []

    for pass_index, pass_id in enumerate(("pass1", "pass2", "pass3"), start=1):
        pass_dir = out / pass_id
        pass_dir.mkdir(parents=True)
        builder.write_json(out.parent / ("plan-%d.json" % pass_index), plan_document(pass_index))
        auto_label = AUTO_LABELS[pass_id]
        pass_start_utc = T0 + timedelta(seconds=(pass_index - 1) * PASS_SECONDS)
        builder.utc = pass_start_utc
        builder.tick = 0
        ledger_records = []
        ledger_udp_cumulative = 0
        order = []
        dns_totals = {}

        for row_id, plan, dual, product_process, udp_endpoint, base_p50 in ROWS:
            row_dir = pass_dir / row_id
            row_dir.mkdir(parents=True)
            order.append(row_id)
            label = "%s-p%d" % (row_id, pass_index) if auto_label else pass_id
            run_start_tick = builder.tick
            run_start_utc = builder.utc
            arms = PLAN_ARMS[plan]
            arm_entries = []

            injections = {}
            if row_id == "proxifyre" and pass_index == 2:
                injections = {"foreign_connection": 3, "loss_rate": 0.004}
            if row_id == "proxybridge" and pass_index == 1:
                injections = {"identity_break": True}
            if row_id == "proxybridge" and pass_index == 3:
                injections = {"corrupt": 2, "loss_rate": 0.002}
            if row_id == "proxifyre" and pass_index == 3:
                injections = {"abandoned": 2}
            if row_id == "wf-aot-opt" and pass_index == 2:
                injections = {"loss_rate": 0.0005}
            if row_id == "wf-aot-opt" and pass_index == 3:
                injections = {"scheduled_extra": 2}
            if row_id == "proxifier" and pass_index == 1:
                injections = {"idle_desktop": 1, "survived_idle": False, "reconnects": 1, "persist_timeouts": 1}
            if row_id == "wf-fdd-opt" and pass_index == 2:
                injections = {"loss_rate": 0.001}
            if row_id.startswith("control"):
                injections = {"base_loss_rate": 0.0 if row_id == "control-pre" else 0.008}

            for arm in arms:
                arm_injections = dict(injections)
                if arm == "LAT" and row_id == "wf-aot-opt" and pass_index == 2:
                    arm_injections["read_error_at"] = 3
                if arm == "REL" and row_id == "wf-aot-opt" and pass_index == 2:
                    arm_injections["restart_at"] = 15
                if window_overflow_row is not None and row_id == window_overflow_row and arm == "LAT" and pass_index == 3:
                    arm_injections["window_overflow"] = 3
                if zero_denominator:
                    if row_id == "wf-aot-opt" and arm == "LAT" and pass_index == 1:
                        arm_injections["zero_udp_sent"] = True
                    if row_id == "wf-fdd-opt" and arm == "PERSIST":
                        arm_injections["zero_requests"] = True
                    if row_id == "wf-aot-opt" and arm == "MIX" and pass_index == 1:
                        arm_injections["null_udp_class_rate"] = True
                    if row_id == "wf-aot-opt" and arm == "MIX" and pass_index == 2:
                        arm_injections["drop_udp_class"] = True
                if arm in ("LAT", "LATLOAD", "LOSS", "MIX", "BASE"):
                    if row_id in ("wf-aot-opt", "wf-fdd-opt", "wf-aot-dnsrelay", "proxifyre", "proxybridge"):
                        arm_injections.setdefault("udp_endpoint", PROXY_ENDPOINT)
                    else:
                        arm_injections.setdefault("udp_endpoint", udp_endpoint or CLIENT_ENDPOINT)
                arm_notes = []
                if arm == "PERSIST" and row_id == "proxifier" and pass_index == 1:
                    arm_notes.append("survivedIdle false after the idle gap")
                records, result, windows = build_arm(
                    builder,
                    arm,
                    row_id,
                    plan,
                    pass_index,
                    rng,
                    product_process,
                    notes_extra=arm_notes,
                    injections=arm_injections,
                )
                result["label"] = label
                if arm == "LAT" and row_id == "wf-aot-opt" and pass_index == 2:
                    records.append(
                        {
                            "type": "samplerError",
                            "ticks": records[0]["ticks"] + 5 * FREQ,
                            "arm": "LAT",
                            "process": product_process,
                            "error": "Win32Exception",
                            "message": "Access is denied (synthetic)",
                        }
                    )
                arm_start_tick = builder.tick - int(ARM_SECONDS[arm] * FREQ)
                arm_end_tick = builder.tick
                arm_entries.append(
                    {
                        "name": arm,
                        "kind": result["kind"],
                        "file": "%s.jsonl" % arm,
                        "startedTicks": arm_start_tick,
                        "endedTicks": arm_end_tick,
                        "failed": False,
                    }
                )
                summary = {
                    "type": "armSummary",
                    "arm": arm,
                    "kind": result["kind"],
                    "label": label,
                    "parameters": result["parameters"],
                    "gates": result["gates"],
                    "resultFile": "%s.jsonl" % arm,
                    "startedTicks": arm_start_tick,
                    "endedTicks": arm_end_tick,
                }
                result["startedTicks"] = arm_start_tick
                result["endedTicks"] = arm_end_tick
                builder.write_jsonl(row_dir / ("%s.jsonl" % arm), records + [result, summary])

                arm_utc_start = run_start_utc + timedelta(seconds=(arm_start_tick - run_start_tick) / FREQ)
                arm_utc_end = run_start_utc + timedelta(seconds=(arm_end_tick - run_start_tick) / FREQ)
                if windows.get("tcp_connections"):
                    verdict_cycle = ["clean", "reset", "partialFin", "halfClose"]
                    for index in range(int(windows["tcp_connections"])):
                        ledger_records.append(
                            {
                                "utc": iso(arm_utc_start + timedelta(seconds=0.5 * index / max(1, windows["tcp_connections"] / ARM_SECONDS[arm]))),
                                "label": label,
                                "_direct": False,
                                "type": "tcp",
                                "connectionId": 0x5000_0000 + index,
                                "mode": verdict_cycle[index % 4],
                                "expectedBytes": 8192,
                                "bytesEchoed": 8192,
                                "verdict": verdict_cycle[index % 4],
                                "peer": "192.168.77.2:41000",
                                "startedTicks": arm_start_tick,
                                "endedTicks": arm_end_tick,
                            }
                        )
                if windows.get("udp_echo"):
                    sent, endpoint = windows["udp_echo"]
                    per_second = max(1, sent // max(1, ARM_SECONDS[arm]))
                    emitted = 0
                    for second in range(0, ARM_SECONDS[arm], 2):
                        chunk = min(per_second * 2, sent - emitted)
                        if chunk <= 0:
                            break
                        emitted += chunk
                        ledger_udp_cumulative += chunk
                        ledger_records.append(
                            {
                                "utc": iso(arm_utc_start + timedelta(seconds=second + 0.5)),
                                "label": label,
                                "type": "udpSummary",
                                "received": ledger_udp_cumulative,
                                "undecodable": 0,
                                "bytes": ledger_udp_cumulative * 200,
                                "ticks": int((second + 1) * FREQ),
                                "sources": [{"address": endpoint.split(":")[0], "port": int(endpoint.split(":")[1]),
                                             "datagrams": chunk}],
                                "sourceOverflow": 0,
                            }
                        )
                for port, totals in (windows.get("dns") or {}).items():
                    entry = dns_totals.setdefault(port, {"udp": 0, "tcp": 0, "tcpConnections": 0})
                    entry["udp"] += totals["udp"]
                    entry["tcp"] += totals["tcp"]
                    entry["tcpConnections"] += totals["tcp_connections"]
                if arm == "MIX":
                    mix_dns = result["metrics"]["classes"]["dns"]["sent"]
                    entry = dns_totals.setdefault(
                        DNS_PORTS["DNS"], {"udp": 0, "tcp": 0, "tcpConnections": 0}
                    )
                    entry["udp"] += mix_dns

            if dual:
                pass_level = row_id == "proxybridge" and auto_label
                dual_dir = (pass_dir / "dual") if pass_level else (row_dir / "dual")
                dual_dir.mkdir(parents=True, exist_ok=True)
                direct_endpoint = DIRECT_ENDPOINT
                leak = 0
                if row_id == "wf-fdd-opt" and pass_index == 2:
                    leak = 2
                    direct_endpoint = PROXY_ENDPOINT
                lane_truth = {}
                for lane, lane_plan, lane_endpoint in (
                    ("proxied", ["LAT", "LOSS"], PROXY_ENDPOINT),
                    ("direct", ["LAT", "LOSS"], direct_endpoint),
                ):
                    lane_latency_scale = 1.0
                    if lane == "direct":
                        lane_latency_scale = 1.16 if row_id == "proxifier" else 0.99 + 0.005 * pass_index
                    lane_dir = dual_dir / lane
                    lane_dir.mkdir()
                    lane_label = "%s-dual-%s" % (row_id, lane)
                    lane_start_tick = builder.tick
                    lane_start_utc = builder.utc
                    lane_entries = []
                    lane_truth[lane] = {"tcp": 0, "udp": 0}
                    for arm in lane_plan:
                        arm_injections = {
                            "udp_endpoint": lane_endpoint,
                            "loss_rate": (0.012 if row_id == "proxifier" and lane == "direct" else 0.0),
                            "latency_scale": lane_latency_scale,
                        }
                        records, result, windows = build_arm(
                            builder, arm, row_id, "full", pass_index, rng, product_process,
                            injections=arm_injections,
                        )
                        result["label"] = lane_label
                        arm_start_tick = builder.tick - int(ARM_SECONDS[arm] * FREQ)
                        arm_end_tick = builder.tick
                        result["startedTicks"] = arm_start_tick
                        result["endedTicks"] = arm_end_tick
                        summary = {
                            "type": "armSummary",
                            "arm": arm,
                            "kind": result["kind"],
                            "label": lane_label,
                            "parameters": result["parameters"],
                            "gates": result["gates"],
                            "resultFile": "%s.jsonl" % arm,
                            "startedTicks": arm_start_tick,
                            "endedTicks": arm_end_tick,
                        }
                        builder.write_jsonl(lane_dir / ("%s.jsonl" % arm), records + [result, summary])
                        lane_entries.append(
                            {"name": arm, "kind": result["kind"], "file": "%s.jsonl" % arm,
                             "startedTicks": arm_start_tick, "endedTicks": arm_end_tick, "failed": False}
                        )
                        if windows.get("tcp_connections"):
                            lane_truth[lane]["tcp"] += int(windows["tcp_connections"])
                        arm_utc_start = lane_start_utc + timedelta(seconds=(arm_start_tick - lane_start_tick) / FREQ)
                        arm_utc_end = lane_start_utc + timedelta(seconds=(arm_end_tick - lane_start_tick) / FREQ)
                        if windows.get("udp_echo"):
                            sent, endpoint = windows["udp_echo"]
                            lane_truth[lane]["udp"] += int(sent)
                            per_second = max(1, sent // ARM_SECONDS[arm])
                            emitted = 0
                            for second in range(0, ARM_SECONDS[arm], 2):
                                chunk = min(per_second * 2, sent - emitted)
                                if chunk <= 0:
                                    break
                                emitted += chunk
                                ledger_udp_cumulative += chunk
                                ledger_records.append(
                                    {
                                        "utc": iso(arm_utc_start + timedelta(seconds=second + 0.5)),
                                        "label": lane_label,
                                        "_direct": lane == "direct",
                                        "type": "udpSummary",
                                        "received": ledger_udp_cumulative,
                                        "undecodable": 0,
                                        "bytes": ledger_udp_cumulative * 200,
                                        "ticks": int((second + 1) * FREQ),
                                        "sources": [
                                            {"address": endpoint.split(":")[0], "port": int(endpoint.split(":")[1]),
                                             "datagrams": chunk}
                                        ],
                                        "sourceOverflow": 0,
                                    }
                                )
                        if windows.get("tcp_connections"):
                            verdict_cycle = ["clean", "reset", "partialFin", "halfClose"]
                            for index in range(int(windows["tcp_connections"])):
                                ledger_records.append(
                                    {
                                        "utc": iso(arm_utc_start + timedelta(seconds=0.5 * index / max(1, windows["tcp_connections"] / 30))),
                                        "label": lane_label,
                                        "_direct": lane == "direct",
                                        "type": "tcp",
                                        "connectionId": 0x7000_0000 + index,
                                        "mode": verdict_cycle[index % 4],
                                        "expectedBytes": 8192,
                                        "bytesEchoed": 8192,
                                        "verdict": verdict_cycle[index % 4],
                                        "peer": "192.168.77.2:41000",
                                        "startedTicks": arm_start_tick,
                                        "endedTicks": arm_end_tick,
                                    }
                                )
                    lane_end_tick = builder.tick
                    builder.write_json(
                        lane_dir / "run.json",
                        {
                            "type": "run",
                            "label": lane_label,
                            "clientVersion": "1.0.0.0",
                            "osDescription": "Windows 11 IoT LTSC (synthetic)",
                            "frameworkDescription": ".NET 10.0.12",
                            "logicalProcessors": 8,
                            "planHash": "synth%s%s" % (pass_index, lane),
                            "planPath": None,
                            "planSource": "builtin",
                            "outDirectory": str(lane_dir),
                            "target": {
                                "address": TARGET_ADDRESS,
                                "tcpPort": DIRECT_TCP_PORT if lane == "direct" else PROXIED_TCP_PORT,
                                "udpPort": DIRECT_TCP_PORT if lane == "direct" else PROXIED_TCP_PORT,
                                "dnsPort": DIRECT_DNS_PORT if lane == "direct" else PROXIED_DNS_PORT,
                            },
                            "samplerProcesses": [product_process] if product_process else [],
                            "startedUtc": iso(lane_start_utc),
                            "endedUtc": iso(lane_start_utc + timedelta(seconds=(lane_end_tick - lane_start_tick) / FREQ)),
                            "startedTicks": lane_start_tick,
                            "endedTicks": lane_end_tick,
                            "wallSeconds": round((lane_end_tick - lane_start_tick) / FREQ, 3),
                            "arms": lane_entries,
                            "failed": False,
                        },
                    )
                    (lane_dir / "config.synthetic").write_text(
                        "# lane %s of %s/%s\n" % (lane, pass_id, row_id), encoding="utf-8"
                    )
                builder.write_json(
                    dual_dir / "proxy-truth.json",
                    {
                        "tcp": lane_truth["proxied"]["tcp"] + lane_truth["direct"]["tcp"],
                        "udp": lane_truth["proxied"]["udp"] + lane_truth["direct"]["udp"],
                        "utcp": 0,
                        "total": lane_truth["proxied"]["tcp"] + lane_truth["direct"]["tcp"],
                        "directLeak": leak,
                    },
                )

            run_end_tick = builder.tick
            run_label = label
            carriage = UDP_CARRIAGE.get(row_id)
            udp_arms = sum(1 for arm in arms if arm in ("LAT", "LATLOAD", "LOSS", "MIX"))
            if "DNSALT" in arms:
                udp_arms += 1
            if "DNS" in arms and row_id in ("wf-aot-dnsrelay", "proxybridge"):
                udp_arms += 1
            row_truth = {
                "tcp": sum(
                    1
                    for arm in arms
                    if arm in ("LAT", "LATLOAD", "REL", "PERSIST", "THRU", "MIX")
                ),
                "udp": 0,
                "utcp": 0,
                "total": 0,
            }
            row_truth["tcp"] = (
                (2 + ARM_SECONDS["LAT"] if "LAT" in arms else 0)
                + (1 + ARM_SECONDS["LATLOAD"] if "LATLOAD" in arms else 0)
                + (5 * ARM_SECONDS["REL"] if "REL" in arms else 0)
                + (1 if "PERSIST" in arms else 0)
                + (4 if "THRU" in arms else 0)
                + (1 if "DNS" in arms else 0)
                + ((max(1, ARM_SECONDS["MIX"] // 20) * 13 * 2 + 2) if "MIX" in arms else 0)
            )
            if carriage == "utcp":
                row_truth["utcp"] = udp_arms
            elif carriage == "native":
                row_truth["udp"] = udp_arms
            row_truth["total"] = row_truth["tcp"] + row_truth["udp"] + row_truth["utcp"]
            builder.write_json(
                row_dir / "run.json",
                {
                    "type": "run",
                    "label": run_label,
                    "clientVersion": "1.0.0.0",
                    "osDescription": "Windows 11 IoT LTSC (synthetic)",
                    "frameworkDescription": ".NET 10.0.12",
                    "logicalProcessors": 8,
                    "planHash": "synth%s" % pass_index,
                    "planPath": "/tmp/wf-synth/plan-%s.json" % pass_index,
                    "planSource": "file",
                    "outDirectory": str(row_dir),
                    "target": {"address": TARGET_ADDRESS, "tcpPort": PROXIED_TCP_PORT,
                               "udpPort": PROXIED_TCP_PORT, "dnsPort": PROXIED_DNS_PORT},
                    "samplerProcesses": [product_process] if product_process else [],
                    "startedUtc": iso(run_start_utc),
                    "endedUtc": iso(builder.utc),
                    "startedTicks": run_start_tick,
                    "endedTicks": run_end_tick,
                    "wallSeconds": round((run_end_tick - run_start_tick) / FREQ, 3),
                    "arms": arm_entries,
                    "failed": False,
                },
            )
            (row_dir / "config.synthetic").write_text("# %s %s\n" % (row_id, plan), encoding="utf-8")
            builder.write_json(row_dir / "proxy-truth.json", row_truth)

        (pass_dir / "order.txt").write_text(", ".join(order) + "\n", encoding="utf-8")
        for port, totals in sorted(dns_totals.items()):
            ledger_records.append(
                {"utc": iso(builder.utc), "label": "%s-target" % pass_id if auto_label else pass_id,
                 "type": "dnsSummary", **dns_summary_block(port, totals or {"udp": 0, "tcp": 0, "tcpConnections": 0})}
            )
        tcp_records = [record for record in ledger_records if record["type"] == "tcp"]
        verdicts = {}
        for record in tcp_records:
            verdicts[record["verdict"]] = verdicts.get(record["verdict"], 0) + 1
        ledger_records.append(
            {
                "utc": iso(builder.utc),
                "label": "%s-target" % pass_id if auto_label else pass_id,
                "type": "tcpSummary",
                "connections": len(tcp_records),
                "acceptErrors": 0,
                "bytesEchoed": sum(record["bytesEchoed"] for record in tcp_records),
                "protocolErrors": 0,
                "truncatedFrames": 0,
                "verdicts": tcp_summary_verdicts(verdicts),
            }
        )
        ledger_records.append(
            {
                "utc": iso(builder.utc),
                "label": "%s-target" % pass_id if auto_label else pass_id,
                "type": "targetSummary",
                "startedTicks": 0,
                "endedTicks": builder.tick,
                "ledgerWriteErrors": 0,
                "tcp": {
                    "connections": len(tcp_records),
                    "acceptErrors": 0,
                    "bytesEchoed": 0,
                    "protocolErrors": 0,
                    "truncatedFrames": 0,
                    "verdicts": tcp_summary_verdicts(verdicts),
                },
                "udp": {"udpReceivers": UDP_RECEIVERS, "received": ledger_udp_cumulative, "undecodable": 0,
                        "bytes": ledger_udp_cumulative * 200, "sendErrors": 0},
                "dns": dns_summary_block(DNS_PORTS["DNS"], dns_totals.get(DNS_PORTS["DNS"])),
                "dnsAlt": dns_summary_block(DNS_PORTS["DNSALT"], dns_totals.get(DNS_PORTS["DNSALT"])),
                "acceptErrors": 0,
                "truncatedFrames": 0,
            }
        )
        ledger_records.sort(key=lambda record: record["utc"])
        ledger_records.append(
            ledger_error_record(
                iso(builder.utc + timedelta(seconds=1)),
                "%s-target" % pass_id if auto_label else pass_id,
                "SocketException",
            )
        )
        campaign_ledgers.append((pass_index, ledger_records))

    main_records = []
    direct_records = []
    for _, records in campaign_ledgers:
        for record in records:
            (direct_records if record.get("_direct") else main_records).append(record)
    if undecodable_total:
        stamp_undecodable(main_records, undecodable_total)
    if truncated_tcp or truncated_dns:
        stamp_truncated(main_records, truncated_tcp, truncated_dns)
    for path, records in ((out.parent / "ledger-main.jsonl", main_records), (out.parent / "ledger-direct.jsonl", direct_records)):
        with path.open("w", encoding="utf-8") as handle:
            for record in records:
                record.pop("_direct", None)
                handle.write(json.dumps(record, separators=(",", ":")) + "\n")
    print("wrote synthetic tree to %s (ledgers in %s)" % (out, out.parent))


if __name__ == "__main__":
    main()
