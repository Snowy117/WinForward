# Verification — Forwarded IPv6 数据面 E2E

日期：2026-10-25。范围：`forwarded-tcp` / `forwarded-udp` 的 native IPv6 happy path、
TCP 同入口多 L 与 Internal/External 两入口、Linux 门禁 `--include-v6`、v4 回归与环境恢复。
本任务未改驱动；驱动 ABI 仍为 **v8**。

## 1. 结论

| 项 | 结果 | 证据 |
|---|---|---|
| `tcp-core6`（64 KiB 双向） | PASS，44 条断言 | `verification/check-final/tcp-core6-suite.log`、`tcp-core6-client.log` |
| `tcp-upstreams6`（两个 `atyp=4` CONNECT） | PASS，42 条断言 | `verification/check-final/tcp-upstreams6-suite.log`、`-client.log` |
| `udp-core6`（10 datagram、decoy 无数据） | PASS，85 条断言 | `verification/check-final/udp-core6-suite.log`、`-client.log` |
| `udp-socks5-6`（两次 ASSOCIATE、`atyp=4`） | PASS，36 条断言 | `verification/check-final/udp-socks5-6-suite.log`、`-client.log` |
| `tcp-multi6`（4 连接、同入口两 L + 两入口） | PASS，42 条断言 | `verification/check-final/tcp-multi6-suite.log`、`-client.log` |
| `forwarded_gate.py --include-v6` | **ALL PASS**（v4 6 阶段 + v6 5 阶段） | `verification/check-final/run.log` |
| 默认 v4 门禁（不带 `--include-v6`） | **ALL PASS** | `verification/check-final-v4/run.log` |
| v4 `upstreams` / `multi` / `hotswap` 夹具回归 | PASS：40 / 42 / 27 条断言 | `verification/manual-v4/` |
| 负向：错误 target | 门禁在 `tcp-core6` 客户端超时，exit 1；清理无残留 | `verification/check-final-negative-v6/run.log` |
| 负向：`--no-verify` | argparse 拒绝，exit 2；`run_phase` 仍内建 guard | `verification/negative-v6/no-verify.log` |
| 环境恢复 | v6 地址/规则/process/listener 全部清除 | `verification/check-final/cleanup.log`、`run.log` |
| `tools/check.sh --full` | PASS（8 PASS / 0 FAIL / 0 SKIP） | 本文件 §7、§12 |
| Userland 测试驱动 | `total=254 passed=254 failed=0` | 本文件 §7、§12 |

非零但已解释的历史日志：`verification/full-v6/run-udpbug.log` 是修复前 `udp-core6` 的失败现场
（见 §6.1）；`run-firstgreen.log` 是 prepare 补 v4 规则前的首次全绿运行。实现阶段的
`verification/full-v6/run.log` 保留历史；最终脚本的复跑证据是 `verification/check-final/run.log`
（见 §12）。

## 2. 环境与驱动

- Windows VM `WinLTSC`，ABI v8，驱动 `C:\Windows\System32\drivers\wfprobe.sys`，
  SHA-256 `9838D4BCEB69F7BDFE88C619707A296580221F74C08A5C31A3FC8189CD276350`。
- 入口：Internal ifIndex=5、External ifIndex=14；Internal `ms_tcpip6` 启用。
- IPv6 地址：

| 角色 | 地址 | 来源 |
|---|---|---|
| Internal 主 L（L1） | `fe80::8ca8:508a:7f89:be57%5` | 现有，prepare 只读发现 |
| Internal 第二 L（L2） | `fe80::98` | prepare `-WithIpv6` 添加 |
| Internal target | `fe80::99` | prepare 添加 |
| External L | `fe80::a09e:8f4a:9bb4:71fc%14` | 现有，prepare 只读发现 |
| External target | `fe80::199` | prepare 添加 |
| Linux eth0 源 | `fe80::215:5dff:fe03:728a%eth0` | 现有，不改 |
| Linux eth1 源 | `fe80::215:5dff:fe03:728b%eth1` | 现有，不改 |

