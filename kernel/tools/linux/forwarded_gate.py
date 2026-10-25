
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
  With --include-v6: tcp-core6, tcp-upstreams6, udp-core6, udp-socks5-6, tcp-multi6
  (link-local L/target addresses prepared and removed by the Windows prepare script).

Not automated here:
  * multi-ingress / hotswap / noingress / StateCommand IPv4 variants (Windows-only
    wfprobe phases driven by the product controller; see B6b verification).
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
# prepare-b6c-forwarded.ps1 的默认 v6 测试地址；prepare 中途失败、没打印 B6C-V6-READY 时
# 仍用它们检查 -Remove 是否清干净。
DEFAULT_V6_TEST_ADDRESSES = ("fe80::98", "fe80::99", "fe80::199")


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


def backend_alive(port, kind):
    host = "127.0.0.1"
    sock = socket.socket(socket.AF_INET, kind)
    sock.settimeout(0.5)
    try:
        sock.connect((host, port))
        if kind == socket.SOCK_STREAM:
            return True
        sock.sendall(b"probe")
        return sock.recv(64) == b"probe"
    except OSError:
        return False
    finally:
        sock.close()


def start_backends(port, log_dir):
    started = []
    for name, script in (("tcp", "tcp_echo.py"), ("udp", "udp_echo.py")):
        sock_type = socket.SOCK_STREAM if name == "tcp" else socket.SOCK_DGRAM
        if not backend_alive(port, sock_type):
            log(f"starting {name} echo backend on {port}")
            log_file = open(log_dir / f"forwarded-gate-{name}-echo.log", "wb")
            proc = subprocess.Popen([sys.executable, str(LINUX / script), str(port)],
                                    stdout=log_file, stderr=subprocess.STDOUT)
            started.append(proc)
            time.sleep(0.5)
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


def parse_v6_ready(text):
    for line in text.splitlines():
        line = line.strip()
        if line.startswith("B6C-V6-READY "):
            values = {}
            for token in line.split()[1:]:
                if "=" in token:
                    key, value = token.split("=", 1)
                    values[key] = value
            return values
    return None


def linux_link_local(interface):
    result = subprocess.run(["ip", "-6", "-o", "addr", "show", "dev", interface, "scope", "link"],
                            text=True, capture_output=True, timeout=10)
    if result.returncode != 0:
        raise GateError(f"cannot read IPv6 addresses of {interface}: {result.stderr.strip()}")
    for line in result.stdout.splitlines():
        fields = line.split()
        if "inet6" in fields:
            address = fields[fields.index("inet6") + 1].split("/")[0]
            if address.startswith("fe80:"):
                return address
    raise GateError(f"{interface} has no link-local IPv6 address")


