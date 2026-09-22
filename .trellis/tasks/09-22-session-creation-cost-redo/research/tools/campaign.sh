#!/usr/bin/env bash
# Step 4 campaign of task 09-22-session-creation-cost-redo.
# Run from the repo root, one process at a time; every run records before/after loadavg.
# The framework-ladder A/B batch (in x3, external x3) is launched separately.
set -u

RAW=.trellis/tasks/09-22-session-creation-cost-redo/research/raw

note() { printf '=== %s %s loadavg: %s\n' "$1" "$(date -Is)" "$(cat /proc/loadavg)"; }

run_bench() { # $1 = log stem, rest = benchmark CLI args
    local stem="$1"
    shift
    note before >"$RAW/$stem.load"
    dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- "$@" >"$RAW/$stem.log" 2>&1
    note "after exit=$?" >>"$RAW/$stem.load"
}

run_external_bench() { # $1 = log stem, rest = benchmark CLI args
    local stem="$1"
    shift
    note before >"$RAW/$stem.load"
    WINFORWARD_BENCH_EXTERNAL_SERVER=1 dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- "$@" >"$RAW/$stem.log" 2>&1
    note "after exit=$?" >>"$RAW/$stem.load"
}

run_stability() { # $1 = log stem, rest = stability CLI args
    local stem="$1"
    shift
    note before >"$RAW/$stem.load"
    dotnet run -c Release --project benchmarks/WinForward.Benchmarks -- --stability "$@" >"$RAW/$stem.log" 2>&1
    note "after exit=$?" >>"$RAW/$stem.load"
}

# --- probe: real transport, out-of-process (3 runs) -------------------------------
for r in 1 2 3; do
    run_external_bench "udpsession-ext-run$r" --filter '*UdpSession*' --job short
done

# --- probe: in-process (Noop cross-check + recorded-shape A/B, 3 runs) ------------
for r in 1 2 3; do
    run_bench "udpsession-in-run$r" --filter '*UdpSession*' --job short
done

# --- stage decomposition (clean instruments, R4 cross-check, 3 runs) --------------
for r in 1 2 3; do
    run_bench "stages-run$r" --filter '*SessionSetupDecomposition*' --job short
done

# --- churn wave matrix, out-of-process: N x D, 3 runs per cell ---------------------
for n in 48 128 256; do
    for d in 0 5 20; do
        for r in 1 2 3; do
            run_stability "udpchurn-run$r-N$n-D$d" \
                --scenario udpchurn --burst-flows "$n" --dial-delay-ms "$d" --churn-waves 1 \
                --socks5-external --output "$RAW/udpchurn-run$r-N$n-D$d.jsonl"
        done
    done
done

# --- churn sustained, out-of-process: D in {0,5}, 3 runs each ---------------------
for d in 0 5; do
    for r in 1 2 3; do
        run_stability "udpchurn-sustained-D$d-run$r" \
            --scenario udpchurn --burst-flows 48 --dial-delay-ms "$d" --churn-waves 0 --duration 30 \
            --socks5-external --output "$RAW/udpchurn-sustained-D$d-run$r.jsonl"
    done
done

# --- churn in-process sanity cell (recorded shape must still reproduce ~91 KB) ----
run_stability "udpchurn-incheck-N48-D0" \
    --scenario udpchurn --burst-flows 48 --dial-delay-ms 0 --churn-waves 1 \
    --output "$RAW/udpchurn-incheck-N48-D0.jsonl"

# --- retained footprint (fake transports, unchanged path) -------------------------
for r in 1 2 3; do
    run_stability "footprint-run$r" --scenario footprint --output "$RAW/footprint-run$r.jsonl"
done

echo "CAMPAIGN-DONE"