- 原始目的 target 与 L 是不同地址；回程 SNAT 若写错成 L，客户端的源地址/SHA 校验会直接失败。

## 3. 本轮改动（接续前一位 implement agent）

1. `tools/wfprobe/ForwardedUdpSuite.cs`
   - `RunCoreAsync` 把 listener 与 backend 的 receive-from 占位端点和族分开：
     `listenerAny = AnyFor(listener.AddressFamily)`、`backendAny = AnyFor(backendSocket.AddressFamily)`。
     这是 `udp-core6` 0/10 的根因（v6 listener + v4 backend 共用 `IPv6Any` 时
     `Socket.ReceiveFromAsync` 抛 `The supplied EndPoint ... is not valid for this Socket`）。
   - `Socks5UdpFakeServer` 新增 `V6Datagrams` 计数；socks5 阶段在 v6 目的下断言每个上游
     至少收到 `expectDatagrams` 个 `atyp=4` datagram，补上 PRD 要求 5 的显式 `atyp=4` 证据。
2. `tools/linux/forwarded_gate.py`
   - `tcp-multi6` 的 Linux 绑定地址加 `%eth0` / `%eth1`（链路本地 bind 需要 scope）。
   - `tcp-multi6` 期望文本改为 `multi 选中了非首个 L`（与 suite 实际输出一致）。
3. `tools/windows/prepare-b6c-forwarded.ps1`
   - v4 端口放行补齐 `TCP 40250/40251`、`UDP 40251`、`TCP 40420`，与 v6 规则集对齐。
     缺 `TCP 40420` 时 v4 `multi` 的 External（Private）连接可达，而 Internal（Public）
     被防火墙丢弃，4 条只 accept 1 条；补规则后 42/0。
4. 文档：`src/driver/README.md`、`src/userland/README.md`、`.trellis/spec/backend/wfp-architecture.md`
   （新增 §7.17）更新 IPv6 状态；`tools/linux/forwarded_gate.md` 已含 `--include-v6` 说明。

驱动、blob 布局与产品网络语义未改。

## 4. 全量门禁

命令（工作目录 `/home/paff/Projects/WinForward.Kernel`）：

```bash
python3 tools/linux/forwarded_gate.py --include-v6 \
  --log-dir .trellis/tasks/10-24-fwd-ipv6-e2e/verification/full-v6 \
  2>&1 | tee .trellis/tasks/10-24-fwd-ipv6-e2e/verification/full-v6/run.log
```

结果：v4 的 `tcp-core`、`udp-core`、`fragment-ipv4`、`permit-first`、`host-same-remote`、
`reject` 与 v6 的五个阶段全部 PASS，末尾 `ALL PASS`；`finally` 的 `prepare -Remove`
输出 `B6C-V6-CLEAN ...` / `B6C-ENV-CLEAN ...`，残留检查 `residual_v6=none b6c_rules=none`。
`fail-udp/fail-tcp` 按既有原因跳过（B6c ABI v7 发现，v8 未复验；不是本任务范围）。

默认 v4 门禁（同一条命令去掉 `--include-v6`，日志目录 `verification/v4-regression-current`）
同样 `ALL PASS`；此前一轮基线在 `verification/v4-regression/`。

## 5. 分阶段关键断言

### 5.1 tcp-core6

- 客户端：`bytes=65536 acked=65537 sha=ef463692... ok=1`。
- redirect 表：`server=fe80::99`、`ingress=5`、`L=fe80::8ca8:508a:7f89:be57`；
  `inject_failed=0`、`return_failed=0`，计数恒等式全部通过。
- suite 44 条断言全部 PASS（含 legacy query、ACK、半关闭）。

### 5.2 tcp-upstreams6

- 两个假 SOCKS5：`upstream 0 收到 atyp=4 CONNECT (1)`、`upstream 1 收到 atyp=4 CONNECT (1)`。
- 40250/40251 分别映射 upstream 0/1，两会话各 2048B 双向，客户端 `ok=1`。

### 5.3 udp-core6

