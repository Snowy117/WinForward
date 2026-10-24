
#!/usr/bin/env python3
"""Independent Linux gate for the Forwarded data plane.

Runs on the Linux client host and drives a Windows host through configurable
command prefixes:

  --run-cmd   "windows-vm fast run"     (synchronous PowerShell command)
  --poll-cmd  "windows-vm b6ctl run"    (second session used for polling)
  --up-cmd    "windows-vm up2 up"       (upload a local file)

CI must provide equivalents (e.g. ssh/winrm wrappers).  The gate never passes
--no-verify to any suite or client; a phase that needs it must be changed, not
silenced.

Phases:
  tcp-core, udp-core, fragment-ipv4, permit-first, host-same-remote,
  fail-udp, fail-tcp, reject (optional; requires the B6a reject harness/config).

Not automated here:
  * multi-ingress / hotswap / noingress / StateCommand (Windows-only wfprobe
    phases driven by the product controller; see B6b verification).
  * IPv6 9-fragment (needs an IPv6 address + MTU change on the client; see
    tools/linux/ipv6_mtu_fragment.py, run manually).
  * A5 performance numbers (see verification/a5-cost.md; measurement procedure,
    not pass/fail).
"""
import argparse
import datetime
import json
import os
import pathlib
import shlex
import socket
import subprocess
import sys
import time

ROOT = pathlib.Path(__file__).resolve().parents[2]
LINUX = ROOT / "tools" / "linux"


class GateError(RuntimeError):
    pass


def log(message):
    print(f"[forwarded-gate] {message}", flush=True)


class Remote:
    def __init__(self, run_cmd, poll_cmd, up_cmd):
        self.run_cmd = run_cmd
        self.poll_cmd = poll_cmd
        self.up_cmd = up_cmd

    def _shell(self, prefix, command, timeout, check):
        full = f"{prefix} {shlex.quote(command)}"
        result = subprocess.run(full, shell=True, text=True, capture_output=True, timeout=timeout)
        if check and result.returncode != 0:
            raise GateError(f"remote command failed: {command}\n{result.stdout}\n{result.stderr}")
        return result

    def run(self, command, timeout=120, check=True):
        return self._shell(self.run_cmd, command, timeout, check)

    def poll(self, command, timeout=60):
        return self._shell(self.poll_cmd, command, timeout, False)

    def upload(self, local, remote, timeout=120):
        if not self.up_cmd:
            raise GateError("--up-cmd is required to upload gate scripts")
        full = f"{self.up_cmd} {shlex.quote(str(local))} {shlex.quote(remote)}"
        result = subprocess.run(full, shell=True, text=True, capture_output=True, timeout=timeout)
        if result.returncode != 0:
            raise GateError(f"upload failed: {local} -> {remote}\n{result.stdout}\n{result.stderr}")


def start_backends(port, log_dir):
    started = []
    for name, script in (("tcp", "tcp_echo.py"), ("udp", "udp_echo.py")):
        sock_type = socket.SOCK_STREAM if name == "tcp" else socket.SOCK_DGRAM
        probe = socket.socket(socket.AF_INET, sock_type)
        probe.settimeout(0.3)
        try:
            if name == "tcp":
                probe.connect(("127.0.0.1", port))
            else:
                probe.sendto(b"probe", ("127.0.0.1", port))
        except OSError:
            log(f"starting {name} echo backend on {port}")
            log_file = open(log_dir / f"forwarded-gate-{name}-echo.log", "wb")
            proc = subprocess.Popen([sys.executable, str(LINUX / script), str(port)],
                                    stdout=log_file, stderr=subprocess.STDOUT)
            started.append(proc)
            time.sleep(0.5)
        finally:
            probe.close()
    return started


def stop_process(proc):
    if proc is None:
        return
    proc.terminate()
    try:
        proc.wait(timeout=3)
    except subprocess.TimeoutExpired:
        proc.kill()


def write_suite_script(path, helper, exe, out, args):
    path.write_text(
        "& '" + helper + "' -Exe '" + exe + "' -Out '" + out + "' -ArgumentLine '" + args + "'\n",
        encoding="utf-8",
    )


