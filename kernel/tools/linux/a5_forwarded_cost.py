
#!/usr/bin/env python3
"""A5 cost baseline: forwarded TCP/UDP vs direct host path, same size/pps.

Not a pass/fail gate.  CPU is the wfprobe/suite process CPU on the Windows
side for the forwarded path (includes the C# relay), and wall time for both
paths.  Environment: 1GbE vSwitch, single run, no isolation; numbers are
order-of-magnitude only.
"""
import argparse
import json
import pathlib
import re
import resource
import subprocess
import sys
import time

from forwarded_gate import GateError, Remote, ROOT, read_remote, write_suite_script


def cpu_of(remote, cfg):
    out = remote.poll("(Get-Process wfprobe -ErrorAction SilentlyContinue | Measure-Object -Property CPU -Sum).Sum", 30).stdout
    match = re.search(r"([0-9.]+)", out)
    return float(match.group(1)) if match else 0.0


def run_suite(remote, cfg, name, args, client_cmd, timeout):
    script = cfg.log_dir / f"{name}.ps1"
    remote_script = f"{cfg.remote_root}/{name}.ps1"
    remote_out = f"{cfg.remote_root}/{name}.out"
    write_suite_script(script, cfg.helper, cfg.suite_exe, remote_out, args)
    remote.upload(script, remote_script)
    start = subprocess.Popen(f"{remote.run_cmd} {chr(39)}pwsh -NoProfile -File {remote_script}{chr(39)}",
                             shell=True, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    deadline = time.time() + 60
    output = ""
    while time.time() < deadline:
        output = read_remote(remote, remote_out, 30)
        if "READY" in output:
            break
        time.sleep(0.7)
    if "READY" not in output:
        start.terminate()
        raise GateError(f"{name}: no READY\n{output[-1000:]}")
    cpu0 = resource.getrusage(resource.RUSAGE_CHILDREN).ru_utime + resource.getrusage(resource.RUSAGE_CHILDREN).ru_stime
    began = time.perf_counter()
    client = subprocess.run(client_cmd, shell=True, text=True, capture_output=True, timeout=timeout)
    elapsed = time.perf_counter() - began
    start.communicate(timeout=timeout)
    final = read_remote(remote, remote_out, 30)
    cpu1 = resource.getrusage(resource.RUSAGE_CHILDREN).ru_utime + resource.getrusage(resource.RUSAGE_CHILDREN).ru_stime
    (cfg.log_dir / f"{name}.log").write_text(
        f"$ {client_cmd}\n{client.stdout}\n{client.stderr}\n--- suite ---\n{final}\n", encoding="utf-8")
    if "ASSERT FAIL" in final:
        raise GateError(f"{name}: suite failed\n{final[-2000:]}")
    return elapsed, max(0.0, cpu1 - cpu0), client.stdout


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--run-cmd", default="windows-vm fast run")
    parser.add_argument("--poll-cmd", default="windows-vm b6ctl run")
    parser.add_argument("--up-cmd", default="windows-vm up2 up")
    parser.add_argument("--remote-root", default="C:/workspace")
    parser.add_argument("--suite-exe", default="C:/workspace/WinForward.Kernel/tools/wfprobe/bin/Release/net10.0/wfprobe.exe")
    parser.add_argument("--helper", default="C:/workspace/forwarded-gate-suite.ps1")
    parser.add_argument("--prepare", default="C:/workspace/prepare-b6c-forwarded.ps1")
    parser.add_argument("--target", default="192.168.100.99")
    parser.add_argument("--linux-ip", default="192.168.100.4")
    parser.add_argument("--vm-ip", default="192.168.100.2")
    parser.add_argument("--remote-port", type=int, default=40250)
    parser.add_argument("--listener-port", type=int, default=40400)
    parser.add_argument("--verify-port", type=int, default=41400)
    parser.add_argument("--tcp-bytes", type=int, default=16 * 1024 * 1024)
    parser.add_argument("--udp-count", type=int, default=1000)
    parser.add_argument("--udp-interval", type=float, default=0.005)
    parser.add_argument("--log-dir", default=str(ROOT / "verification" / "a5-cost"))
    args = parser.parse_args()
    cfg = type("Cfg", (), {})()
    cfg.__dict__.update(vars(args))
    cfg.log_dir = pathlib.Path(args.log_dir)
    cfg.log_dir.mkdir(parents=True, exist_ok=True)
    remote = Remote(args.run_cmd, args.poll_cmd, args.up_cmd)
    ready = False
    try:
        remote.upload(ROOT / "tools/windows/prepare-b6c-forwarded.ps1", args.prepare)
        remote.upload(ROOT / "tools/windows/forwarded-gate-suite.ps1", args.helper)
        prep = remote.run(f"pwsh -NoProfile -File {args.prepare} -IfIndex 5", 120)
        (cfg.log_dir / "prepare.log").write_text(prep.stdout, encoding="utf-8")
        if "B6C-ENV-READY" not in prep.stdout:
            raise GateError("prepare failed")
        ready = True
        base = (f"--remote-address {args.target} --remote-port {args.remote_port} "
                f"--listener-port {args.listener_port}")
        results = {}
        elapsed, cpu, out = run_suite(
            remote, cfg, "forwarded-tcp",
            f"forwarded-tcp --phase core {base} --backend {args.linux_ip}:{args.remote_port} "
            f"--verify-port {args.verify_port} --expect-accepts 1 --bytes {args.tcp_bytes} --wait-ms 60000",
            f"python3 {ROOT / 'tools/linux/forwarded_tcp_client.py'} --target {args.target}:{args.remote_port} "
            f"--bytes {args.tcp_bytes} --verify {args.vm_ip}:{args.verify_port}", 300)
        results["forwarded_tcp"] = {"seconds": elapsed, "client_cpu_seconds": cpu,
                                    "mbps": args.tcp_bytes / elapsed / 1e6}
        began = time.perf_counter()
        host_tcp = subprocess.run(
            f"python3 {ROOT / 'tools/linux/tcp_echo_client.py'} --target {args.linux_ip}:{args.remote_port} "
            f"--bytes {args.tcp_bytes}", shell=True, text=True, capture_output=True, timeout=300)
        host_seconds = time.perf_counter() - began
        results["host_tcp"] = {"seconds": host_seconds, "mbps": args.tcp_bytes / host_seconds / 1e6}
        (cfg.log_dir / "host-tcp.log").write_text(host_tcp.stdout, encoding="utf-8")
        elapsed, cpu, out = run_suite(
            remote, cfg, "forwarded-udp",
            f"forwarded-udp --phase core {base} --backend {args.linux_ip}:{args.remote_port} "
            f"--verify-port {args.verify_port} --expect-datagrams {args.udp_count} --wait-ms 60000",
            f"python3 {ROOT / 'tools/linux/forwarded_udp_client.py'} --target {args.target}:{args.remote_port} "
            f"--count {args.udp_count} --verify {args.vm_ip}:{args.verify_port} --source-port-base 46001 --interval {args.udp_interval}",
            300)
        results["forwarded_udp"] = {"seconds": elapsed, "client_cpu_seconds": cpu,
                                    "pps": args.udp_count / elapsed}
        began = time.perf_counter()
        host_udp = subprocess.run(
            f"python3 {ROOT / 'tools/linux/forwarded_udp_client.py'} --target {args.linux_ip}:{args.remote_port} "
            f"--count {args.udp_count} --source-port-base 47001 --interval {args.udp_interval}",
            shell=True, text=True, capture_output=True, timeout=300)
        results["host_udp"] = {"seconds": time.perf_counter() - began,
                              "pps": args.udp_count / (time.perf_counter() - began)}
        (cfg.log_dir / "host-udp.log").write_text(host_udp.stdout, encoding="utf-8")
        (cfg.log_dir / "results.json").write_text(json.dumps(results, indent=2), encoding="utf-8")
        print(json.dumps(results, indent=2), flush=True)
    finally:
        if ready:
            remote.run(f"pwsh -NoProfile -File {args.prepare} -Remove", 120, False)
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except GateError as error:
        print(f"[a5] FAIL: {error}", file=sys.stderr)
        sys.exit(1)
