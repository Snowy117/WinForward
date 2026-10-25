# Forwarded Linux 门禁

`tools/linux/forwarded_gate.py` 在 Linux 客户端侧驱动 Windows VM 上的 `wfprobe` suite，
自动 prepare/cleanup，按阶段跑 forwarded 数据面。CI 用法与本地相同，只是把三条远程命令
换成 CI 的 SSH/WinRM 包装。

## 前置

- Windows VM 已安装控制面 ABI v8 的 `wfprobe.sys`，服务 RUNNING；`wfprobe.exe` 已构建
  （B6c 验收时为 ABI v7）。
- Linux 侧能访问 VM（默认 `192.168.100.2`），且原始目的地别名 `.99` 由门禁自己创建/删除。
- 远端三个命令（可由环境变量提供）：

```bash
export WF_GATE_RUN='windows-vm fast run'    # 同步执行 PowerShell
export WF_GATE_POLL='windows-vm b6ctl run'  # 另一个会话，用于轮询输出
export WF_GATE_UP='windows-vm up2 up'       # 上传本地文件到 Windows
python3 tools/linux/forwarded_gate.py
```

CI 中把三个变量换成等价的 `ssh`/`winrm` 包装；`--run-cmd/--poll-cmd/--up-cmd` 也可显式传。
门禁**禁止**任何阶段传 `--no-verify`；脚本会直接报错。

## 覆盖

| 阶段 | suite | 客户端 |
|---|---|---|
| `tcp-core` | forwarded-tcp core | `forwarded_tcp_client.py`（verify） |
| `udp-core` | forwarded-udp core | `forwarded_udp_client.py`（verify） |
| `fragment-ipv4` | forwarded-udp fragment | `raw_fragment_sender.py` 9×IPv4 |
| `permit-first` | fragment + permit-first 端口 | raw sender |
| `host-same-remote` | fragment-host | raw sender → 40600 |
| `reject` | B6a 产品 harness + fake SOCKS5 | UDP client → report |

`reject` 需要 `tools/linux/forwarded-udp-reject.json`；
可用 `--skip-reject` 跳过。

## IPv6 阶段（`--include-v6`）

默认门禁只跑 IPv4；`--include-v6` 追加下面五个 native IPv6 阶段：

| 阶段 | suite | 客户端 |
|---|---|---|
| `tcp-core6` | forwarded-tcp core `--no-alias` | `forwarded_tcp_client.py` → `target%eth0:40250`，64 KiB |
| `tcp-upstreams6` | forwarded-tcp upstreams `--no-alias` | 顺序连 40250/40251，断言两个假 SOCKS5 各一次 `atyp=4` |
| `udp-core6` | forwarded-udp core `--no-alias` | `forwarded_udp_client.py` 10 datagram，源地址必须是原始目的 |
| `udp-socks5-6` | forwarded-udp socks5 `--no-alias` | 两个目的各一次 UDP ASSOCIATE |
| `tcp-multi6` | forwarded-tcp multi `--no-alias` | `forwarded_tcp_b6b.py`：Internal/External 跨入口 + 同入口两 L，4 条连接 |

前置条件与地址：

- Windows 侧由 prepare 脚本 `-WithIpv6` 添加 Internal 第二 L `fe80::98`、Internal
  target `fe80::99`、External target `fe80::199`，并只读发现 Internal/External 的现有
  链路本地 L；地址进入 `Preferred` 后才开始测。Internal 的 `ms_tcpip6` 必须启用，
  否则脚本明确失败（不自动修改绑定）。
- 客户端只使用 Linux eth0/eth1 现有链路本地地址（`ip -6 -o addr show dev <iface>
  scope link`），不需要 root，也不改地址。target 通过 `%eth0` / `%eth1` 指定 scope。
- target 与 L 必须是不同地址：回程 SNAT 若写错成 L，客户端会直接看到错误源地址。
- prepare 打印 `B6C-V6-READY internal_l1=... internal_l2=... internal_target=...
  external_l1=... external_target=...`；门禁用输出值，不写死地址。
- `finally` 始终执行 `prepare -Remove`，删除新增 v6 地址与 `B6C-ENV-V6-*` 防火墙规则；
  `run.log` 会记录地址清单、`cleanup.log` 与清理后的残留检查。
- 客户端失败时门禁不提前杀 suite：suite 走完自己的等待窗口、恢复空 forwarded 表与
  disabled config 后再清理，并保存该阶段的 `*-suite.log`；只有等待本身超时才终止本地
  包装进程。

手工清理（脚本中断时）：

```powershell
netsh interface ipv6 delete address "Internal" fe80::98
netsh interface ipv6 delete address "Internal" fe80::99
netsh interface ipv6 delete address "External" fe80::199
Get-NetFirewallRule -DisplayName "B6C-ENV-*" | Remove-NetFirewallRule
```

`--include-v6` 不能与 `--no-prepare` 组合：地址必须由 prepare 创建并回报。

## 未自动化（明确）

- **fail 阶段**：B6c 验收时 ABI v7 下 config disabled 时 WFP filter 不再进入 classify，suite
  的 `no_config` 断言不成立；当前控制面 v8 未复验。`--include-fail` 保留为诊断入口，当前
  不作为门禁通过条件。这是 B6c 发现，已记录，未改数据面。
- multi-ingress / hotswap / noingress / StateCommand 的 IPv4 变体：Windows-only wfprobe
  阶段，B6b 已单独验证；v6 只有上表的 `tcp-multi6`，UDP 多 L、IPv6 热替换仍不在门禁内。
- IPv6 9 分片：需要客户端 IPv6 地址与 MTU 调整，见 `tools/linux/ipv6_mtu_fragment.py`，
  手工阶段（`--include-v6` 的五个阶段是 happy path，不含分片闸）。
- A5 成本：`tools/linux/a5_forwarded_cost.py` 是测量脚本，不是 pass/fail 门禁。

## 产物

默认写到 `verification/forwarded-gate/`：`run.log`、每阶段 `*-suite.log`/`*-client.log`、
`reject-*`、`prepare.log`、`cleanup.log`。原始 suite 输出在 Windows `C:\workspace\fg-*.out`。
正常结束（包括失败路径）都会执行 `prepare -Remove`，删除别名、IPv6 测试地址与防火墙规则；
清理命令返回非零会让门禁失败；`--include-v6` 还会检查 IPv6 地址与 `B6C-ENV-*` 规则残留，
有残留同样失败，并在 `run.log` 记录 v6 地址清单与 `cleanup check` 结果。
`--keep-alias` 只为兼容旧命令行保留，不再跳过清理。