- 客户端：`received=10/10 source_ok=True last_source=fe80::99:40250`，SHA 与 suite 一致。
- redirect 表每条 datagram 都记录 `server=fe80::99:40250`；`udp_created=1`、
  `udp_ingress=10`、`udp_return=10`；decoy（绑定 `fe80::99:40250`）`无数据`。

### 5.4 udp-socks5-6

- 两个目的各一次 UDP ASSOCIATE（两个上游各 1 次），每个上游 10 个 `atyp=4` datagram。
- 两个客户端各 10/10，源地址分别为 `fe80::99:40250` / `fe80::99:40251`，SHA 一致。

### 5.5 tcp-multi6

- 4 条连接全部 2048B 双向；跨入口 `L=fe80::8ca8...%5 + fe80::a09e...%14`、ingress 5/14；
  同入口 `L=fe80::8ca8...%5 + fe80::98%5`、同一源端口 40501 两个目的。
- `redirect_multi_l=1`（`multi 选中了非首个 L`）、`return_ambiguous=0`、`no_ingress=0`；
  计数恒等式通过。

## 6. 调试记录

### 6.1 udp-core6 0/10（`run-udpbug.log`）

第一次全量运行在 `udp-core6` 失败：客户端 `received=0/10`，suite 进程在
`backendSocket.ReceiveFromAsync(..., any, ...)` 抛出
`The supplied EndPoint of AddressFamily InterNetworkV6 is not valid for this Socket,
use InterNetwork instead. (Parameter 'remoteEndPoint')`。原因是同一 `any` 端点同时用于
v6 listener 和 v4 backend。按 §3.1 分离后 `udp-core6` 10/10；随后 `udp-socks5-6`、
`tcp-multi6` 与全量门禁均通过。单阶段调试日志在 `verification/manual6/`。

### 6.2 v4 multi 只 accept 1/4

`verification/manual-v4` 的首轮 v4 `multi` 失败：Internal 目标连接超时、suite 在窗口内只
接受 1 条连接。检查发现 Internal 网络类别是 `Public`、External 是 `Private`，而 prepare
的 v4 规则集缺少 `TCP 40420`（multi 的 listener 端口），Public 入站被默认丢弃。补规则后
v4 `multi` 42/0、`hotswap` 27/0。该缺口在 B6c 规则集中已存在，不是本轮夹具改动引入。

## 7. 质量检查

```bash
tools/check.sh --full
# 环境/格式/有效行/cppcheck/禁止模式/配置/clang-tidy/wf_check：8 PASS，0 FAIL，0 SKIP
# 结论：PASS（耗时 131.064 s）

dotnet run --project src/userland/WinForward.Userland.Tests -c Release -- test
# [tests] total=254 passed=254 failed=0
```

`dotnet test` 对本仓库的零依赖测试驱动不会执行用例（工程不是 Microsoft.NET.Test.Sdk
测试项目）；实际用例通过上面的 `dotnet run ... -- test` 执行。另手工复核了
`tools/linux/forwarded_tcp_client.py`、`forwarded_udp_client.py`、`forwarded_tcp_b6b.py`、
`forwarded_gate.py` 的 `py_compile`。

v4 夹具回归（`verification/manual-v4/`）：

| 阶段 | 结果 |
|---|---|
| `tcp-upstreams` | 40 PASS / 0 FAIL，两个 upstream 各一次 CONNECT |
| `tcp-multi` | 42 PASS / 0 FAIL，`redirect_multi_l=1`、`return_ambiguous=0`、`no_ingress=0` |
| `tcp-hotswap` | 27 PASS / 0 FAIL，旧连接 4096B、新连接 2048B |

## 8. 负向验证

- 把 `tcp-core6` 的 suite 与客户端 target 改成不存在的 `fe80::dead`，用等价的 gate
  副本运行（其余 phase 与清理逻辑不变）：门禁在 `tcp-core6` 以 client
  `TimeoutError` / rc=1 退出，`cleanup check: residual_v6=none b6c_rules=none`。
  日志：`verification/negative-v6/run.log`。