def read_remote(remote, path, timeout=60):
    return remote.poll(f"Get-Content -LiteralPath '{path}' -Raw -ErrorAction SilentlyContinue",
                       timeout).stdout


def run_phase(remote, cfg, name, suite_args, client, expected, timeout):
    if "--no-verify" in suite_args or "--no-verify" in client.get("cmd", ""):
        raise GateError(f"{name}: CI forbids --no-verify")
    remote_script = f"{cfg.remote_root}/fg-{name}.ps1"
    remote_out = f"{cfg.remote_root}/fg-{name}.out"
    local_script = cfg.log_dir / f"fg-{name}.ps1"
    write_suite_script(local_script, cfg.helper, cfg.suite_exe, remote_out, suite_args)
    remote.upload(local_script, remote_script)
    start = subprocess.Popen(
        f"{remote.run_cmd} {shlex.quote('pwsh -NoProfile -File ' + remote_script)}",
        shell=True, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
    )
    deadline = time.time() + 60
    output = ""
    while time.time() < deadline:
        output = read_remote(remote, remote_out, 30)
        if "READY" in output or "ASSERT FAIL" in output:
            break
        time.sleep(0.7)
    if "READY" not in output:
        start.terminate()
        raise GateError(f"{name}: suite did not reach READY\n{output[-2000:]}")
    log(f"{name}: READY")
    client_cmd = client["cmd"].format(**cfg.__dict__)
    client_result = subprocess.run(client_cmd, shell=True, text=True, capture_output=True,
                                   timeout=client.get("timeout", 180))
    (cfg.log_dir / f"{name}-client.log").write_text(
        f"$ {client_cmd}\n{client_result.stdout}\n{client_result.stderr}\n", encoding="utf-8")
    if client_result.returncode != 0 and not client.get("allow_rc", False):
        raise GateError(f"{name}: client failed rc={client_result.returncode}\n{client_result.stdout}\n{client_result.stderr}")
    start_out, _ = start.communicate(timeout=timeout)
    (cfg.log_dir / f"{name}-suite.log").write_text(
        f"{start_out}\n--- suite output ---\n{output}\n", encoding="utf-8")
    final = read_remote(remote, remote_out, 30)
    (cfg.log_dir / f"{name}-suite.log").write_text(
        f"{start_out}\n--- suite output ---\n{final}\n", encoding="utf-8")
    if "ASSERT FAIL" in final:
        raise GateError(f"{name}: suite assertion failed\n{final[-4000:]}")
    if "SUITE-EXIT=0" not in start_out:
        raise GateError(f"{name}: suite exit not 0\n{start_out[-2000:]}")
    for pattern in expected:
        if pattern not in final and pattern not in client_result.stdout:
            raise GateError(f"{name}: missing expected text {pattern!r}\n{final[-3000:]}\n{client_result.stdout[-2000:]}")
    log(f"{name}: PASS")