def drain_or_stop(start, remote, remote_out, suite_log, timeout):
    """收集失败阶段的 suite 输出；超时才结束本地包装进程。

    suite 的等待窗口结束后会自行恢复空 forwarded 表与 disabled config，提前杀进程会把
    已安装的 blob/config 留在驱动里。返回是否在超时内收完输出。
    """
    try:
        start_out, _ = start.communicate(timeout=timeout)
    except subprocess.TimeoutExpired:
        stop_process(start)
        start_out = ""
    try:
        final = read_remote(remote, remote_out, 30)
    except Exception as error:
        final = f"<无法读取远端 suite 输出：{error}>"
    suite_log.write_text(f"{start_out}\n--- suite output ---\n{final}\n", encoding="utf-8")
    return "SUITE-EXIT=" in start_out


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
        drain_or_stop(start, remote, remote_out, cfg.log_dir / f"{name}-suite.log", 30)
        raise GateError(f"{name}: suite did not reach READY\n{output[-2000:]}")
    log(f"{name}: READY")
    client_cmd = client["cmd"].format(**cfg.__dict__)
    client_result = subprocess.run(client_cmd, shell=True, text=True, capture_output=True,
                                   timeout=client.get("timeout", 180))
    (cfg.log_dir / f"{name}-client.log").write_text(
        f"$ {client_cmd}\n{client_result.stdout}\n{client_result.stderr}\n", encoding="utf-8")
    if client_result.returncode != 0 and not client.get("allow_rc", False):
        # 客户端失败时也要让 suite 走完自己的等待窗口并恢复 forwarded config；
        # 直接 terminate 会把已安装的 blob/config 与远端 wfprobe 留在驱动里。
        drained = drain_or_stop(start, remote, remote_out, cfg.log_dir / f"{name}-suite.log",
                                min(timeout, 150))
        detail = f"; suite output {'collected' if drained else 'timed out, see suite log'}"
        raise GateError(f"{name}: client failed rc={client_result.returncode}{detail}\n"
                        f"{client_result.stdout}\n{client_result.stderr}")
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
        remote.run(f"Remove-Item -LiteralPath '{report}' -Force -ErrorAction SilentlyContinue", 30, False)
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
    parser.add_argument("--suite-exe", default="C:/workspace/WinForward.Kernel/tools/wfprobe/bin/Release/net10.0/wfprobe.exe")
    parser.add_argument("--helper", default="C:/workspace/forwarded-gate-suite.ps1")
    parser.add_argument("--prepare", default="C:/workspace/prepare-b6c-forwarded.ps1")
    parser.add_argument("--reject-harness", default="C:/workspace/WinForward.Kernel/tools/windows/forwarded-udp-reject-e2e.ps1")
    parser.add_argument("--reject-config", default=str(ROOT / "tools" / "linux" / "forwarded-udp-reject.json"))
    parser.add_argument("--linux-ip", default="192.168.100.4")
    parser.add_argument("--target", default="192.168.100.99")
    parser.add_argument("--vm-ip", default="192.168.100.2")
    parser.add_argument("--ifindex", type=int, default=5)
    parser.add_argument("--external-ifindex", type=int, default=14)
    parser.add_argument("--remote-port", type=int, default=40250)
    parser.add_argument("--listener-port", type=int, default=40400)
    parser.add_argument("--verify-port", type=int, default=41400)
    parser.add_argument("--host-port", type=int, default=40600)
    parser.add_argument("--log-dir", default=str(ROOT / "verification" / "forwarded-gate"))
    parser.add_argument("--skip-reject", action="store_true")
    parser.add_argument("--include-fail", action="store_true", help="also run fail phases (B6c ABI v7 finding; not re-run on v8; see verification)")
    parser.add_argument("--include-v6", action="store_true",
                        help="also run native IPv6 phases (link-local L/target addresses prepared on the VM)")
    parser.add_argument("--no-prepare", action="store_true")
    parser.add_argument("--keep-alias", action="store_true",
                        help="deprecated: cleanup always runs, including IPv6 addresses and rules")
    args = parser.parse_args()

    cfg = Config()
    cfg.__dict__.update(vars(args))
    cfg.log_dir = pathlib.Path(args.log_dir)
    cfg.log_dir.mkdir(parents=True, exist_ok=True)
    cfg.internal_l1 = None
    cfg.internal_l2 = None
    cfg.internal_target = None
    cfg.external_l1 = None
    cfg.external_target = None
    cfg.eth0_link_local = None
    cfg.eth1_link_local = None
    remote = Remote(args.run_cmd, args.poll_cmd, args.up_cmd)
    started = []
    prepared = False
    cleanup_failures = []
    try:
        if args.include_v6 and args.no_prepare:
            raise GateError("--include-v6 needs prepare to create the IPv6 addresses; do not combine with --no-prepare")
        if not args.no_prepare:
            remote.upload(ROOT / "tools/windows/prepare-b6c-forwarded.ps1", args.prepare)
            remote.upload(ROOT / "tools/windows/forwarded-gate-suite.ps1", args.helper)
            prepare_cmd = (f"pwsh -NoProfile -File {args.prepare} -IfIndex {args.ifindex} "
                           f"-ExternalIfIndex {args.external_ifindex}")
            if args.include_v6:
                prepare_cmd += " -WithIpv6"
            # 上传成功后无论 prepare 是否中途失败，都要走 finally 里的 -Remove：
            # 脚本可能已经添加了 v4 别名或部分 v6 地址。
            prepared = True
            result = remote.run(prepare_cmd, 180, False)
            (cfg.log_dir / "prepare.log").write_text(result.stdout + result.stderr, encoding="utf-8")
            if result.returncode != 0 or "B6C-ENV-READY" not in result.stdout:
                raise GateError(f"prepare failed rc={result.returncode}\n"
                                f"{result.stdout[-2000:]}\n{result.stderr[-2000:]}")
            if args.include_v6:
                v6 = parse_v6_ready(result.stdout)
                if v6 is None:
                    raise GateError("prepare did not print B6C-V6-READY")
                required = ("internal_l1", "internal_l2", "internal_target", "external_l1", "external_target")
                missing = [key for key in required if not v6.get(key)]
                if missing:
                    raise GateError(f"B6C-V6-READY is missing {','.join(missing)}: {v6}")
                cfg.internal_l1 = v6["internal_l1"]
                cfg.internal_l2 = v6["internal_l2"]
                cfg.internal_target = v6["internal_target"]
                cfg.external_l1 = v6["external_l1"]
                cfg.external_target = v6["external_target"]
                cfg.eth0_link_local = linux_link_local("eth0")
                cfg.eth1_link_local = linux_link_local("eth1")
                log(f"v6 env: internal_l1={cfg.internal_l1} internal_l2={cfg.internal_l2} "
                    f"internal_target={cfg.internal_target} external_l1={cfg.external_l1} "
                    f"external_target={cfg.external_target}")
                log(f"v6 env: linux eth0={cfg.eth0_link_local}%eth0 eth1={cfg.eth1_link_local}%eth1")
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
        if args.include_v6:
            internal = f"{cfg.internal_target}%eth0"
            external = f"{cfg.external_target}%eth1"
            base6 = (f"--no-alias --local-ip {cfg.internal_l1} --ifindex {cfg.ifindex} "
                     f"--listener-port {cfg.listener_port} --backend {args.linux_ip}:{args.remote_port} "
                     f"--verify-port {cfg.verify_port}")
            run_phase(remote, cfg, "tcp-core6",
                      f"forwarded-tcp --phase core {base6} --remote-address {cfg.internal_target} "
                      f"--remote-port {args.remote_port} --expect-accepts 1 --bytes 65536 --wait-ms 60000",
                      {"cmd": f"python3 {LINUX / 'forwarded_tcp_client.py'} --target {internal}:{args.remote_port} "
                              f"--bytes 65536 --verify {args.vm_ip}:{cfg.verify_port}"},
                      ["redirect 表记录原始服务器地址", "客户端 TCP 已确认全部发送字节", "ok=1"], 240)
            run_phase(remote, cfg, "tcp-upstreams6",
                      f"forwarded-tcp --phase upstreams {base6} --remote-address {cfg.internal_target} "
                      f"--remote-port {args.remote_port} --expect-accepts 2 --bytes 2048 --wait-ms 60000",
                      {"cmd": f"python3 {LINUX / 'forwarded_tcp_client.py'} --target {internal}:{args.remote_port} "
                              f"--bytes 2048 --verify {args.vm_ip}:{cfg.verify_port} && "
                              f"python3 {LINUX / 'forwarded_tcp_client.py'} "
                              f"--target {internal}:{args.remote_port + 1} --bytes 2048 "
                              f"--verify {args.vm_ip}:{cfg.verify_port}"},
                      ["upstream 0 收到 atyp=4 CONNECT", "upstream 1 收到 atyp=4 CONNECT", "ok=1"], 240)
            run_phase(remote, cfg, "udp-core6",
                      f"forwarded-udp --phase core {base6} --remote-address {cfg.internal_target} "
                      f"--remote-port {args.remote_port} --expect-datagrams 10 --wait-ms 60000",
                      {"cmd": f"python3 {LINUX / 'forwarded_udp_client.py'} --target {internal}:{args.remote_port} "
                              f"--count 10 --verify {args.vm_ip}:{cfg.verify_port} --source-port-base 44011"},
                      ["U7：原始目的地没有收到未改写帧（decoy 无数据）",
                       "客户端看到的源地址是原始服务器地址", "source_ok=1"], 240)
            run_phase(remote, cfg, "udp-socks5-6",
                      f"forwarded-udp --phase socks5 {base6} --remote-address {cfg.internal_target} "
                      f"--remote-port {args.remote_port} --expect-datagrams 10 --wait-ms 60000",
                      {"cmd": f"python3 {LINUX / 'forwarded_udp_client.py'} "
                              f"--target {internal}:{args.remote_port} "
                              f"--target {internal}:{args.remote_port + 1} --count 10 "
                              f"--verify {args.vm_ip}:{cfg.verify_port} --source-port-base 44021"},
                      ["两个客户端都回报了验证记录", "upstream 0 只收到一次 UDP ASSOCIATE", "source_ok=1"], 240)
            run_phase(remote, cfg, "tcp-multi6",
                      f"forwarded-tcp --phase multi --no-alias --local-ip {cfg.internal_l1} "
                      f"--second-local-ip {cfg.internal_l2} --external-local-ip {cfg.external_l1} "
                      f"--ifindex {cfg.ifindex} --listener-port 40420 "
                      f"--backend {args.linux_ip}:{args.remote_port} "
                      f"--remote-address {cfg.internal_target} "
                      f"--external-remote-address {cfg.external_target} "
                      f"--remote-port {args.remote_port} --second-remote-port {args.remote_port + 1} "
                      f"--bytes 2048 --wait-ms 60000",
                      {"cmd": f"python3 {LINUX / 'forwarded_tcp_b6b.py'} --mode multi "
                              f"--target {internal}:{args.remote_port} --target {external}:{args.remote_port} "
                              f"--bind-address {cfg.eth0_link_local}%eth0 "
                              f"--bind-address {cfg.eth1_link_local}%eth1 "
                              f"--source-port 40500 --payload-size 2048 --timeout 20 && "
                              f"python3 {LINUX / 'forwarded_tcp_b6b.py'} --mode multi "
                              f"--target {internal}:{args.remote_port} "
                              f"--target {internal}:{args.remote_port + 1} "
                              f"--bind-address {cfg.eth0_link_local}%eth0 --source-port 40501 "
                              f"--payload-size 2048 --timeout 20"},
                      ["multi 跨入口使用两个 L", "multi 同入口并发使用两个 L",
                       "multi 选中了非首个 L", "ok=1"], 240)
    finally:
        for proc in started:
            stop_process(proc)
        if prepared:
            try:
                cleanup = remote.run(
                    f"pwsh -NoProfile -File {args.prepare} -IfIndex {args.ifindex} "
                    f"-ExternalIfIndex {args.external_ifindex} -Remove", 120, False)
                text = cleanup.stdout + cleanup.stderr
                (cfg.log_dir / "cleanup.log").write_text(text, encoding="utf-8")
                log(f"cleanup: {text.strip()}")
                if cleanup.returncode != 0:
                    cleanup_failures.append(
                        f"prepare -Remove failed rc={cleanup.returncode}: {text[-500:]}")
                if args.include_v6:
                    address_result = remote.poll(
                        "Get-NetIPAddress -AddressFamily IPv6 | Out-String -Width 200", 30)
                    rule_result = remote.poll(
                        "Get-NetFirewallRule -DisplayName 'B6C-ENV-*' -ErrorAction SilentlyContinue | "
                        "Out-String -Width 200", 30)
                    addresses = address_result.stdout
                    rules = rule_result.stdout.strip()
                    managed = tuple(address for address in
                                    (cfg.internal_l2, cfg.internal_target, cfg.external_target)
                                    if address) or DEFAULT_V6_TEST_ADDRESSES
                    residual = [address for address in managed if address in addresses]
                    log(f"cleanup check: residual_v6={residual or 'none'} b6c_rules={rules or 'none'}")
                    if address_result.returncode != 0 or not addresses.strip():
                        cleanup_failures.append(
                            f"IPv6 residual check failed rc={address_result.returncode}: "
                            f"{address_result.stderr.strip()}")
                    if residual:
                        cleanup_failures.append(f"residual IPv6 addresses: {','.join(residual)}")
                    if rule_result.returncode != 0:
                        cleanup_failures.append(
                            f"firewall residual check failed rc={rule_result.returncode}: "
                            f"{rule_result.stderr.strip()}")
                    if "B6C-ENV-" in rules:
                        cleanup_failures.append("residual B6C-ENV-* firewall rules")
            except Exception as error:
                cleanup_failures.append(f"cleanup failed: {error}")
    if cleanup_failures:
        raise GateError("; ".join(cleanup_failures))
    log("ALL PASS")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except GateError as error:
        print(f"[forwarded-gate] FAIL: {error}", file=sys.stderr)
        sys.exit(1)