- `python3 tools/linux/forwarded_gate.py --no-verify`：`unrecognized arguments: --no-verify`，
  exit 2。日志：`verification/negative-v6/no-verify.log`。`run_phase` 内的
  `--no-verify` guard 未放宽。

## 9. 环境恢复

最终清理（门禁 `finally` + 手工复核）：

- Windows IPv6 只剩 `fe80::8ca8:508a:7f89:be57%5`（Internal）、
  `fe80::a09e:8f4a:9bb4:71fc%14`、`fd00:1234:5678:1::2`（External）、`::1`；
  `fe80::98/99/199` 均无残留。
- `Get-NetFirewallRule -DisplayName 'B6C-ENV-*'` 计数 0；IPv4 别名 `192.168.100.99` 已删除。
- 无 `wfprobe` 进程，无 40250/40251/40400/40420/40600/41400 listener。
- Linux eth0/eth1 地址未变化（仅有现有 IPv4/链路本地/ULA）。
- `verification/full-v6/cleanup.log` 记录 `B6C-V6-CLEAN internal=fe80::98,fe80::99
  external=fe80::199`；`run.log` 记录 `residual_v6=none b6c_rules=none`。

## 10. 剩余边界与未覆盖

- UDP 多 L / 多入口、UDP 与 IPv6 热替换、IPv6 9 片 / atomic fragment / 逐片 raw 构造未在
  本任务 E2E；IPv6 分片闸的既有证据仍来自已归档 B6 任务。
- `WfpController.ResolveForwardedListenerPlan` 仍只自动解析 IPv4 L，
  `ForwardedListenerAddress` 没有 JSON 入口；v6 L 由 wfprobe 直接下发 ingress 配置。
- `fail-udp` / `fail-tcp` 未在 ABI v8 复验（B6c 遗留边界，`--include-fail` 保留为诊断）。
- LWF 对注入帧可见性、UDP 多 L 并发多目的、产品控制器多 L 仍未验证（沿用 B6b 边界）。

## 11. 复核补充（trellis-check，2026-10-25）

本轮复核在实现者的全量证据之外补做了失败路径收口，并用最终脚本重跑了一次全量门禁。

### 11.1 复核改动

1. `tools/linux/forwarded_gate.py`
   - `run_phase` 的客户端失败 / 未 READY 分支改为 `drain_or_stop`：先让 suite 走完等待窗口
     并恢复 forwarded config，再保存 `*-suite.log`；只有收输出超时才终止本地包装进程。
     原实现直接 `terminate()`，失败阶段既没有 suite 日志，也可能把已安装的 blob/config
     留在驱动里。
   - prepare 命令改为 `check=False` 并先记录 `prepare.log`；上传成功后即置 `prepared`，
     prepare 中途失败（例如 v6 地址只加了一半）也会在 `finally` 执行 `-Remove`；清理命令
     带上实际的 `-IfIndex` / `-ExternalIfIndex`，返回非零即记为清理失败。
   - v6 清理残留检查在 `Get-NetIPAddress` / `Get-NetFirewallRule` 失败或返回空地址表时
     报错，不再把“查不到”当成“已清干净”；prepare 失败未打印 `B6C-V6-READY` 时用脚本
     默认地址 `fe80::98/99/199` 做残留检查。
2. `tools/windows/prepare-b6c-forwarded.ps1`
   - `ms_tcpip6` 绑定检查移到任何宿主状态修改之前：禁用时直接失败，不先加 v4 别名/规则。
3. 文档与 usage：`tools/linux/forwarded_gate.md` 补充失败阶段行为；
   `tools/wfprobe/Program.cs` 的 `forwarded-udp` usage 标明 IPv6 地址。

驱动、ABI（v8）、blob 布局与产品网络语义未改。

### 11.2 复核结果