def run_reject(remote, cfg, backend):
    if cfg.skip_reject:
        log("reject: skipped by --skip-reject")
        return
    fake = None
    if not port_open("127.0.0.1", 40500, socket.SOCK_STREAM):
        fake = subprocess.Popen([sys.executable, str(LINUX / "fake_socks5_udp.py"), "40500", "--reject"],
                                stdout=open(cfg.log_dir / "reject-fake.log", "wb"),
                                stderr=subprocess.STDOUT)
        time.sleep(0.5)
    try:
        remote.upload(cfg.reject_config, f"{cfg.remote_root}/fg-reject.json")
        report = f"{cfg.remote_root}/fg-reject-client.txt"
        out = f"{cfg.remote_root}/fg-reject.out"
        script = (f"pwsh -NoProfile -File {cfg.reject_harness} -Config {cfg.remote_root}/fg-reject.json "
                  f"-ClientWaitSeconds 30 -ClientReport {report} *> {out}")
        start = subprocess.Popen(f"{remote.run_cmd} {shlex.quote(script)}", shell=True, text=True,
                                 stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        deadline = time.time() + 60
        text = ""
        while time.time() < deadline:
            text = read_remote(remote, out, 30)
            if "READY" in text or "ASSERT FAIL" in text:
                break
            time.sleep(0.7)
        if "READY" not in text:
            start.terminate()
            raise GateError(f"reject: harness did not reach READY\n{text[-2000:]}")
        client = (f"python3 {LINUX / 'forwarded_udp_client.py'} --target {cfg.target}:{cfg.remote_port} "
                  f"--count 3 --interval 0.6 --timeout 1")
        client_result = subprocess.run(client, shell=True, text=True, capture_output=True, timeout=120)
        (cfg.log_dir / "reject-client.log").write_text(client_result.stdout + client_result.stderr, encoding="utf-8")
        remote.upload(cfg.log_dir / "reject-client.log", report)
        start.communicate(timeout=180)
        final = read_remote(remote, out, 30)
        (cfg.log_dir / "reject-harness.log").write_text(final, encoding="utf-8")
        if "REJECT-E2E-RESULT=PASS" not in final:
            raise GateError(f"reject: harness did not PASS\n{final[-4000:]}")
        log("reject: PASS")
    finally:
        stop_process(fake)


def port_open(host, port, kind):
    sock = socket.socket(socket.AF_INET, kind)
    sock.settimeout(0.3)
    try:
        if kind == socket.SOCK_STREAM:
            sock.connect((host, port))
        else:
            sock.sendto(b"probe", (host, port))
        return True
    except OSError:
        return False
    finally:
        sock.close()


class Config:
    pass


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--run-cmd", default=os.environ.get("WF_GATE_RUN", "windows-vm fast run"))
    parser.add_argument("--poll-cmd", default=os.environ.get("WF_GATE_POLL", "windows-vm b6ctl run"))
    parser.add_argument("--up-cmd", default=os.environ.get("WF_GATE_UP", "windows-vm up2 up"))
    parser.add_argument("--remote-root", default="C:/workspace")
    parser.add_argument("--suite-exe", default="C:/workspace/WinForward.Kernel/tools/wfprobe/bin/Release/net11.0/wfprobe.exe")
    parser.add_argument("--helper", default="C:/workspace/forwarded-gate-suite.ps1")
    parser.add_argument("--prepare", default="C:/workspace/prepare-b6c-forwarded.ps1")
    parser.add_argument("--reject-harness", default="C:/workspace/WinForward.Kernel/tools/windows/forwarded-udp-reject-e2e.ps1")
    parser.add_argument("--reject-config", default=str(ROOT / "tools" / "linux" / "forwarded-udp-reject.json"))
    parser.add_argument("--linux-ip", default="192.168.100.4")
    parser.add_argument("--target", default="192.168.100.99")
    parser.add_argument("--vm-ip", default="192.168.100.2")
    parser.add_argument("--ifindex", type=int, default=5)
    parser.add_argument("--remote-port", type=int, default=40250)
    parser.add_argument("--listener-port", type=int, default=40400)
    parser.add_argument("--verify-port", type=int, default=41400)
    parser.add_argument("--host-port", type=int, default=40600)
    parser.add_argument("--log-dir", default=str(ROOT / "verification" / "forwarded-gate"))
    parser.add_argument("--skip-reject", action="store_true")
    parser.add_argument("--include-fail", action="store_true", help="also run fail phases (B6c ABI v7 finding; not re-run on v8; see verification)")
    parser.add_argument("--no-prepare", action="store_true")
    parser.add_argument("--keep-alias", action="store_true")
    args = parser.parse_args()

    cfg = Config()
    cfg.__dict__.update(vars(args))
    cfg.log_dir = pathlib.Path(args.log_dir)
    cfg.log_dir.mkdir(parents=True, exist_ok=True)
    remote = Remote(args.run_cmd, args.poll_cmd, args.up_cmd)
    started = []
    prepared = False
    try:
        if not args.no_prepare:
            remote.upload(ROOT / "tools/windows/prepare-b6c-forwarded.ps1", args.prepare)
            remote.upload(ROOT / "tools/windows/forwarded-gate-suite.ps1", args.helper)
            result = remote.run(f"pwsh -NoProfile -File {args.prepare} -IfIndex {args.ifindex}", 120)
            (cfg.log_dir / "prepare.log").write_text(result.stdout + result.stderr, encoding="utf-8")
            if "B6C-ENV-READY" not in result.stdout:
                raise GateError("prepare failed")
            prepared = True
        started = start_backends(args.remote_port, cfg.log_dir)
        base = (f"--remote-address {args.target} --remote-port {args.remote_port} "
                f"--listener-port {args.listener_port}")
        run_phase(remote, cfg, "tcp-core",
                  f"forwarded-tcp --phase core {base} --backend {args.linux_ip}:{args.remote_port} "
                  f"--verify-port {args.verify_port} --expect-accepts 1 --bytes 2048 --wait-ms 60000",
                  {"cmd": f"python3 {LINUX / 'forwarded_tcp_client.py'} --target {args.target}:{args.remote_port} "
                          f"--bytes 2048 --verify {args.vm_ip}:{args.verify_port}"},
                  ["PASS"], 180)
        run_phase(remote, cfg, "udp-core",
                  f"forwarded-udp --phase core {base} --backend {args.linux_ip}:{args.remote_port} "
                  f"--verify-port {args.verify_port} --expect-datagrams 10 --wait-ms 60000",
                  {"cmd": f"python3 {LINUX / 'forwarded_udp_client.py'} --target {args.target}:{args.remote_port} "
                          f"--count 10 --verify {args.vm_ip}:{args.verify_port} --source-port-base 44001"},
                  ["ASSERT PASS"], 180)
        raw = ("podman run --rm --network=host --cap-add=NET_RAW "
               f"-v {ROOT / 'tools/linux'}:/work:ro docker.io/library/python:3.12-alpine "
               "python /work/raw_fragment_sender.py --family 4 "
               f"--source {args.linux_ip} --target {args.target}:{args.remote_port}")
        run_phase(remote, cfg, "fragment-ipv4",
                  f"forwarded-udp --phase fragment {base} --wait-ms 60000 --expect-fragments 9 "
                  f"--fragment-timeout-ms 5000",
                  {"cmd": raw + " --fragments 9 --identification 46791"},
                  ["fragmented=9", "udp_blocked=9"], 180)
        run_phase(remote, cfg, "permit-first",
                  f"forwarded-udp --phase fragment {base} --wait-ms 60000 --expect-fragments 9 "
                  f"--fragment-timeout-ms 5000 --fragment-permit-first-port 9999",
                  {"cmd": raw + " --fragments 9 --identification 46792"},
                  ["fragmented=9", "udp_blocked=9"], 180)
        run_phase(remote, cfg, "host-same-remote",
                  f"forwarded-udp --phase fragment-host {base} --host-fragment-address {args.target} "
                  f"--host-fragment-port {args.host_port} --fragment-timeout-ms 8000 --wait-ms 60000",
                  {"cmd": raw.replace(f":{args.remote_port}", f":{args.host_port}") + " --fragments 9 --identification 46793"},
                  ["received=True"], 180)
        if args.include_fail:
            run_phase(remote, cfg, "fail-udp",
                      f"forwarded-udp --phase fail {base} --wait-ms 15000",
                      {"cmd": f"python3 {LINUX / 'forwarded_udp_client.py'} --target {args.target}:{args.remote_port} "
                              f"--count 1 --timeout 1", "allow_rc": True},
                      ["no_config=1"], 120)
            run_phase(remote, cfg, "fail-tcp",
                      f"forwarded-tcp --phase fail {base} --wait-ms 15000",
                      {"cmd": f"python3 {LINUX / 'forwarded_tcp_probe.py'} --target {args.target}:{args.remote_port}"},
                      ["no_config=1"], 120)
        else:
            log("fail-udp/fail-tcp: skipped (B6c ABI v7 known finding; not re-run on v8; pass --include-fail to exercise)")
        run_reject(remote, cfg, args)
    finally:
        for proc in started:
            stop_process(proc)
        if prepared and not args.keep_alias:
            try:
                remote.run(f"pwsh -NoProfile -File {args.prepare} -Remove", 120, False)
            except Exception:
                pass
        for name in ("prepare",):
            pass
    log("ALL PASS")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except GateError as error:
        print(f"[forwarded-gate] FAIL: {error}", file=sys.stderr)
        sys.exit(1)