| 项 | 结果 | 证据 |
|---|---|---|
| `tools/check.sh --full` | PASS，8 PASS / 0 FAIL / 0 SKIP（123.362 s） | `verification/check-v6/check-full.log` |
| `py_compile`（4 个改动 Python 文件） | PASS | —— |
| VM wfprobe 构建 | `0 Warning(s) 0 Error(s)` | `dotnet build ... -c Release -v:q` |
| `forwarded_gate.py --include-v6` 复跑 | **ALL PASS**，v4 6 阶段 + v6 5 阶段；断言数 44/42/85/36/42；`residual_v6=none b6c_rules=none` | `verification/check-v6/run.log` |
| 默认 v4 门禁复跑（不带 `--include-v6`） | **ALL PASS**（v4 6 阶段）；`cleanup.log` 记录 v6 地址清理 | `verification/check-v4/run.log`、`cleanup.log` |
| 失败路径复跑（target=`fe80::dead`） | 在 `tcp-core6` 打印 FAIL；`tcp-core6-suite.log` 记录 `ASSERT FAIL`、`SUITE-EXIT=6` 与 `已恢复 rules=0 fallback=permit config=disabled`；清理无残留 | `verification/check-negative-v6/run.log`、`tcp-core6-suite.log`、`gate-dead.patch` |
| 复核后环境 | Internal 仅 `fe80::8ca8...%5`，External 仅 `fe80::a09e...%14` + ULA；`B6C-ENV-*` 规则 0、无 `wfprobe` 进程、无测试 listener | VM 手工复核 |

复跑命令：

```bash
tools/check.sh --full
python3 tools/linux/forwarded_gate.py --include-v6 \
  --log-dir .trellis/tasks/10-24-fwd-ipv6-e2e/verification/check-v6 \
  2>&1 | tee .trellis/tasks/10-24-fwd-ipv6-e2e/verification/check-v6/run.log
python3 tools/linux/forwarded_gate.py \
  --log-dir .trellis/tasks/10-24-fwd-ipv6-e2e/verification/check-v4 \
  2>&1 | tee .trellis/tasks/10-24-fwd-ipv6-e2e/verification/check-v4/run.log
# 负向：按 gate-dead.patch 生成 target=fe80::dead 的临时 gate 副本后运行
python3 /tmp/forwarded_gate_dead.py --include-v6 \
  --log-dir .trellis/tasks/10-24-fwd-ipv6-e2e/verification/check-negative-v6 \
  2>&1 | tee .trellis/tasks/10-24-fwd-ipv6-e2e/verification/check-negative-v6/run.log
```

`check-v6` 是复核阶段的中间复跑；实现阶段的原始全量证据保留在 `full-v6/`。
`check-negative-v6` 是失败路径日志，不是通过证据；负向副本与实际脚本的差异见
`gate-dead.patch`。最终脚本在全部代码与文档收口后又复跑了一次，见 §12（`check-final/`）。

## 12. 最终脚本复跑（trellis-check 收口，2026-10-25）

本轮在全部代码与文档改动完成后，用最终脚本重跑了完整门禁与质量检查；§12 是收口证据，
`full-v6/`、`check-v6/`、`check-v4/`、`check-negative-v6/` 作为历史日志保留。驱动未改，
SHA-256 仍为 `9838D4BCEB69F7BDFE88C619707A296580221F74C08A5C31A3FC8189CD276350`，
ABI v8。

### 12.1 最终脚本状态

- `tools/linux/forwarded_gate.py`：失败阶段 `drain_or_stop`（先让 suite 恢复并收日志，
  超时才终止本地包装进程；客户端失败最多等 `min(timeout,150)` 秒，门禁在首个失败阶段
  即停止）；prepare 上传成功后立即置 `prepared`；cleanup 带 `-ExternalIfIndex` 并检查
  IPv6 地址 / `B6C-ENV-*` 规则残留；`--include-v6` 与 `--no-prepare` 互斥。
- `tools/windows/prepare-b6c-forwarded.ps1`：`-WithIpv6` 添加 `fe80::98/99/199` 与
  `B6C-ENV-V6-*` 规则；`ms_tcpip6` 检查先于任何宿主状态修改；v4 规则补齐
  TCP 40250/40251、UDP 40251、TCP 40420；`-Remove` 无论开关都清理 v6 地址与规则。
- `tools/windows/forwarded-udp-reject-e2e.ps1`：启动前删除旧 `-ClientReport`，避免把
  上一轮 `received=0/3` 当成本轮结果造成假 PASS；gate 也会在启动前删除同一文件。
- wfprobe 的 TCP/UDP 地址族、scope、`atyp=4` 断言改动见 §3、§11；本文档与 spec 的 IPv6
  口径见 §10、`.trellis/spec/backend/wfp-architecture.md` §7.17。

### 12.2 命令与结果

| 检查 | 命令 / 结果 | 证据 |
|---|---|---|
| Python 语法 | `python3 -m py_compile forwarded_gate.py forwarded_tcp_client.py forwarded_udp_client.py forwarded_tcp_b6b.py` → PASS | —— |
| wfprobe 构建 | `dotnet build ...wfprobe.csproj -c Release -v:q` → `0 Warning(s) 0 Error(s)` | VM 输出 |
| v6 全量门禁 | `python3 tools/linux/forwarded_gate.py --include-v6 --log-dir .../check-final` → **ALL PASS**；v4 6 阶段 + v6 5 阶段；断言数 44/42/85/36/42；`residual_v6=none b6c_rules=none` | `verification/check-final/run.log`、`cleanup.log` |
| 默认 v4 门禁 | `python3 tools/linux/forwarded_gate.py --log-dir .../check-final-v4` → **ALL PASS**（v4 6 阶段） | `verification/check-final-v4/run.log` |
| 项目检查 | `tools/check.sh --full` → 8 PASS / 0 FAIL / 0 SKIP（123.037 s） | `verification/check-final/check-full.log` |
| Userland 测试 | `dotnet run --project src/userland/WinForward.Userland.Tests -c Release -- test` → `total=254 passed=254 failed=0` | `verification/check-final/userland-tests.log` |
| 负向（错误 target） | 临时 gate 副本把 target 改为 `fe80::dead`：`tcp-core6` 客户端超时，gate 退出 **1**；suite `SUITE-EXIT=6`、打印 `ASSERT FAIL: 在等待窗口内收到客户端连接` 并 `已恢复 rules=0 fallback=permit config=disabled`；cleanup `residual_v6=none b6c_rules=none` | `verification/check-final-negative-v6/run.log`、`tcp-core6-suite.log`、`gate-dead.patch` |

关键内容断言（`check-final`）：

- `tcp-core6`：客户端 `bytes=65536 acked=65537 sha=ef463692... ok=1`；redirect 表
  `server=fe80::99`；44 条断言全部 PASS。
- `tcp-upstreams6`：`upstream 0/1 收到 atyp=4 CONNECT (1/1)`；两会话 2048B、`ok=1`。
- `udp-core6`：`received=10/10 source_ok=True last_source=fe80::99:40250`；decoy 无数据。
- `udp-socks5-6`：两个上游各 10 个 `atyp=4` datagram；回程源地址分别为
  `fe80::99:40250` / `fe80::99:40251`。
- `tcp-multi6`：跨入口 L=`fe80::8ca8...%5` + `fe80::a09e...%14`、ingress 5/14；
  同入口 L=`fe80::8ca8...%5` + `fe80::98%5`；`redirect_multi_l=1`、`return_ambiguous=0`、
  `no_ingress=0`；4 条连接全部 2048B 双向。

### 12.3 复跑后环境

- Windows IPv6 仅保留原地址；`fe80::98/99/199` 计数 0。
- `Get-NetFirewallRule -DisplayName 'B6C-ENV-*'` 计数 0；IPv4 别名 `192.168.100.99` 已删除。
- `Get-Process wfprobe` 计数 0；40250/40251/40400/40420/40600/41400 的 TCP/UDP listener 计数 0。
- 驱动 SHA-256 不变；Linux eth0/eth1 地址未改。
- 负向复跑同样经过 `finally` 清理，未留下地址、规则或 suite 进程。
